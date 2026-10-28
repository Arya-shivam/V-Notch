using System.Text.Json;
using VNotch.Services.AiUsage;
using Xunit;

namespace VNotch.Tests;

public sealed class AiUsageProviderTests
{
    [Fact]
    public void CodexPreservesWindowDurationUsageAndReset()
    {
        using var json = JsonDocument.Parse("""
            {"rate_limit":{"primary_window":{"used_percent":12.5,"limit_window_seconds":18000,"reset_at":1793209836},
                           "secondary_window":{"used_percent":100,"limit_window_seconds":604800,"reset_at":1793777985}}}
            """);
        var result = CodexUsageProvider.ParseUsage(json.RootElement);
        Assert.Equal(UsageProviderState.Available, result.State);
        Assert.Collection(result.Windows,
            window =>
            {
                Assert.Equal("5-hour", window.Name);
                Assert.Equal(12.5, window.UsedPercent);
                Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793209836), window.ResetsAt);
            },
            window =>
            {
                Assert.Equal("1-week", window.Name);
                Assert.Equal(100, window.UsedPercent);
                Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793777985), window.ResetsAt);
            });
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"12\"")]
    [InlineData("-1")]
    [InlineData("101")]
    [InlineData("1e999")]
    public void CodexInvalidQuotaDoesNotBecomeAnAllowance(string used)
    {
        using var json = JsonDocument.Parse("{\"rate_limit\":{\"primary_window\":{\"used_percent\":" + used + "}}}");
        var result = CodexUsageProvider.ParseUsage(json.RootElement);
        Assert.Equal(UsageProviderState.Error, result.State);
        Assert.Empty(result.Windows);
        Assert.Null(result.UpdatedAt);
    }

    [Fact]
    public void CodexMissingWindowsAreUnavailableRatherThanUnused()
    {
        using var json = JsonDocument.Parse("{\"rate_limit\":null}");
        var result = CodexUsageProvider.ParseUsage(json.RootElement);
        Assert.Equal(UsageProviderState.Unavailable, result.State);
        Assert.Empty(result.Windows);
    }

    [Fact]
    public void ClaudeDoesNotConvertSpendingOrMalformedUtilizationToQuota()
    {
        using var json = JsonDocument.Parse("""
            {"five_hour":{"utilization":0,"resets_at":"2026-10-30T10:00:00Z"},
             "seven_day":{"utilization":101},"seven_day_sonnet":{"utilization":"20"},
             "extra_usage":{"used_credits":42,"monthly_limit":100}}
            """);
        var window = Assert.Single(ClaudeUsageProvider.ParseWindows(json.RootElement));
        Assert.Equal("5 hours", window.Name);
        Assert.Equal(0, window.UsedPercent);
        Assert.Equal(DateTimeOffset.Parse("2026-10-30T10:00:00Z"), window.ResetsAt);
    }

    [Fact]
    public void KimiRatioPoolsAreNotDuplicatedByLegacyQuotasOrMonthlyShares()
    {
        using var json = JsonDocument.Parse("""
            {"usages":{"limit_5h":{"used_ratio":0.25,"reset_at":"2026-10-30T10:00:00Z"},
                       "limit_7d":{"used_ratio":0.4},"limit_month_total":{"used_ratio":0.1},
                       "limit_month_code":{"used_ratio":0.9}},
             "limits":[{"window":{"duration":5,"timeUnit":"TIME_UNIT_HOUR"},
                        "detail":{"limit":"100","used":"30","resetTime":"2026-10-30T10:00:00Z"}}]}
            """);
        Assert.Collection(KimiUsageProvider.ParseWindows(json.RootElement),
            window => { Assert.Equal("5 hours", window.Name); Assert.Equal(30, window.UsedPercent); },
            window => { Assert.Equal("Weekly", window.Name); Assert.Equal(40, window.UsedPercent); },
            window => { Assert.Equal("Monthly total", window.Name); Assert.Equal(10, window.UsedPercent); });
    }

    [Theory]
    [InlineData("{\"usage\":{\"limit\":0,\"remaining\":0}}")]
    [InlineData("{\"usage\":{\"limit\":100,\"used\":101}}")]
    [InlineData("{\"usages\":{\"limit_5h\":{\"used_ratio\":1.1}}}")]
    public void KimiInvalidLimitsDoNotBecomeFullOrEmptyAllowance(string payload)
    {
        using var json = JsonDocument.Parse(payload);
        Assert.Empty(KimiUsageProvider.ParseWindows(json.RootElement));
    }

    [Fact]
    public void CustomSourceKeepsProviderAndQuotaMeaningWithoutAProviderSpecificView()
    {
        var definition = new CustomUsageProviderDefinition("another-provider", "Another Provider", new Uri("https://example.com/usage"));
        using var json = JsonDocument.Parse("""
            {"windows":[{"name":"Daily","usedPercent":37.25,"resetsAt":"2026-10-30T00:00:00Z"}]}
            """);
        var result = CustomJsonUsageProvider.ParseUsage(definition, json.RootElement);
        Assert.Equal("another-provider", result.ProviderId);
        var window = Assert.Single(result.Windows);
        Assert.Equal(37.25, window.UsedPercent);
        Assert.Equal(DateTimeOffset.Parse("2026-10-30T00:00:00Z"), window.ResetsAt);
    }

    [Theory]
    [InlineData("http://example.com/usage")]
    [InlineData("https://user:password@example.com/usage")]
    [InlineData("https://example.com/usage?token=secret")]
    public void CustomSourceRejectsInsecureOrEmbeddedCredentialEndpoints(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => new CustomJsonUsageProvider(
            new CustomUsageProviderDefinition("custom", "Custom", new Uri(endpoint))));
    }
}
