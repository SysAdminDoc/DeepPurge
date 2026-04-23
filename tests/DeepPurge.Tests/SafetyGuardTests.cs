using DeepPurge.Core.Safety;
using Xunit;

namespace DeepPurge.Tests;

/// <summary>
/// SafetyGuard is the single choke-point preventing catastrophic deletes.
/// These tests lock in its blocklist so a refactor can't accidentally
/// relax it.
/// </summary>
public class SafetyGuardTests
{
    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Windows\System32\kernel32.dll")]
    [InlineData(@"C:\Windows\System32\config\SYSTEM")]
    [InlineData(@"C:\Program Files\Windows Defender")]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\")]
    [InlineData(@"C:\$Recycle.Bin")]
    [InlineData(@"C:\bootmgr")]
    [InlineData(@"")]
    public void Blocks_protected_paths(string path)
    {
        Assert.False(SafetyGuard.IsPathSafeToDelete(path), $"Should reject {path}");
    }

    [Theory]
    [InlineData(@"C:\Users\alice\AppData\Local\Temp\setup.tmp")]
    [InlineData(@"D:\some\user\file.txt")]
    [InlineData(@"C:\ProgramData\MyApp\cache.dat")]
    public void Allows_user_paths(string path)
    {
        Assert.True(SafetyGuard.IsPathSafeToDelete(path), $"Should allow {path}");
    }

    [Theory]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Enum")]
    [InlineData(@"HKLM\SAM")]
    [InlineData(@"HKLM\SOFTWARE\Policies")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion")]
    public void Blocks_protected_registry(string path)
    {
        Assert.False(SafetyGuard.IsRegistryPathSafeToDelete(path));
    }

    [Theory]
    [InlineData(@"HKCU\Software\SomeVendor\SomeApp")]
    [InlineData(@"HKLM\SOFTWARE\ThirdParty\App")]
    public void Allows_app_registry(string path)
    {
        Assert.True(SafetyGuard.IsRegistryPathSafeToDelete(path));
    }

    [Theory]
    [InlineData("wuauserv")]
    [InlineData("BITS")]
    [InlineData("LSM")]
    [InlineData("Winmgmt")]
    public void Blocks_core_services(string name) => Assert.False(SafetyGuard.IsServiceSafeToModify(name));

    [Theory]
    [InlineData("SomeVendorService")]
    [InlineData("MyCustomDaemon")]
    public void Allows_third_party_services(string name) => Assert.True(SafetyGuard.IsServiceSafeToModify(name));
}
