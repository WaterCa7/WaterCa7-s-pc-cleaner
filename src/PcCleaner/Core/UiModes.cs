using System.Text.Json;
using System.Text.Json.Serialization;
using PcCleaner.Core.Localization;

namespace PcCleaner.Core;

/// <summary>
/// Простой: only the pages most people need (Главная, Очистка, Программы, Настройки). Опытный: every page, as it always was.
/// Saved in settings.json as "simple" / "expert" (<see cref="AppSettings.UiMode"/>, null = not chosen yet).
/// </summary>
public enum UiMode
{
    Simple,
    Expert,
}

/// <summary>
/// What each mode shows, and the one place that changes it. The shell (<see cref="MainViewModel"/>) listens to
/// <see cref="Changed"/>: the sidebar and the Dashboard follow at once, no restart.
/// </summary>
public static class UiModes
{
    /// <summary>The pages of the simple mode, in the order of the sidebar (PageViewModel.Key). The other pages are hidden there.</summary>
    public static IReadOnlyList<string> SimplePageKeys { get; } = new[] { "Dashboard", "Cleaner", "Programs", "Settings" };

    /// <summary>
    /// Raised after a mode was chosen with <see cref="Choose"/>, on the caller's thread (the UI thread). Tests that raise it
    /// run in the sequential "Modes" collection, so that two threads never refresh one shell.
    /// </summary>
    public static event Action? Changed;

    /// <summary>
    /// The mode that is in force: the chosen one, or the expert mode while nothing is chosen (every page stays where it was
    /// until the user picks the simple mode).
    /// </summary>
    public static UiMode Effective(UiMode? chosen) => chosen ?? UiMode.Expert;

    public static UiMode Of(AppSettings settings) => Effective(settings.UiMode);

    /// <summary>Is the page with this <see cref="PageViewModel.Key"/> shown in the sidebar in this mode?</summary>
    public static bool IsVisible(UiMode mode, string pageKey)
        => mode == UiMode.Expert ||
           SimplePageKeys.Any(k => string.Equals(k, pageKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>"simple" / "expert" (any case, spaces around are fine); anything else is null.</summary>
    public static UiMode? TryParse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "simple" => UiMode.Simple,
        "expert" => UiMode.Expert,
        _ => null,
    };

    /// <summary>The name used in settings.json and on the command line.</summary>
    internal static string Name(UiMode mode) => mode == UiMode.Simple ? "simple" : "expert";

    /// <summary>
    /// The user picked a mode (Настройки, the question of a hidden page, later the first-run screen): stores it, saves, logs and
    /// tells the shell. False — and nothing happens — when this very mode is already chosen. Choosing the expert mode when
    /// nothing was chosen is a choice and is saved.
    /// </summary>
    public static bool Choose(AppSettings settings, UiMode mode, Action<AppSettings> save)
    {
        if (settings.UiMode == mode)
            return false;
        settings.UiMode = mode;
        save(settings);
        Log.Info(mode == UiMode.Simple ? T._("Настройки: режим — простой") : T._("Настройки: режим — опытный"));
        Changed?.Invoke();
        return true;
    }
}

/// <summary>
/// "simple" / "expert" in settings.json. A hand-edited value of any other kind (a typo, a number, an object) reads as
/// "not chosen" instead of failing the whole file, which would reset every other setting.
/// </summary>
internal sealed class UiModeJsonConverter : JsonConverter<UiMode?>
{
    public override bool HandleNull => true;

    public override UiMode? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return UiModes.TryParse(reader.GetString());
            case JsonTokenType.StartObject or JsonTokenType.StartArray:
                reader.Skip();
                return null;
            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, UiMode? value, JsonSerializerOptions options)
    {
        if (value is { } mode && Enum.IsDefined(mode))
            writer.WriteStringValue(UiModes.Name(mode));
        else
            writer.WriteNullValue();
    }
}
