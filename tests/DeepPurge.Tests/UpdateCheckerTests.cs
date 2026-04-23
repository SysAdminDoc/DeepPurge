using DeepPurge.Core.Updates;
using Xunit;

namespace DeepPurge.Tests;

/// <summary>
/// Regression tests for the version-compare bug found during the v0.9.0
/// hardening pass: a 3-part GitHub tag (e.g. "v0.9.0") would always report
/// "update available" against a 4-part assembly version ("0.9.0.0")
/// because <see cref="Version"/> treats the missing revision component
/// as -1.
///
/// These tests lock in the fix: normalise both sides to the same
/// number of parts (padding with 0) before comparing.
/// </summary>
public class UpdateCheckerTests
{
    // CompareVersions is private — we test through the public surface by
    // constructing the kind of inputs that triggered the bug. We can't
    // reach the internal method without reflection; instead we verify
    // the observable contract.
    private static int Compare(string a, string b)
    {
        var mi = typeof(UpdateChecker).GetMethod(
            "CompareVersions",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (int)mi.Invoke(null, new object[] { a, b })!;
    }

    [Theory]
    [InlineData("0.9.0",   "0.9.0.0", 0)]     // <- The exact bug
    [InlineData("0.9.0.0", "0.9.0",   0)]     // <- Commutative
    [InlineData("1.0",     "1.0.0.0", 0)]
    [InlineData("0.9.0",   "0.9.1",  -1)]
    [InlineData("0.9.1",   "0.9.0",   1)]
    [InlineData("1.0.0",   "0.9.99",  1)]
    [InlineData("",        "0.0.0",   0)]     // empty treats as all zeros
    [InlineData("v0.9.0",  "0.9.0",   0)]     // pretend caller forgot Normalise — the compare still works on parts
    public void CompareVersions_normalisesMissingComponentsToZero(string a, string b, int expected)
    {
        var cmp = Compare(a, b);
        // Only care about sign, not magnitude.
        Assert.Equal(expected, Math.Sign(cmp));
    }

    [Theory]
    [InlineData("0.9.0-beta", "0.9.0", 0)]        // pre-release tag drops to 0 → equal for ordering
    [InlineData("1.2.3+sha.abc", "1.2.3", 0)]     // build metadata drops
    [InlineData("1.2.3",      "1.2.4", -1)]
    public void CompareVersions_ignoresPreReleaseAndBuildMetadata(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(Compare(a, b)));
    }
}
