using PcCleaner.Core.Localization;

namespace PcCleaner.Core.Updates;

/// <summary>The corner of the window's bottom line: the version and what the updater knows (pure, unit-tested).</summary>
public static class UpdateFooter
{
    public static string Text(UpdateState state, UpdateManifest? manifest, Version current)
    {
        var version = "v" + UpdateManifest.Normalize(current).ToString(3);
        var found = manifest is null ? null : UpdateManifest.Normalize(manifest.Version).ToString(3);
        var tail = state switch
        {
            UpdateState.Checking => T._("проверка…"),
            UpdateState.UpToDate => T._("последняя версия"),
            UpdateState.Downloading when found is not null => T.F("скачивается {0}", found),
            UpdateState.Ready when found is not null => T.F("скачана {0}", found),
            UpdateState.ManualRequired when found is not null => T.F("есть версия {0}", found),
            UpdateState.Updated => T._("обновлена"),
            _ => null,
        };
        return tail is null ? version : version + " · " + tail;
    }
}
