using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace VNotch.Services.AiUsage;

/// <summary>Reads the existing Claude Code login without refreshing or rewriting it.</summary>
public sealed class ClaudeUsageProvider : IAiUsageProvider, IDisposable
{
    // Protocol: https://github.com/steipete/CodexBar/blob/main/docs/claude.md
    // Windows storage: https://code.claude.com/docs/en/authentication#credential-management
    private static readonly Uri UsageEndpoint = new("https://api.anthropic.com/api/oauth/usage");
    private readonly HttpClient _client;
    private readonly string? _credentialsPath;

    public string Id => "claude";
    public string DisplayName => "Claude";

    public ClaudeUsageProvider() : this(AiUsageHttp.CreateClient(), null) { }

    internal ClaudeUsageProvider(HttpClient client, string? credentialsPath)
    {
        _client = client;
        _credentialsPath = credentialsPath;
    }

    public async Task<ProviderUsage> FetchAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(AiUsageHttp.RequestTimeout);
        try
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string configDirectory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } configured
                ? ExpandHome(configured, home) : Path.Combine(home, ".claude");
            using var credentials = await AiUsageHttp.ReadLocalJsonAsync(
                _credentialsPath ?? Path.Combine(configDirectory, ".credentials.json"), deadline.Token).ConfigureAwait(false);
            var root = credentials.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Failure(UsageProviderState.Error, "Claude Code credentials are malformed; sign in again in Claude Code.");
            if (!root.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object
                || string.IsNullOrWhiteSpace(AiUsageHttp.GetString(oauth, "accessToken")))
                return Failure(UsageProviderState.NotSignedIn, "No Claude subscription login found. Sign in with a claude.ai account in Claude Code; API keys and MCP logins do not expose subscription limits.");

            if (oauth.TryGetProperty("scopes", out var scopes) && scopes.ValueKind == JsonValueKind.Array
                && !scopes.EnumerateArray().Any(scope => scope.ValueKind == JsonValueKind.String && scope.GetString() == "user:profile"))
                return Failure(UsageProviderState.Unavailable, "This Claude token lacks user:profile usage access. Sign in in Claude Code; setup-token inference tokens cannot read subscription limits.");
            if (oauth.TryGetProperty("expiresAt", out var expiry) && expiry.ValueKind != JsonValueKind.Null)
            {
                if (expiry.ValueKind != JsonValueKind.Number || !expiry.TryGetDouble(out double expiresAt) || !double.IsFinite(expiresAt))
                    return Failure(UsageProviderState.Error, "Claude Code login expiry is malformed. Sign in again in Claude Code.");
                if (expiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                    return Failure(UsageProviderState.NotSignedIn, "Claude Code login has expired. Open Claude Code to renew it, then refresh; V-Notch never refreshes your credentials.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AiUsageHttp.GetString(oauth, "accessToken"));
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return Failure(UsageProviderState.NotSignedIn, "Claude rejected the saved login. Open Claude Code to renew it, then refresh.");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return Failure(UsageProviderState.Unavailable, "Claude denied subscription usage access. A claude.ai login with user:profile scope is required.");
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Failure(UsageProviderState.Unavailable, "Claude's subscription usage endpoint is unavailable for this login.");
            if (!response.IsSuccessStatusCode)
                return Failure(UsageProviderState.Error, $"Claude usage request failed (HTTP {(int)response.StatusCode}).");

            using var document = await AiUsageHttp.ReadJsonAsync(response, deadline.Token).ConfigureAwait(false);
            var windows = ParseWindows(document.RootElement);
            return windows.Count == 0
                ? Failure(UsageProviderState.Unavailable, "Claude returned no measurable subscription quota windows; missing usage is not treated as zero.")
                : new ProviderUsage(Id, DisplayName, UsageProviderState.Available, "Claude subscription usage", windows, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(UsageProviderState.Error, "Claude usage request timed out.");
        }
        catch (FileNotFoundException) { return MissingLogin(); }
        catch (DirectoryNotFoundException) { return MissingLogin(); }
        catch (UnauthorizedAccessException) { return Failure(UsageProviderState.Error, "Claude Code credentials cannot be read. Check their file permissions."); }
        catch (JsonException) { return Failure(UsageProviderState.Error, "Claude credentials or usage response contain malformed JSON."); }
        catch (IOException) { return Failure(UsageProviderState.Error, "Claude Code credentials or usage response could not be read."); }
        catch (HttpRequestException) { return Failure(UsageProviderState.Error, "Claude usage could not be reached. Check the network and online AI privacy settings."); }
        catch (ArgumentException) { return Failure(UsageProviderState.Error, "Claude Code login configuration is invalid."); }
        catch (FormatException) { return Failure(UsageProviderState.Error, "Claude Code saved login is malformed."); }
    }

    internal static IReadOnlyList<UsageWindow> ParseWindows(JsonElement root)
    {
        var windows = new List<UsageWindow>(6);
        if (root.ValueKind != JsonValueKind.Object)
            return windows;
        AddWindow("five_hour", "5 hours");
        AddWindow("seven_day", "Weekly");
        AddWindow("seven_day_sonnet", "Sonnet weekly");
        AddWindow("seven_day_opus", "Opus weekly");
        AddWindow("seven_day_routines", "Routines");
        AddWindow("seven_day_cowork", "Cowork");
        // extra_usage is metered spending, not a subscription allowance.
        return windows;

        void AddWindow(string key, string name)
        {
            if (!root.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object
                || !window.TryGetProperty("utilization", out var usage) || usage.ValueKind != JsonValueKind.Number
                || !usage.TryGetDouble(out double percent) || !double.IsFinite(percent) || percent < 0 || percent > 100)
                return;
            DateTimeOffset? resetsAt = DateTimeOffset.TryParse(AiUsageHttp.GetString(window, "resets_at"),
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var reset) ? reset : null;
            windows.Add(new UsageWindow(name, percent, resetsAt));
        }
    }

    private ProviderUsage MissingLogin() => Failure(UsageProviderState.NotSignedIn,
        "Sign in with a claude.ai account in Claude Code. Browser/Desktop-only sessions and API keys are not read by this provider.");

    private ProviderUsage Failure(UsageProviderState state, string status) => AiUsageHttp.Failure(Id, DisplayName, state, status);

    private static string ExpandHome(string path, string home) => path == "~" ? home
        : path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(home, path[2..]) : path;

    public void Dispose() => _client.Dispose();
}
