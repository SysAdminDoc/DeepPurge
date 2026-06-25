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
    [InlineData(@"C:\Users\Public\..\..\..\Windows\System32\config\SAM")]
    [InlineData(@"C:\Temp\..\Windows\System32")]
    [InlineData(@"C:\Users\alice\..\..\bootmgr")]
    [InlineData(@"..")]
    [InlineData(@"C:\safe\path\..\..\Windows")]
    public void Blocks_path_traversal(string path)
    {
        Assert.False(SafetyGuard.IsPathSafeToDelete(path), $"Should reject traversal path: {path}");
    }

    [Theory]
    [InlineData(@"C:\Users\alice\AppData\Local\Temp\setup.tmp")]
    [InlineData(@"D:\some\user\file.txt")]
    [InlineData(@"C:\ProgramData\MyApp\cache.dat")]
    public void Allows_user_paths(string path)
    {
        Assert.True(SafetyGuard.IsPathSafeToDelete(path), $"Should allow {path}");
    }

    [Fact]
    public void Blocks_dynamic_windows_dir()
    {
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.False(SafetyGuard.IsPathSafeToDelete(winDir));
        Assert.False(SafetyGuard.IsPathSafeToDelete(Path.Combine(winDir, "System32")));
        Assert.False(SafetyGuard.IsPathSafeToDelete(Path.Combine(winDir, "System32", "ntdll.dll")));
    }

    [Fact]
    public void Blocks_dynamic_program_files()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.False(SafetyGuard.IsPathSafeToDelete(Path.Combine(pf, "Windows Defender")));
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

    [Theory]
    [InlineData("Core Networking - DNS (UDP-Out)")]
    [InlineData("Windows Defender Firewall Remote Management")]
    [InlineData("@FirewallAPI.dll,-12345")]
    [InlineData("@%SystemRoot%\\system32\\something.dll")]
    public void Blocks_core_firewall_rules(string displayName)
        => Assert.False(SafetyGuard.IsFirewallRuleSafeToDelete(displayName));

    [Theory]
    [InlineData("Adobe Updater")]
    [InlineData("Spotify Music")]
    [InlineData("My Custom App Rule")]
    public void Allows_third_party_firewall_rules(string displayName)
        => Assert.True(SafetyGuard.IsFirewallRuleSafeToDelete(displayName));

    [Theory]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Windows\SysWOW64")]
    [InlineData(@"C:\Program Files\dotnet")]
    [InlineData(@"C:\Program Files\PowerShell\7")]
    [InlineData(@"C:\Program Files\WindowsApps")]
    public void Blocks_system_path_entries(string dir)
        => Assert.False(SafetyGuard.IsPathEntrySafeToRemove(dir));

    [Theory]
    [InlineData(@"C:\Program Files\SomeApp")]
    [InlineData(@"D:\Tools\bin")]
    public void Allows_third_party_path_entries(string dir)
        => Assert.True(SafetyGuard.IsPathEntrySafeToRemove(dir));
}
