import os
import time
from pathlib import Path

import pytest

from pccleaner import advisor, report
from pccleaner.core import Finding, ModuleResult, human_size
from pccleaner.modules import apps, junk, network, processes


def test_human_size():
    assert human_size(512) == "512 Б"
    assert human_size(1536) == "1.5 КБ"
    assert human_size(3 * 1024 ** 3) == "3.0 ГБ"


def test_finding_rejects_bad_severity():
    with pytest.raises(ValueError):
        Finding("x", "y", severity="critical")


@pytest.mark.parametrize("name,exe,needle", [
    ("svch0st.exe", "", "похоже на системный"),
    ("invoice.pdf.exe", "", "двойное расширение"),
    ("xmrig", "/usr/bin/xmrig", "майнер"),
    ("helper", r"C:\Users\me\AppData\Local\Temp\helper.exe" if os.name == "nt" else "/tmp/helper", "временной"),
    ("a9f3k2l1qz8x7w", "", "случайное"),
])
def test_suspicion_reasons_flags(name, exe, needle):
    assert any(needle in r for r in processes.suspicion_reasons(name, exe))


@pytest.mark.parametrize("name", ["svchost.exe", "chrome.exe", "python3", "explorer.exe", "code",
                                  "sihost.exe", "csrss.exe", "WindowsAzureGuestAgent.exe",
                                  "ServiceFabricLocalClusterManager.exe", "msedgewebview2.exe"])
def test_suspicion_reasons_clean(name):
    assert processes.suspicion_reasons(name, "") == []


def test_idle_process_not_flagged():
    assert processes.suspicion_reasons("System Idle Process", "", cpu=99) == []


def test_high_cpu_flag():
    assert any("CPU" in r for r in processes.suspicion_reasons("game.exe", "", cpu=95))


def test_match_pup():
    assert apps.match_pup("Amigo") is not None
    assert apps.match_pup("uTorrent") is not None
    assert apps.match_pup("7-Zip 23.01") is None


def _fake_target(monkeypatch, tmp_path):
    t = junk.Target("fake", "Тест", tmp_path)
    monkeypatch.setattr(junk, "targets", lambda: [t, junk.Target("manual", "Ручн", tmp_path, safe=False)])
    old = tmp_path / "old.tmp"
    new = tmp_path / "sub" / "new.tmp"
    new.parent.mkdir()
    old.write_bytes(b"x" * 100)
    new.write_bytes(b"y" * 50)
    past = time.time() - 48 * 3600
    os.utime(old, (past, past))
    return old, new


def test_clean_dry_run_deletes_nothing(monkeypatch, tmp_path):
    old, new = _fake_target(monkeypatch, tmp_path)
    rep = junk.clean(min_age_hours=24, apply=False)
    assert rep["deleted_files"] == 1 and rep["freed"] == 100
    assert old.exists() and new.exists()


def test_clean_apply_respects_age_and_safe_flag(monkeypatch, tmp_path):
    old, new = _fake_target(monkeypatch, tmp_path)
    rep = junk.clean(min_age_hours=24, apply=True)
    assert not old.exists() and new.exists()
    assert [t["key"] for t in rep["targets"]] == ["fake"]  # небезопасная цель не тронута


def test_clean_does_not_follow_symlinks(monkeypatch, tmp_path):
    outside = tmp_path.parent / f"{tmp_path.name}-outside"
    outside.mkdir()
    keep = outside / "keep.txt"
    keep.write_text("важно", encoding="utf-8")
    past = time.time() - 48 * 3600
    os.utime(keep, (past, past))
    junkdir = tmp_path / "junk"
    junkdir.mkdir()
    try:
        (junkdir / "link").symlink_to(outside, target_is_directory=True)
    except OSError:
        pytest.skip("нет прав на создание симлинков")
    monkeypatch.setattr(junk, "targets", lambda: [junk.Target("fake", "Тест", junkdir)])
    junk.clean(apply=True)
    assert keep.exists()


def test_hosts_parsing(monkeypatch):
    monkeypatch.setattr(network, "read_text", lambda _: "127.0.0.1 localhost\n# c\n1.2.3.4 vk.com www.vk.com # x\n")
    assert network.hosts_entries() == [("127.0.0.1", "localhost"), ("1.2.3.4", "vk.com"), ("1.2.3.4", "www.vk.com")]


def test_is_public():
    assert network._is_public("8.8.8.8")
    assert not network._is_public("192.168.1.1")
    assert not network._is_public("127.0.0.1")


def test_score_and_html_escaping():
    r = ModuleResult("x", "X")
    r.add("Тест", "<script>alert(1)</script>", severity="high")
    r.add("Тест", "ok", severity="low")
    assert advisor.score([r]) == 83
    html = report.to_html([r])
    assert "<script>alert" not in html and "&lt;script&gt;" in html
    assert '"score": 83' in report.to_json([r])


def test_cli_scan_smoke(tmp_path):
    from pccleaner.cli import main
    out = tmp_path / "r.html"
    assert main(["scan", "--only", "processes,junk", "--quick", "--html", str(out)]) == 0
    assert out.read_text(encoding="utf-8").startswith("<!doctype html>")


def test_hardware_rules_on_fake_windows_data(monkeypatch):
    from pccleaner.modules import hardware
    fake = {
        "cpu": {"Name": "AMD Ryzen 5 5600", "VirtualizationFirmwareEnabled": False},
        "ram": [{"Capacity": 8 * 1024 ** 3, "Speed": 3200, "ConfiguredClockSpeed": 2133, "SMBIOSMemoryType": 26}],
        "slots": 4,
        "gpu": {"Name": "NVIDIA GeForce RTX 3060", "DriverDate": "2022-01-01"},
        "disks": [{"FriendlyName": "WDC", "MediaType": "HDD", "HealthStatus": "Warning", "Size": 10 ** 12}],
        "bios": {"Manufacturer": "AMI", "SMBIOSBIOSVersion": "F2", "ReleaseDate": "2020-05-01"},
        "fwtype": 1, "secureboot": 0, "tpm": None, "fastboot": 1, "hags": 1,
        "power": "GUID: x  (Power saver)", "os": {"LastBoot": "2020-01-01 10:00"},
    }
    monkeypatch.setattr(hardware, "IS_WINDOWS", True)
    monkeypatch.setattr(hardware, "IS_LINUX", False)
    monkeypatch.setattr(hardware, "_win_info", lambda: fake)
    monkeypatch.setattr(hardware.psutil, "sensors_battery", lambda: None, raising=False)
    titles = " | ".join(f.title for f in hardware.scan().findings)
    for needle in ("BIOS не обновлялся", "Legacy", "Secure Boot", "TPM", "базовой частоте 2133",
                   "один модуль", "состояние: Warning", "HDD", "Драйвер видеокарты", "энергосбережения",
                   "Быстрый запуск", "HAGS", "не перезагружался"):
        assert needle in titles, needle
