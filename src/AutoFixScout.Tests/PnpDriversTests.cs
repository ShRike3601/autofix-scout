using AutoFixScout.TestBench;
using Xunit;

namespace AutoFixScout.Tests;

public class PnpDriversTests
{
    // Shape of `pnputil /enum-drivers /class Media` on AFS-TESTBENCH during step 5c: staged package listed first.
    private const string TwoIntelEntries = """
        Microsoft PnP Utility

        Published Name:     oem42.inf
        Original Name:      intcdaud.inf
        Provider Name:      Intel(R) Corporation
        Class Name:         MEDIA
        Driver Version:     02/25/2020 10.27.0.9
        Signer Name:        Microsoft Windows Hardware Compatibility Publisher

        Published Name:     oem9.inf
        Original Name:      intcdaud.inf
        Provider Name:      Intel(R) Corporation
        Class Name:         MEDIA
        Driver Version:     11/14/2019 10.27.0.8
        Signer Name:        Microsoft Windows Hardware Compatibility Publisher
        """;

    [Fact]
    public void Parse_ReadsEachEntrysOwnVersion()
    {
        var store = PnpDrivers.Parse(TwoIntelEntries.Replace("\n", "\r\n"));
        Assert.Equal(new[] { ("oem42.inf", "10.27.0.9"), ("oem9.inf", "10.27.0.8") }, store.Select(d => (d.PublishedName, d.Version)));
    }

    [Fact]
    public void Unstage_DeletesOnlyTheStagedPackage_NeverTheOriginal()
    {
        // Step 5c regression: the old regex matched oem42's name with oem9's version and deleted oem9.
        var store = PnpDrivers.Parse(TwoIntelEntries);
        Assert.Equal("oem42.inf", PnpDrivers.ToDelete(store, new("oem9.inf", "oem42.inf")));
    }

    [Fact]
    public void Unstage_NoRecord_OrOriginalMissing_DeletesNothing()
    {
        var store = PnpDrivers.Parse(TwoIntelEntries);
        Assert.Null(PnpDrivers.ToDelete(store, null));
        Assert.Null(PnpDrivers.ToDelete(store, new("oem42.inf", "oem42.inf")));
        // The original is gone: deleting the staged one would leave the device with no driver.
        Assert.Null(PnpDrivers.ToDelete(store.Where(d => d.PublishedName != "oem9.inf").ToList(), new("oem9.inf", "oem42.inf")));
    }

    [Fact]
    public void StageRecord_RoundTrips()
    {
        var r = new PnpDrivers.StageRecord("oem9.inf", "oem42.inf");
        Assert.Equal(r, PnpDrivers.StageRecord.Parse(r.Serialize()));
        Assert.Null(PnpDrivers.StageRecord.Parse("garbage"));
    }
}
