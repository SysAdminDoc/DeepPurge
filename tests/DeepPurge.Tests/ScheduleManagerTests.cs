using DeepPurge.Core.Schedule;
using Xunit;

namespace DeepPurge.Tests;

/// <summary>
/// Exercise the name sanitiser — injection of schtasks metacharacters
/// through the job name was part of the v0.9 hardening pass.
/// </summary>
public class ScheduleManagerTests
{
    // SanitizeName is private; reach it by reflection because the contract
    // is security-critical and worth locking in.
    private static string Sanitize(string input)
    {
        var mi = typeof(ScheduleManager).GetMethod(
            "SanitizeName",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (string)mi.Invoke(null, new object[] { input })!;
    }

    [Theory]
    [InlineData("Normal Job Name",      "Normal Job Name")]
    [InlineData("with_underscore-dash", "with_underscore-dash")]
    [InlineData("Nightly 3am",          "Nightly 3am")]
    public void Allows_normal_names(string input, string expected)
    {
        Assert.Equal(expected, Sanitize(input));
    }

    [Theory]
    [InlineData("foo & del /q *",             "foo  del q")]   // & / * stripped, trailing spaces trimmed
    [InlineData("job | rm -rf /",             "job  rm -rf")]
    [InlineData("a\"b\"c",                    "abc")]
    [InlineData("..\\..\\escape",             "escape")]
    [InlineData("'; DROP TABLE jobs; --",     "DROP TABLE jobs --")]
    [InlineData("",                           "DeepPurgeJob")]
    [InlineData("   ",                        "DeepPurgeJob")]
    public void Strips_metacharacters_and_falls_back(string input, string expected)
    {
        Assert.Equal(expected, Sanitize(input));
    }

    [Fact]
    public void Null_input_falls_back_to_default()
    {
        Assert.Equal("DeepPurgeJob", Sanitize(null!));
    }

    [Fact]
    public void Caps_at_64_chars()
    {
        var longName = new string('A', 200);
        var result = Sanitize(longName);
        Assert.Equal(64, result.Length);
    }
}
