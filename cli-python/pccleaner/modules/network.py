"""Анализ сети: интерфейсы, скорость, задержки, DNS, открытые порты, соединения процессов, hosts, прокси."""
from __future__ import annotations

import re
import socket
import statistics
import time
from collections import Counter
from pathlib import Path

import psutil

from ..core import IS_WINDOWS, ModuleResult, human_size, powershell, read_text, run

# Порты, которые опасно держать открытыми наружу.
RISKY_LISTEN = {
    21: "FTP (пароли передаются открытым текстом)",
    23: "Telnet (небезопасный удалённый доступ)",
    135: "RPC Windows",
    139: "NetBIOS",
    445: "SMB — общие папки (через него распространялся WannaCry)",
    3389: "Удалённый рабочий стол (RDP) — частая цель подбора паролей",
    5900: "VNC — удалённый доступ",
    5985: "WinRM — удалённое управление",
    3306: "MySQL",
    5432: "PostgreSQL",
    6379: "Redis",
    27017: "MongoDB",
}

# Эти порты слушает любая Windows по умолчанию — не пугаем ими пользователя.
WINDOWS_DEFAULT_PORTS = {135, 139, 445}

# Удалённые порты, характерные для вредоносного ПО и майнеров.
SUSPICIOUS_REMOTE = {
    4444: "Metasploit/бэкдоры", 1337: "бэкдоры", 31337: "бэкдоры", 6667: "IRC-ботнеты",
    6666: "IRC-ботнеты", 3333: "майнинг-пул", 5555: "майнинг-пул/ADB", 7777: "майнинг-пул",
    14444: "майнинг-пул Monero", 14433: "майнинг-пул Monero", 45700: "майнинг-пул",
    9050: "Tor", 9150: "Tor Browser",
}

# Домены, перенаправление которых в hosts — признак вируса или блокировки обновлений.
PROTECTED_DOMAINS = ("google.", "microsoft.", "windowsupdate", "kaspersky", "drweb", "eset",
                     "avast", "virustotal", "vk.com", "yandex.", "mail.ru", "sberbank", "tinkoff",
                     "gosuslugi", "paypal", "facebook", "steam")

LATENCY_TARGETS = [("Cloudflare DNS", "1.1.1.1", 443), ("Google DNS", "8.8.8.8", 443),
                   ("Яндекс", "ya.ru", 443), ("Microsoft", "www.microsoft.com", 443)]


def tcp_latency(host: str, port: int, tries: int = 3, timeout: float = 2.0) -> float | None:
    """Время TCP-подключения в мс (медиана), None — если хост недоступен."""
    samples = []
    for _ in range(tries):
        t0 = time.perf_counter()
        try:
            with socket.create_connection((host, port), timeout=timeout):
                samples.append((time.perf_counter() - t0) * 1000)
        except OSError:
            continue
    return round(statistics.median(samples), 1) if samples else None


def dns_time(name: str = "example.com") -> float | None:
    t0 = time.perf_counter()
    try:
        socket.getaddrinfo(name, 443)
    except OSError:
        return None
    return round((time.perf_counter() - t0) * 1000, 1)


def throughput(seconds: float = 2.0) -> dict[str, tuple[float, float]]:
    """Текущая скорость приёма/передачи по интерфейсам, байт/с."""
    a = psutil.net_io_counters(pernic=True)
    time.sleep(seconds)
    b = psutil.net_io_counters(pernic=True)
    out = {}
    for nic, cb in b.items():
        ca = a.get(nic)
        if ca:
            out[nic] = ((cb.bytes_recv - ca.bytes_recv) / seconds, (cb.bytes_sent - ca.bytes_sent) / seconds)
    return out


def dns_servers() -> list[str]:
    if IS_WINDOWS:
        out = powershell("(Get-DnsClientServerAddress -AddressFamily IPv4 | "
                         "Where-Object {$_.ServerAddresses}).ServerAddresses | Sort-Object -Unique")
        return [x.strip() for x in out.splitlines() if x.strip()]
    return re.findall(r"^nameserver\s+(\S+)", read_text("/etc/resolv.conf"), re.M)


def hosts_entries() -> list[tuple[str, str]]:
    path = Path(r"C:\Windows\System32\drivers\etc\hosts") if IS_WINDOWS else Path("/etc/hosts")
    out = []
    for line in read_text(str(path)).splitlines():
        line = line.split("#", 1)[0].strip()
        if not line:
            continue
        parts = line.split()
        for host in parts[1:]:
            out.append((parts[0], host.lower()))
    return out


