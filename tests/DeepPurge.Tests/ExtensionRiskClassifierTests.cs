using DeepPurge.Core.Browsers;
using Xunit;

namespace DeepPurge.Tests;

public class ExtensionRiskClassifierTests
{
    [Fact]
    public void Benign_extension_classified_as_low()
    {
        var ext = new BrowserExtension
        {
            Permissions = new() { "storage", "activeTab" },
            HostPermissions = new(),
        };
        ExtensionRiskClassifier.Classify(ext);
        Assert.Equal(ExtensionRiskLevel.Low, ext.RiskLevel);
        Assert.Empty(ext.RiskLabels);
    }

    [Fact]
    public void Broad_host_access_classified_as_high()
    {
        var ext = new BrowserExtension
        {
            Permissions = new() { "storage" },
            HostPermissions = new() { "<all_urls>" },
        };
        ExtensionRiskClassifier.Classify(ext);
        Assert.Equal(ExtensionRiskLevel.High, ext.RiskLevel);
        Assert.Contains("Broad host access", ext.RiskLabels);
    }

    [Fact]
    public void Wildcard_https_detected_as_broad()
    {
        var ext = new BrowserExtension
        {
            HostPermissions = new() { "https://*/*" },
        };
        ExtensionRiskClassifier.Classify(ext);
        Assert.Equal(ExtensionRiskLevel.High, ext.RiskLevel);
        Assert.Contains("Broad host access", ext.RiskLabels);
    }

    [Fact]
    public void Native_messaging_classified_as_critical()
    {
        var ext = new BrowserExtension
        {
            Permissions = new() { "nativeMessaging" },
        };
        ExtensionRiskClassifier.Classify(ext);
        Assert.Equal(ExtensionRiskLevel.Critical, ext.RiskLevel);
        Assert.Contains("Native messaging", ext.RiskLabels);
    }

    [Fact]
    public void Background_api_classified_as_medium()
    {
        var ext = new BrowserExtension
        {
            Permissions = new() { "webRequest" },
        };
        ExtensionRiskClassifier.Classify(ext);
        Assert.Equal(ExtensionRiskLevel.Medium, ext.RiskLevel);
        Assert.Contains("Background activity", ext.RiskLabels);
    }

    [Fact]
    public void Sensitive_api_classified_as_high()
    {
        var ext = new BrowserExtension
        {
            Permissions = new() { "history", "cookies" },
        };
        ExtensionRiskClassifier.Classify(ext);
        Assert.Equal(ExtensionRiskLevel.High, ext.RiskLevel);
        Assert.Single(ext.RiskLabels.Where(l => l.Contains("Sensitive API")));
    }

    [Fact]
    public void Multiple_risks_take_highest_level()
    {
        var ext = new BrowserExtension
        {
            Permissions = new() { "nativeMessaging", "history", "webRequest" },
            HostPermissions = new() { "<all_urls>" },
        };
        ExtensionRiskClassifier.Classify(ext);
        Assert.Equal(ExtensionRiskLevel.Critical, ext.RiskLevel);
        Assert.True(ext.RiskLabels.Count >= 3);
    }
}
