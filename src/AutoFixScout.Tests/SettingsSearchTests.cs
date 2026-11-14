using AutoFixScout.Areas;
using Xunit;

namespace AutoFixScout.Tests;

public class SettingsSearchTests
{
    [Theory]
    [InlineData("make it dark", "Theme")]
    [InlineData("The TEXT SIZE is small", "Text size")]
    [InlineData("I want bigger text", "Text size")]
    [InlineData("too much blur", "Frosted glass")]
    [InlineData("turn off animation", "Reduce motion")]
    [InlineData("new updates?", "Check for updates")]
    [InlineData("join the beta", "Get test versions early")]
    [InlineData("weekly", "Weekly check-up")]
    [InlineData("please clear history.", "Clear history")]
    [InlineData("change the accent", "Accent colour")]
    public void Match_finds_setting(string text, string title) => Assert.Equal(title, SettingsSearch.Match(text));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("my printer is jammed")]
    [InlineData("updated yesterday")]
    [InlineData("highlight")]
    [InlineData("subtheme")]
    public void Match_misses(string text) => Assert.Null(SettingsSearch.Match(text));

    [Fact]
    public void Match_null_is_null() => Assert.Null(SettingsSearch.Match(null));
}
