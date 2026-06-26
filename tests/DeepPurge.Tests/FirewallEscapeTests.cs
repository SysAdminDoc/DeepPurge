using DeepPurge.Core.Firewall;
using Xunit;

namespace DeepPurge.Tests;

public class FirewallEscapeTests
{
    [Fact]
    public void EncodePsCommand_RoundTrips()
    {
        var cmd = "Remove-NetFirewallRule -Name 'test' -ErrorAction Stop";
        var encoded = FirewallRuleScanner.EncodePsCommand(cmd);
        var decoded = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        Assert.Equal(cmd, decoded);
    }

    [Fact]
    public void EncodePsCommand_HandlesDoubleQuotes()
    {
        var cmd = "Remove-NetFirewallRule -Name 'rule\"with\"quotes' -ErrorAction Stop";
        var encoded = FirewallRuleScanner.EncodePsCommand(cmd);
        var decoded = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        Assert.Equal(cmd, decoded);
    }

    [Fact]
    public void EncodePsCommand_HandlesSemicolonsAndDollar()
    {
        var cmd = "Remove-NetFirewallRule -Name 'rule; $(evil)' -ErrorAction Stop";
        var encoded = FirewallRuleScanner.EncodePsCommand(cmd);
        var decoded = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        Assert.Equal(cmd, decoded);
    }

    [Fact]
    public void EncodePsCommand_HandlesBackticks()
    {
        var cmd = "Remove-NetFirewallRule -Name 'rule`nwith`tbackticks' -ErrorAction Stop";
        var encoded = FirewallRuleScanner.EncodePsCommand(cmd);
        var decoded = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        Assert.Equal(cmd, decoded);
    }

    [Fact]
    public void EncodePsCommand_ProducesValidBase64()
    {
        var cmd = "Get-Process";
        var encoded = FirewallRuleScanner.EncodePsCommand(cmd);
        var bytes = Convert.FromBase64String(encoded);
        Assert.True(bytes.Length > 0);
    }
}
