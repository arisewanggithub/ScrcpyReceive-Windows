using ScrcpyReceive.Core;
using System.Xml.Linq;
using Xunit;

namespace ScrcpyReceive.Tests;

public sealed class PlaybackShortcutsTests
{
    [Theory]
    [InlineData(ConsoleKey.Enter)]
    [InlineData(ConsoleKey.F)]
    public void FullscreenRequiresActuallyPresentedVideo(ConsoleKey key)
    {
        Assert.Equal(FullscreenAction.None, PlaybackShortcuts.Evaluate(key, false, false));
        Assert.Equal(FullscreenAction.Enter, PlaybackShortcuts.Evaluate(key, true, false));
        Assert.Equal(FullscreenAction.Exit, PlaybackShortcuts.Evaluate(key, true, true));
    }

    [Theory]
    [InlineData(ConsoleKey.Enter)]
    [InlineData(ConsoleKey.F)]
    public void EditingAndModifiedKeysAreNotIntercepted(ConsoleKey key)
    {
        Assert.Equal(FullscreenAction.None, PlaybackShortcuts.Evaluate(key, true, false, editingText: true));
        Assert.Equal(FullscreenAction.None, PlaybackShortcuts.Evaluate(key, true, false, modified: true));
    }

    [Fact]
    public void EscapeAlwaysExitsExistingFullscreenButDoesNotEnterIt()
    {
        Assert.Equal(FullscreenAction.Exit, PlaybackShortcuts.Evaluate(ConsoleKey.Escape, true, true));
        Assert.Equal(FullscreenAction.Exit, PlaybackShortcuts.Evaluate(ConsoleKey.Escape, false, true));
        Assert.Equal(FullscreenAction.None, PlaybackShortcuts.Evaluate(ConsoleKey.Escape, true, false));
        Assert.Equal(FullscreenAction.None, PlaybackShortcuts.Evaluate(ConsoleKey.Spacebar, true, false));
        Assert.Equal(FullscreenAction.None, PlaybackShortcuts.Evaluate(null, true, false));
    }

    [Fact]
    public void PassiveReceiverUiDoesNotExposeCommandButtonsOrEditableReceiverName()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "MainWindow.xaml"));
        Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName is "Button" or "TextBox");
        Assert.Contains(document.Descendants(), element => element.Attribute("PreviewKeyDown")?.Value == "Root_KeyDown");
        Assert.Contains(document.Descendants(), element => element.Name.LocalName == "ContentControl" &&
            element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value == "PlayerInput");
        var waitingPage = document.Descendants().Single(element =>
            element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value == "PageScroll");
        Assert.DoesNotContain(waitingPage.Descendants(), element => element.Name.LocalName == "SwapChainPanel");
    }

    [Fact]
    public void LandingPageOmitsTechnicalPanelsAndShowsFooterVersion()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "MainWindow.xaml"));
        var name = XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml");
        Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName is "Expander" or "ListView");
        var version = Assert.Single(document.Descendants(), element => element.Attribute(name)?.Value == "VersionText");
        Assert.Equal("2", version.Parent?.Attribute("Grid.Row")?.Value);
        Assert.Null(version.Attribute("Text"));
        Assert.Equal("True", document.Descendants().Single(element => element.Attribute(name)?.Value == "AddressesText")
            .Attribute("IsTextSelectionEnabled")?.Value);
        Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName == "Viewbox");
        Assert.Contains(document.Descendants(), element => element.Attribute(name)?.Value == "AddressRows");
        Assert.Equal("Wrap", document.Descendants().Single(element => element.Attribute(name)?.Value == "AddressesText")
            .Attribute("TextWrapping")?.Value);
        var layout = document.Descendants().Single(element => element.Attribute(name)?.Value == "LayoutRoot");
        Assert.Equal("Stretch", layout.Attribute("HorizontalAlignment")?.Value);
        Assert.Equal("Stretch", layout.Attribute("VerticalAlignment")?.Value);
        Assert.Null(layout.Attribute("MinHeight"));
        var scroll = document.Descendants().Single(element => element.Attribute(name)?.Value == "PageScroll");
        Assert.Equal("Stretch", scroll.Attribute("VerticalContentAlignment")?.Value);
        Assert.DoesNotContain(document.Descendants(), element => element.Attribute(name)?.Value == "HomeCard");
        Assert.Equal("1", document.Descendants().Single(element => element.Attribute(name)?.Value == "HomeContent")
            .Attribute("Grid.Row")?.Value);
        Assert.Single(document.Descendants(), element => element.Name.LocalName == "InfoBar");
        var key = XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml");
        Assert.Contains(document.Descendants(), element => element.Attribute(key)?.Value == "Light");
        Assert.Contains(document.Descendants(), element => element.Attribute(key)?.Value == "Default");
        Assert.Contains(document.Descendants(), element => element.Attribute(key)?.Value == "HighContrast");
    }
}