def proxy_settings() -> str:
    if IS_WINDOWS:
        try:
            import winreg  # type: ignore
            k = winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                               r"Software\Microsoft\Windows\CurrentVersion\Internet Settings")
            enabled = winreg.QueryValueEx(k, "ProxyEnable")[0]
            if enabled:
                return winreg.QueryValueEx(k, "ProxyServer")[0]
            try:
                return "PAC: " + winreg.QueryValueEx(k, "AutoConfigURL")[0]
            except OSError:
                return ""
        except OSError:
            return ""
    import os
    return os.environ.get("http_proxy") or os.environ.get("HTTP_PROXY") or ""


def wifi_info() -> dict:
    if IS_WINDOWS:
        out = run(["netsh", "wlan", "show", "interfaces"])
        info = {}
        for key, rx in (("ssid", r"^\s*SSID\s*:\s*(.+)$"), ("signal", r"(?:Signal|Сигнал)\s*:\s*(\d+)%"),
                        ("band", r"(?:Band|Диапазон)\s*:\s*(.+)$"),
                        ("rate", r"(?:Receive rate|Скорость приема).*?:\s*([\d.,]+)")):
            m = re.search(rx, out, re.M | re.I)
            if m:
                info[key] = m.group(1).strip()
        return info
    out = run(["nmcli", "-t", "-f", "ACTIVE,SSID,SIGNAL,FREQ", "dev", "wifi"])
    for line in out.splitlines():
        if line.startswith("yes:"):
            _, ssid, sig, freq = (line.split(":") + ["", "", ""])[:4]
            return {"ssid": ssid, "signal": sig, "band": freq}
    return {}


def _is_public(ip: str) -> bool:
    import ipaddress
    try:
        a = ipaddress.ip_address(ip)
        return not (a.is_private or a.is_loopback or a.is_link_local or a.is_multicast or a.is_unspecified)
    except ValueError:
        return False


