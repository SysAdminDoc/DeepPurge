using DeepPurge.Core.Repair;
using Xunit;

namespace DeepPurge.Tests;

/// <summary>
/// WindowsRepairEngine interpolates user-supplied tokens into winget /
/// msiexec command lines. These tests lock the regex sanitisers so a
/// future edit can't silently widen what's accepted.
/// </summary>
public class WindowsRepairSanitiserTests
{
    private static string SanitizeToken(string? raw)
    {
        var mi = typeof(WindowsRepairEngine).GetMethod(
            "SanitizeToken",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (string)mi.Invoke(null, new object?[] { raw })!;
    }

    private static string SanitizeProductCode(string? raw)
    {
        var mi = typeof(WindowsRepairEngine).GetMethod(
            "SanitizeProductCode",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (string)mi.Invoke(null, new object?[] { raw })!;
    }

    // ── Winget package ID sanitiser ────────────────────────────────

    [Theory]
    [InlineData("Microsoft.VisualStudioCode", "Microsoft.VisualStudioCode")]
    [InlineData("Git.Git", "Git.Git")]
    [InlineData("JanDeDobbeleer.OhMyPosh", "JanDeDobbeleer.OhMyPosh")]
    [InlineData("node-lts", "node-lts")]
    [InlineData("pkg_with_underscore", "pkg_with_underscore")]
    public void Winget_id_lets_through_valid_identifiers(string input, string expected)
    {
        Assert.Equal(expected, SanitizeToken(input));
    }

    [Theory]
    [InlineData("& del /q *",           "delq")]
    [InlineData("Package && rm -rf /",  "Packagerm-rf")]
    [InlineData("pkg; DROP TABLE apps", "pkgDROPTABLEapps")]
    [InlineData("..\\..\\evil",         "....evil")]  // '.' is valid in IDs; traversal slashes stripped, harmless
    [InlineData("`rm -rf`",             "rm-rf")]
    [InlineData("\"Microsoft.Foo\"",    "Microsoft.Foo")]
    public void Winget_id_strips_shell_metacharacters(string input, string expected)
    {
        Assert.Equal(expected, SanitizeToken(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    public void Winget_id_falls_back_to_empty_quotes_for_no_chars(string input)
    {
        Assert.Equal("\"\"", SanitizeToken(input));
    }

    [Fact]
    public void Winget_id_null_safe()
    {
        Assert.Equal("\"\"", SanitizeToken(null));
    }

    // ── MSI product-code sanitiser ─────────────────────────────────

    [Theory]
    [InlineData("{00000000-0000-0000-0000-000000000000}")]
    [InlineData("{ABCDEF12-3456-7890-ABCD-EF1234567890}")]
    [InlineData("{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}")]
    public void Product_code_accepts_valid_guid(string guid)
    {
        Assert.Equal(guid, SanitizeProductCode(guid));
    }

    [Theory]
    [InlineData("{00000000-0000-0000-0000-000000000000} & del /q *")]
    [InlineData("prefix{12345678-1234-1234-1234-123456789012}suffix")]
    public void Product_code_extracts_embedded_guid(string input)
    {
        var result = SanitizeProductCode(input);
        Assert.Matches(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$", result);
    }

    [Theory]
    [InlineData("{not-a-guid}")]
    [InlineData("")]
    [InlineData("& del /q *")]
    [InlineData("{000-000-000-000-000}")]
    public void Product_code_rejects_non_guid(string input)
    {
        Assert.Equal("\"\"", SanitizeProductCode(input));
    }

    [Fact]
    public void Product_code_null_safe()
    {
        Assert.Equal("\"\"", SanitizeProductCode(null));
    }
}
