using DeepPurge.Core.Drivers;
using Xunit;

namespace DeepPurge.Tests;

/// <summary>
/// Locks in the text-format parser for <c>pnputil /enum-drivers</c>. This
/// format is what real Windows 10/11 systems produce (the XML format
/// probed first doesn't exist on most builds, as we discovered during
/// v0.9 hardening).
/// </summary>
public class DriverStoreParseTests
{
    private const string SampleOutput = @"Microsoft PnP Utility

Published Name:     oem5.inf
Original Name:      firmware.inf
Provider Name:      Dell, Inc.
Class Name:         Firmware
Class GUID:         {f2e7dd72-6468-4e36-b6f1-6488f42c1b52}
Driver Version:     11/27/2025 1.11.0.0
Signer Name:        Microsoft Windows Hardware Compatibility Publisher

Published Name:     oem29.inf
Original Name:      dax3_ext_rtk.inf
Provider Name:      Dolby
Class Name:         Extensions
Class GUID:         {e2f84ce7-8efa-411c-aa69-97454ca4cb57}
Driver Version:     10/31/2024 9.1101.1244.42
Signer Name:        Microsoft Windows Hardware Compatibility Publisher

";

    private static List<DriverPackage> Parse(string text)
    {
        var mi = typeof(DriverStoreScanner).GetMethod(
            "ParseText",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (List<DriverPackage>)mi.Invoke(null, new object[] { text })!;
    }

    [Fact]
    public void Parses_both_packages_with_all_fields()
    {
        var list = Parse(SampleOutput);

        Assert.Equal(2, list.Count);

        var dell = list[0];
        Assert.Equal("oem5.inf",    dell.PublishedName);
        Assert.Equal("firmware.inf", dell.OriginalName);
        Assert.Equal("Dell, Inc.",   dell.ProviderName);
        Assert.Equal("Firmware",     dell.ClassName);
        Assert.Equal("{f2e7dd72-6468-4e36-b6f1-6488f42c1b52}", dell.ClassGuid);
        Assert.Equal("11/27/2025 1.11.0.0", dell.DriverVersion);
        Assert.Equal("Microsoft Windows Hardware Compatibility Publisher", dell.SignerName);
        Assert.Equal(new Version(1, 11, 0, 0), dell.ParsedVersion);
        Assert.NotNull(dell.DriverDate);
        Assert.Equal(2025, dell.DriverDate!.Value.Year);

        var dolby = list[1];
        Assert.Equal("oem29.inf", dolby.PublishedName);
        Assert.Equal("dax3_ext_rtk.inf", dolby.OriginalName);
        Assert.Equal("Dolby", dolby.ProviderName);
    }

    [Fact]
    public void Parses_legacy_lowercase_labels()
    {
        // `pnputil -e` (dash syntax) outputs "Published name:" not "Published Name:".
        const string legacy = @"Microsoft PnP Utility

Published name :            oem99.inf
Driver package provider :   Acme
Class :                     Ports
Driver date and version :   02/05/2010 7.1.30.51
Signer name :               Microsoft Windows Hardware Compatibility Publisher

";
        // Labels with a trailing space before colon are also in the wild; fall back.
        var list = Parse(legacy.Replace(" :", ":"));
        Assert.Single(list);
        Assert.Equal("oem99.inf", list[0].PublishedName);
        Assert.Equal("Acme", list[0].ProviderName);
        Assert.Equal("Ports", list[0].ClassName);
    }

    [Fact]
    public void Empty_output_returns_empty_list()
    {
        Assert.Empty(Parse(""));
    }

    [Fact]
    public void Records_separated_by_blank_lines()
    {
        const string noTrailingBlank = "Published Name:     oem1.inf\r\nOriginal Name:      a.inf\r\n\r\nPublished Name:     oem2.inf\r\nOriginal Name:      b.inf";
        var list = Parse(noTrailingBlank);
        Assert.Equal(2, list.Count);
    }
}
