namespace VNotch.Services.AiUsage;

public enum UsageProviderState { Available, NotSignedIn, Unavailable, Error }

public sealed record UsageWindow(string Name, double UsedPercent, DateTimeOffset? ResetsAt);

public sealed record ProviderUsage(
    string ProviderId,
    string DisplayName,
    UsageProviderState State,
    string Status,
    IReadOnlyList<UsageWindow> Windows,
    DateTimeOffset? UpdatedAt);

public interface IAiUsageProvider
{
    string Id { get; }
    string DisplayName { get; }
    Task<ProviderUsage> FetchAsync(CancellationToken cancellationToken);
}
