using Xunit;

namespace PcCleaner.Tests.Settings;

/// <summary>
/// Settings tests that save an AppSettings instance (AddTrusted/RemoveTrusted, SetModuleValue) write the same
/// settings.json in the temp test data folder, so they run one after another.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SettingsFileCollection
{
    public const string Name = "SettingsFile";
}
