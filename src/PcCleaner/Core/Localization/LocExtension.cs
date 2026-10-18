using System.Windows.Markup;

namespace PcCleaner.Core.Localization;

/// <summary>
/// A text of the UI in XAML: <c>{loc:T 'Проверить снова'}</c> gives <see cref="T._"/> of it, with
/// <c>xmlns:loc="clr-namespace:PcCleaner.Core.Localization"</c>. Write the text as a quoted literal: LocalizationCatalogTests
/// reads it from the XAML to check en.json. The language is fixed for a run, so the value is read once.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string text) => Text = text;

    /// <summary>The Russian text (the key in en.json).</summary>
    [ConstructorArgument("text")]
    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => T._(Text);
}

/// <summary>
/// The XAML name of <see cref="LocExtension"/>: <c>{loc:T '…'}</c>. XAML looks for "TExtension" before "T", and T itself
/// is the static class of the code.
/// </summary>
public sealed class TExtension : LocExtension
{
    public TExtension()
    {
    }

    public TExtension(string text) : base(text)
    {
    }
}
