using DeepPurge.Core.Privacy;
using Xunit;

namespace DeepPurge.Tests;

public class CookieWhitelistTests
{
    [Theory]
    [InlineData("Cookies", true)]
    [InlineData("Cookies-journal", true)]
    [InlineData("cookies.sqlite", true)]
    [InlineData("cookies.sqlite-wal", true)]
    [InlineData("cookies.sqlite-shm", true)]
    [InlineData("History", false)]
    [InlineData("Cache", false)]
    [InlineData("", false)]
    [InlineData("cookie.db", false)]
    public void IsCookiePath_detects_cookie_files(string filename, bool expected)
    {
        var path = Path.Combine(@"C:\Users\test\AppData\Local\Google\Chrome\User Data\Default", filename);
        Assert.Equal(expected, EvidenceRemover.IsCookiePath(path));
    }

    [Fact]
    public void IsCookiePath_handles_full_path()
    {
        Assert.True(EvidenceRemover.IsCookiePath(@"C:\deep\nested\path\to\Cookies"));
        Assert.True(EvidenceRemover.IsCookiePath(@"C:\Users\me\AppData\Roaming\Mozilla\Firefox\Profiles\abc123\cookies.sqlite"));
        Assert.False(EvidenceRemover.IsCookiePath(@"C:\Users\me\AppData\Roaming\Mozilla\Firefox\Profiles\abc123\places.sqlite"));
    }
}