def scan(sample_seconds: float = 2.0, latency: bool = True) -> ModuleResult:
    res = ModuleResult("network", "Сеть")
    s: dict = {}

    # Интерфейсы
    stats = psutil.net_if_stats()
    addrs = psutil.net_if_addrs()
    ifaces = []
    for nic, st in stats.items():
        if not st.isup or nic.lower().startswith(("lo", "loopback")):
            continue
        ips = [a.address for a in addrs.get(nic, []) if a.family == socket.AF_INET]
        ifaces.append({"name": nic, "speed_mbps": st.speed, "mtu": st.mtu, "ips": ips})
        if 0 < st.speed < 1000 and not re.search(r"wi-?fi|wlan|wireless|беспровод", nic, re.I):
            res.add("Сеть", f"Адаптер {nic} работает на {st.speed} Мбит/с", severity="low",
                    recommendation="Гигабитная карта на 100 Мбит/с — обычно плохой кабель (нужен Cat5e/Cat6, все 8 жил) "
                                   "или порт роутера. Проверьте кабель.")
    s["interfaces"] = ifaces

    # Трафик
    tp = throughput(sample_seconds)
    s["throughput"] = {k: {"down": human_size(v[0]) + "/с", "up": human_size(v[1]) + "/с"}
                       for k, v in tp.items() if v[0] or v[1]}
    total_up = sum(v[1] for v in tp.values())
    if total_up > 2 * 1024 ** 2:
        res.add("Сеть", f"Высокая исходящая активность: {human_size(total_up)}/с", severity="medium",
                detail="Что-то активно отправляет данные в сеть.",
                recommendation="Если вы ничего не загружаете (облако, торрент, стрим) — проверьте процессы "
                               "в таблице соединений ниже.")

    # Задержки и DNS
    if latency:
        lat = {name: tcp_latency(host, port) for name, host, port in LATENCY_TARGETS}
        s["latency_ms"] = lat
        ok = [v for v in lat.values() if v is not None]
        if not ok:
            res.add("Сеть", "Нет доступа в интернет", severity="high",
                    recommendation="Проверьте кабель/Wi-Fi, перезагрузите роутер; "
                                   "в Windows: netsh winsock reset и ipconfig /flushdns.")
        elif statistics.median(ok) > 100:
            res.add("Сеть", f"Высокая задержка до интернета: ~{statistics.median(ok):.0f} мс", severity="low",
                    recommendation="Подключитесь по кабелю вместо Wi-Fi, закройте загрузки и торренты, "
                                   "перезагрузите роутер.")
        d = dns_time()
        s["DNS-запрос"] = f"{d} мс" if d else "ошибка"
        if d and d > 150:
            res.add("Сеть", f"Медленный DNS: {d:.0f} мс", severity="low",
                    recommendation="Попробуйте DNS 1.1.1.1 / 8.8.8.8 / 77.88.8.8 (Яндекс) в настройках адаптера или роутера.")
    s["dns_servers"] = dns_servers()

    # Wi-Fi
    wifi = wifi_info()
    if wifi:
        s["wifi"] = wifi
        try:
            sig = int(wifi.get("signal", "100"))
        except ValueError:
            sig = 100
        if sig < 50:
            res.add("Сеть", f"Слабый сигнал Wi-Fi: {sig}%", severity="low",
                    recommendation="Переставьте роутер выше и ближе, используйте 5 ГГц или подключитесь кабелем.")
        if "2.4" in wifi.get("band", "") or wifi.get("band", "").startswith("24"):
            res.add("Сеть", "Wi-Fi работает в диапазоне 2.4 ГГц", severity="info",
                    recommendation="Если роутер поддерживает 5 ГГц — подключитесь к нему: быстрее и меньше помех.")

    # Соединения и открытые порты
    try:
        conns = psutil.net_connections(kind="inet")
        s["доступ к соединениям"] = "полный"
    except (psutil.AccessDenied, PermissionError):
        conns = []
        s["доступ к соединениям"] = "нет прав — запустите от администратора"
    names: dict[int, str] = {}

    def pname(pid):
        if pid is None:
            return "?"
        if pid not in names:
            try:
                names[pid] = psutil.Process(pid).name()
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                names[pid] = f"PID {pid}"
        return names[pid]

    listening, established = [], []
    for c in conns:
        if c.status == psutil.CONN_LISTEN and c.laddr:
            listening.append({"port": c.laddr.port, "ip": c.laddr.ip, "process": pname(c.pid)})
        elif c.status == psutil.CONN_ESTABLISHED and c.raddr:
            established.append({"process": pname(c.pid), "remote": f"{c.raddr.ip}:{c.raddr.port}",
                                "rip": c.raddr.ip, "rport": c.raddr.port})
    uniq = {(x["port"], x["ip"], x["process"]): x for x in listening}
    s["listening"] = sorted(uniq.values(), key=lambda x: x["port"])
    s["established"] = established
    s["connections_by_process"] = Counter(e["process"] for e in established).most_common(15)

    seen = set()
    for x in s["listening"]:
        if x["port"] in RISKY_LISTEN and x["ip"] in ("0.0.0.0", "::") and x["port"] not in seen:
            seen.add(x["port"])
            if IS_WINDOWS and x["port"] in WINDOWS_DEFAULT_PORTS:
                res.add("Сеть", f"Открыт стандартный порт Windows {x['port']}", severity="info",
                        detail=RISKY_LISTEN[x["port"]] + ". Есть на любой Windows.",
                        recommendation="Ничего делать не нужно, но не пробрасывайте его на роутере и выбирайте "
                                       "профиль сети «Общедоступная» в кафе/гостиницах.")
                continue
            res.add("Сеть", f"Открыт порт {x['port']} ({x['process']})", severity="medium",
                    detail=RISKY_LISTEN[x["port"]],
                    recommendation="Если служба не нужна — отключите её; иначе ограничьте доступ брандмауэром "
                                   "и не пробрасывайте порт на роутере.")
    for e in established:
        if e["rport"] in SUSPICIOUS_REMOTE and _is_public(e["rip"]):
            res.add("Сеть", f"{e['process']} подключён к {e['remote']}", severity="high",
                    detail=f"Порт {e['rport']}: {SUSPICIOUS_REMOTE[e['rport']]}",
                    recommendation="Проверьте процесс антивирусом и IP на virustotal.com / abuseipdb.com.")

    # hosts и прокси
    hijacked = [(ip, h) for ip, h in hosts_entries()
                if any(d in h for d in PROTECTED_DOMAINS) and ip not in ("127.0.0.1", "0.0.0.0", "::1")]
    blocked = [(ip, h) for ip, h in hosts_entries()
               if any(d in h for d in ("kaspersky", "drweb", "eset", "avast", "virustotal", "windowsupdate"))
               and ip in ("127.0.0.1", "0.0.0.0")]
    for ip, h in hijacked[:10]:
        res.add("Сеть", f"Файл hosts перенаправляет {h} на {ip}", severity="high",
                recommendation="Типичный признак вируса (фишинг). Удалите строку из файла hosts.")
    if blocked:
        res.add("Сеть", f"В hosts заблокированы сайты антивирусов/обновлений ({len(blocked)})", severity="high",
                detail=", ".join(h for _, h in blocked[:5]),
                recommendation="Вирусы так мешают своему удалению. Очистите эти строки и проверьте ПК антивирусом.")
    proxy = proxy_settings()
    s["прокси"] = proxy or "нет"
    if proxy:
        res.add("Сеть", f"Включён системный прокси: {proxy}", severity="medium",
                recommendation="Если вы его не настраивали — это может быть рекламное ПО, перехватывающее трафик. "
                               "Отключите: Параметры → Сеть → Прокси.")
    res.summary = s
    return res
