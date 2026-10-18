using System.Windows.Input;
using PcCleaner.Core.Controls;
using Xunit;

namespace PcCleaner.Tests.Core;

/// <summary>
/// The bar is XAML (a card with "Отмечено: N", the page's buttons and "Снять отметки"); nothing here renders it.
/// These pin the contract the pages bind to: the three properties, and that an unbound bar counts 0, which the
/// XAML turns into Collapsed.
/// </summary>
public class BulkActionBarTests
{
    [Fact]
    public void CheckedCount_IsAnIntThatStartsAtZero_SoAnUnboundBarStaysCollapsed()
    {
        Assert.Equal("CheckedCount", BulkActionBar.CheckedCountProperty.Name);
        Assert.Equal(typeof(int), BulkActionBar.CheckedCountProperty.PropertyType);
        Assert.Equal(typeof(BulkActionBar), BulkActionBar.CheckedCountProperty.OwnerType);
        Assert.Equal(0, BulkActionBar.CheckedCountProperty.DefaultMetadata.DefaultValue);
    }

    [Fact]
    public void ActionsContent_IsAnObjectSlotThatStartsEmpty()
    {
        Assert.Equal("ActionsContent", BulkActionBar.ActionsContentProperty.Name);
        Assert.Equal(typeof(object), BulkActionBar.ActionsContentProperty.PropertyType);
        Assert.Equal(typeof(BulkActionBar), BulkActionBar.ActionsContentProperty.OwnerType);
        Assert.Null(BulkActionBar.ActionsContentProperty.DefaultMetadata.DefaultValue);
    }

    [Fact]
    public void ClearCommand_IsAnICommandThatStartsEmpty()
    {
        Assert.Equal("ClearCommand", BulkActionBar.ClearCommandProperty.Name);
        Assert.Equal(typeof(ICommand), BulkActionBar.ClearCommandProperty.PropertyType);
        Assert.Equal(typeof(BulkActionBar), BulkActionBar.ClearCommandProperty.OwnerType);
        Assert.Null(BulkActionBar.ClearCommandProperty.DefaultMetadata.DefaultValue);
    }
}
