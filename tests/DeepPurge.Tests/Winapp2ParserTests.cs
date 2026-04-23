using DeepPurge.Core.Cleaning;
using Xunit;

namespace DeepPurge.Tests;

/// <summary>
/// Locks in the v0.9.0 hardening fix: DetectOS / SpecialDetect / DetectFile
/// were all collapsed into a single "Detect" bucket pre-fix, so every entry
/// with an OS gate was wrongly excluded on applicability check.
/// </summary>
public class Winapp2ParserTests
{
    private const string SampleIni = @"
[Foo *]
LangSecRef=3021
Default=False
DetectOS=10.0|
Detect=HKCU\Software\Foo
Detect1=HKLM\SOFTWARE\Foo
DetectFile=%AppData%\Foo
DetectFile1=%LocalAppData%\Foo2
Warning=Clears recent searches
FileKey1=%AppData%\Foo|*.tmp
FileKey2=%AppData%\Foo|*.log|RECURSE
RegKey1=HKCU\Software\Foo\Recent
ExcludeKey1=FILE|%AppData%\Foo|keep.tmp
SpecialDetect=DET_CHROME

[Bar]
LangSecRef=3022
FileKey1=%AppData%\Bar|*.bak
";

    [Fact]
    public void Parses_all_bucket_families_correctly()
    {
        using var reader = new StringReader(SampleIni);
        var entries = Winapp2Parser.Parse(reader);

        Assert.Equal(2, entries.Count);
        var foo = entries[0];

        Assert.Equal("Foo *", foo.Section);
        Assert.Equal("3021", foo.LangSecRef);
        Assert.Equal("False", foo.Default);
        Assert.Equal("10.0|", foo.DetectOS);
        Assert.Equal("DET_CHROME", foo.SpecialDetect);

        // Detect / Detect1 both land in Detect list, NOT DetectOS, NOT DetectFile.
        Assert.Equal(2, foo.Detect.Count);
        Assert.Contains(@"HKCU\Software\Foo", foo.Detect);
        Assert.Contains(@"HKLM\SOFTWARE\Foo", foo.Detect);

        Assert.Equal(2, foo.DetectFile.Count);
        Assert.Equal(2, foo.FileKeys.Count);
        Assert.Single(foo.RegKeys);
        Assert.Single(foo.ExcludeKeys);
        Assert.Single(foo.Warning);
    }

    [Fact]
    public void Blank_DetectOs_means_universally_applicable()
    {
        using var reader = new StringReader("[X]\n");
        var list = Winapp2Parser.Parse(reader);
        // No Detect / DetectFile / DetectOS → always applicable.
        Assert.True(list[0].IsApplicable());
    }

    [Fact]
    public void DetectOs_above_current_excludes()
    {
        // 99.0 will be above any real Windows version.
        using var reader = new StringReader("[X]\nDetectOS=99.0|\n");
        var list = Winapp2Parser.Parse(reader);
        Assert.False(list[0].IsApplicable());
    }

    [Fact]
    public void DetectOs_range_accepted_when_within()
    {
        // Current Win11 is 10.0.x; 6.0|11.0 straddles that.
        using var reader = new StringReader("[X]\nDetectOS=6.0|11.0\n");
        var list = Winapp2Parser.Parse(reader);
        Assert.True(list[0].IsApplicable());
    }

    [Fact]
    public void Comments_and_blank_lines_ignored()
    {
        using var reader = new StringReader(@"
; comment line

# also a comment

[Y]
FileKey1=%TEMP%|*.tmp
");
        var list = Winapp2Parser.Parse(reader);
        Assert.Single(list);
        Assert.Equal("Y", list[0].Section);
    }
}
