using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace VNotch.Services.AiUsage;

/// <summary>Reads the existing Codex CLI login without refreshing or changing it.</summary>
/// <remarks>Usage schema: https://github.com/openai/codex/blob/main/codex-rs/backend-client/src/client.rs (rate_limit_snapshots_from_payload).</remarks>
public sealed class CodexUsageProvider : IAiUsageProvider, IDisposable
{
    private static readonly Uri UsageEndpoint = new("https://chatgpt.com/backend-api/wham/usage");
    private readonly HttpClient _http = AiUsageHttp.CreateClient();
    private readonly string? _authPath;

    public string Id => "codex";
    public string DisplayName => "Codex";

    /// <param name="authPath">Optional explicit auth.json path; otherwise CODEX_HOME or ~/.codex is used.</param>
    public CodexUsageProvider(string? authPath = null) => _authPath = authPath;

    public async Task<ProviderUsage> FetchAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? accessToken;
        string? accountId;
        try
        {
            string? home = Environment.GetEnvironmentVariable("CODEX_HOME");
            string path = _authPath ?? Path.Combine(string.IsNullOrWhiteSpace(home)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") : home, "auth.json");
            using var auth = await AiUsageHttp.ReadLocalJsonAsync(path, 256 * 1024, cancellationToken).ConfigureAwait(false);
            if (auth.RootElement.ValueKind != JsonValueKind.Object ||
                !auth.RootElement.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
                return SignedOut("Sign in with the Codex CLI to see subscription usage.");
            accessToken = AiUsageHttp.GetString(tokens, "access_token");
            accountId = AiUsageHttp.GetString(tokens, "account_id");
            if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(accountId) ||
                accessToken.Any(char.IsWhiteSpace) || accountId.Any(char.IsControl))
                return SignedOut("Codex login is incomplete. Sign in again with the Codex CLI.");
            if (IsExpired(accessToken))
                return SignedOut("Codex login has expired. Sign in again with the Codex CLI.");
        }
        catch (FileNotFoundException) { return SignedOut("Sign in with the Codex CLI to see subscription usage."); }
        catch (DirectoryNotFoundException) { return SignedOut("Sign in with the Codex CLI to see subscription usage."); }
        catch (JsonException) { return SignedOut("Codex login file is invalid. Sign in again with the Codex CLI."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return AiUsageHttp.Failure(Id, DisplayName, UsageProviderState.Error, "Cannot read the local Codex login.");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(AiUsageHttp.RequestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Add("ChatGPT-Account-Id", accountId);
            request.Headers.UserAgent.ParseAdd("codex-cli");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return SignedOut("Codex login was rejected or expired. Sign in again with the Codex CLI.");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return AiUsageHttp.Failure(Id, DisplayName, UsageProviderState.Unavailable, "Codex usage access was denied for this account.");
            if (!response.IsSuccessStatusCode)
                return AiUsageHttp.Failure(Id, DisplayName, UsageProviderState.Unavailable,
                    $"Codex usage is unavailable (HTTP {(int)response.StatusCode}).");
            using var document = await AiUsageHttp.ReadJsonAsync(response, timeout.Token).ConfigureAwait(false);
            return ParseUsage(document.RootElement);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AiUsageHttp.Failure(Id, DisplayName, UsageProviderState.Unavailable, "Codex usage request timed out or network permission changed.");
        }
        catch (HttpRequestException)
        {
            return AiUsageHttp.Failure(Id, DisplayName, UsageProviderState.Unavailable, "Cannot reach Codex usage. Check network and privacy settings.");
        }
        catch (FormatException) { return SignedOut("Codex login is invalid. Sign in again with the Codex CLI."); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or IOException)
        {
            return AiUsageHttp.Failure(Id, DisplayName, UsageProviderState.Error, "Codex returned invalid usage data.");
        }
    }

    /// <summary>Parses the Codex WHAM usage response. Missing limits are unavailable, never zero usage.</summary>
    public static ProviderUsage ParseUsage(JsonElement root, DateTimeOffset? updatedAt = null)
    {
        const string id = "codex";
        const string name = "Codex";
        try
        {
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
            var windows = new List<UsageWindow>();
            AddRateLimits(root, "rate_limit", "", windows);
            if (root.TryGetProperty("additional_rate_limits", out var additional) && additional.ValueKind != JsonValueKind.Null)
            {
                if (additional.ValueKind != JsonValueKind.Array || additional.GetArrayLength() > 64) throw new JsonException();
                foreach (var entry in additional.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object) throw new JsonException();
                    string label = AiUsageHttp.GetString(entry, "limit_name") ?? AiUsageHttp.GetString(entry, "metered_feature") ?? "Additional";
                    if (label.Length > 100 || label.Any(char.IsControl)) throw new JsonException();
                    AddRateLimits(entry, "rate_limit", label + " · ", windows);
                }
            }
            if (windows.Count == 0)
                return AiUsageHttp.Failure(id, name, UsageProviderState.Unavailable, "Codex did not expose subscription usage windows for this account.");
            string? plan = AiUsageHttp.GetString(root, "plan_type");
            string status = !string.IsNullOrWhiteSpace(plan) && plan.Length <= 64 && !plan.Any(char.IsControl)
                ? $"{plan} plan" : "Subscription usage";
            return new ProviderUsage(id, name, UsageProviderState.Available, status, windows, updatedAt ?? DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return AiUsageHttp.Failure(id, name, UsageProviderState.Error, "Codex returned invalid usage data.");
        }
    }

    private static void AddRateLimits(JsonElement root, string property, string prefix, List<UsageWindow> windows)
    {
        if (!root.TryGetProperty(property, out var limits) || limits.ValueKind == JsonValueKind.Null) return;
        if (limits.ValueKind != JsonValueKind.Object) throw new JsonException();
        AddWindow(limits, "primary_window", prefix, "Primary", windows);
        AddWindow(limits, "secondary_window", prefix, "Secondary", windows);
    }

    private static void AddWindow(JsonElement limits, string property, string prefix, string fallbackName, List<UsageWindow> windows)
    {
        if (!limits.TryGetProperty(property, out var window) || window.ValueKind == JsonValueKind.Null) return;
        if (window.ValueKind != JsonValueKind.Object || !window.TryGetProperty("used_percent", out var used) ||
            used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out double percent) ||
            !double.IsFinite(percent) || percent < 0 || percent > 100) throw new JsonException();
        string name = fallbackName;
        if (window.TryGetProperty("limit_window_seconds", out var duration))
        {
            if (!duration.TryGetInt64(out long seconds) || seconds <= 0) throw new JsonException();
            name = seconds % 604800 == 0 ? $"{seconds / 604800}-week" :
                seconds % 86400 == 0 ? $"{seconds / 86400}-day" :
                seconds % 3600 == 0 ? $"{seconds / 3600}-hour" :
                seconds % 60 == 0 ? $"{seconds / 60}-minute" : $"{seconds}-second";
        }
        DateTimeOffset? resetsAt = null;
        if (window.TryGetProperty("reset_at", out var reset) && reset.ValueKind != JsonValueKind.Null)
        {
            if (!reset.TryGetInt64(out long timestamp)) throw new JsonException();
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        }
        windows.Add(new UsageWindow(prefix + name, percent, resetsAt));
    }

    private static bool IsExpired(string token)
    {
        // JWT expiry is only an early rejection hint, not signature verification. Opaque tokens go to the fixed origin.
        int first = token.IndexOf('.');
        if (first < 0) return false;
        int second = token.IndexOf('.', first + 1);
        if (second < 0 || second - first > 64 * 1024) return false;
        try
        {
            string payload = token[(first + 1)..second].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("exp", out var expiry) && expiry.TryGetInt64(out long seconds) &&
                seconds <= DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException) { return false; }
    }

    private ProviderUsage SignedOut(string status) => AiUsageHttp.Failure(Id, DisplayName, UsageProviderState.NotSignedIn, status);

    public void Dispose() => _http.Dispose();
}
