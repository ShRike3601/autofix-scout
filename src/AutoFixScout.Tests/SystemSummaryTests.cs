using Xunit;

namespace AutoFixScout.Tests;

public class SystemSummaryTests
{
    [Theory]
    [InlineData("Windows 10 Home Single Language", "24H2", 26100, "Windows 11 Home Single Language 24H2 (build 26100)")]
    [InlineData("Windows 10 Pro", "22H2", 19045, "Windows 10 Pro 22H2 (build 19045)")]
    [InlineData(null, null, 19041, "Windows (build 19041)")]
    public void Windows_NameFollowsTheBuild(string? product, string? version, int build, string expected) =>
        Assert.Equal(expected, SystemSummary.FormatWindows(product, version, build));

    [Theory]
    [InlineData("Intel(R) Core(TM) i7-10750H CPU @ 2.60GHz", "Intel Core i7-10750H")]
    [InlineData("AMD Ryzen 7 5800X 8-Core Processor   ", "AMD Ryzen 7 5800X 8-Core Processor")]
    [InlineData("  ", null)]
    public void Names_AreCleaned(string raw, string? expected) => Assert.Equal(expected, SystemSummary.Clean(raw));

    [Fact]
    public void Graphics_DedicatedFirst() =>
        Assert.Equal("NVIDIA GeForce RTX 2060 + Intel UHD Graphics",
            SystemSummary.GraphicsText(new[] { "Intel UHD Graphics", "NVIDIA GeForce RTX 2060" }));

    [Theory]
    [InlineData(0, 0, "1 min")]
    [InlineData(2, 5, "2 h 5 min")]
    [InlineData(51, 0, "2 d 3 h")]
    public void Uptime_IsShort(int hours, int minutes, string expected) =>
        Assert.Equal(expected, SystemSummary.Uptime(new TimeSpan(hours, minutes, 0)));

    [Fact]
    public void Battery_And_Disk_Text()
    {
        Assert.Equal("80 %, plugged in", SystemSummary.BatteryText(80, true));
        Assert.Null(SystemSummary.BatteryText(255, false));
        Assert.Equal("120 GB free of 512 GB", SystemSummary.DiskText(120_000_000_000, 512_000_000_000));
    }

    [Fact]
    public void Read_OnThisMachine_HasNoPersonalData()
    {
        var lines = SystemSummary.Read();
        Assert.Contains(lines, l => l.Label == "Windows");
        var text = SystemSummary.AsText(lines);
        Assert.DoesNotContain(Environment.UserName, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.MachineName, text, StringComparison.OrdinalIgnoreCase);
    }
}

public class SystemSummaryShortTests
{
    [Theory]
    [InlineData("Windows", "Windows 11 Home Single Language 26H2 (build 26300)", "Windows 11 Home 26H2")]
    [InlineData("Processor", "Intel Core i7-10750H", "Core i7-10750H")]
    [InlineData("Processor", "AMD Ryzen 7 5800X 8-Core Processor", "Ryzen 7 5800X")]
    [InlineData("Graphics", "NVIDIA GeForce RTX 2060 + Intel UHD Graphics", "GeForce RTX 2060")]
    [InlineData("Memory", "16 GB", "16 GB memory")]
    [InlineData("Disk C:", "420 GB free of 511 GB", "420 GB free on C:")]
    [InlineData("Running for", "13 h 40 min", "On for 13 h 40 min")]
    [InlineData("Battery", "99 %, plugged in", "99 %, plugged in")]
    public void Short_IsOneLine(string label, string value, string expected) => Assert.Equal(expected, SystemSummary.Shorten(label, value));
}
