using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace VNotch.Services.AiUsage;

/// <summary>Reads Kimi Code subscription quotas using an existing CLI login or explicit Code API key.</summary>
public sealed class KimiUsageProvider : IAiUsageProvider, IDisposable
{
    // Official auth/usage/storage: https://github.com/MoonshotAI/kimi-cli/tree/main/src/kimi_cli
    // Additional legacy home and ratio schema: https://github.com/steipete/CodexBar/blob/main/docs/kimi.md
    private static readonly Uri ChinaUsageEndpoint = new("https://api.kimi.com/coding/v1/usages");
    private static readonly Uri InternationalUsageEndpoint = new("https://api.kimi.ai/coding/v1/usages");
    private readonly HttpClient _client;
    private readonly string? _homeDirectory;

    public string Id => "kimi";
    public string DisplayName => "Kimi Code";

    public KimiUsageProvider() : this(AiUsageHttp.CreateClient(), null) { }

    internal KimiUsageProvider(HttpClient client, string? homeDirectory)
    {
        _client = client;
        _homeDirectory = homeDirectory;
    }

    public async Task<ProviderUsage> FetchAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(AiUsageHttp.RequestTimeout);
        try
        {
            string? apiKey = Environment.GetEnvironmentVariable("KIMI_CODE_API_KEY");
            bool explicitKey = !string.IsNullOrWhiteSpace(apiKey);
            var endpoint = ResolveEndpoint(Environment.GetEnvironmentVariable("KIMI_CODE_BASE_URL"));
            if (endpoint is null)
                return Failure(UsageProviderState.Unavailable, "Custom Kimi endpoints are not supported. Credentials are sent only to the fixed kimi.com or kimi.ai subscription usage endpoint.");
            if (!explicitKey && (endpoint != ChinaUsageEndpoint || HasCustomOAuthHost()))
                return Failure(UsageProviderState.Unavailable, "The Kimi CLI credential file does not identify its issuing host. Automatic login reuse is limited to the default China service; use a region-matched KIMI_CODE_API_KEY for kimi.ai.");

            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (explicitKey)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey!.Trim());
            }
            else
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string? configuredHome = _homeDirectory ?? Environment.GetEnvironmentVariable("KIMI_SHARE_DIR");
                if (string.IsNullOrWhiteSpace(configuredHome))
                    configuredHome = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
                string root = !string.IsNullOrWhiteSpace(configuredHome) ? ExpandHome(configuredHome, home) : Path.Combine(home, ".kimi");
                using var credentials = await ReadCredentialsAsync(root, string.IsNullOrWhiteSpace(configuredHome), home, deadline.Token).ConfigureAwait(false);
                root = credentials.Home;
                var login = credentials.Document.RootElement;
                if (login.ValueKind != JsonValueKind.Object)
                    return Failure(UsageProviderState.Error, "Kimi Code credentials are malformed. Sign in again in the official Kimi CLI.");
                string? token = AiUsageHttp.GetString(login, "access_token");
                if (string.IsNullOrWhiteSpace(token))
                    return MissingLogin();
                if (!TryNumber(login, "expires_at", out double expiresAt) || expiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60)
                    return Failure(UsageProviderState.NotSignedIn, "Kimi CLI login is expired or has no verifiable expiry. Open kimi to renew it, then refresh; V-Notch never uses the refresh token.");
                string deviceId;
                try
                {
                    deviceId = await ReadDeviceIdAsync(Path.Combine(root, "device_id"), deadline.Token).ConfigureAwait(false);
                }
                catch (FileNotFoundException)
                {
                    return Failure(UsageProviderState.Unavailable, "Kimi CLI device identity is missing. Open the official Kimi CLI once, then refresh; V-Notch will not create or modify login files.");
                }
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Add("X-Msh-Platform", "kimi_cli");
                request.Headers.Add("X-Msh-Device-Id", deviceId);
                request.Headers.Add("X-Msh-Device-Name", AsciiHeader(Environment.MachineName));
                request.Headers.Add("X-Msh-Device-Model", AsciiHeader($"Windows {Environment.OSVersion.Version} {RuntimeInformation.OSArchitecture}"));
                request.Headers.Add("X-Msh-Os-Version", AsciiHeader(Environment.OSVersion.VersionString));
            }

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return Failure(UsageProviderState.NotSignedIn, explicitKey
                    ? "Kimi rejected KIMI_CODE_API_KEY. Use a Kimi Code subscription key, not a Moonshot Open Platform key."
                    : "Kimi rejected the saved CLI login. Open kimi to renew it, then refresh.");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return Failure(UsageProviderState.Unavailable, "Kimi denied subscription usage access (HTTP 403); this can indicate permission or quota restrictions.");
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Failure(UsageProviderState.Unavailable, "Kimi Code subscription usage is not available for this account or endpoint.");
            if (!response.IsSuccessStatusCode)
                return Failure(UsageProviderState.Error, $"Kimi usage request failed (HTTP {(int)response.StatusCode}).");
            using var document = await AiUsageHttp.ReadJsonAsync(response, deadline.Token).ConfigureAwait(false);
            var windows = ParseWindows(document.RootElement);
            return windows.Count == 0
                ? Failure(UsageProviderState.Unavailable, "Kimi returned no measurable subscription quota windows; API balances and unknown limits are not treated as subscription allowance.")
                : new ProviderUsage(Id, DisplayName, UsageProviderState.Available, "Kimi Code subscription usage", windows, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(UsageProviderState.Error, "Kimi usage request timed out.");
        }
        catch (FileNotFoundException) { return MissingLogin(); }
        catch (DirectoryNotFoundException) { return MissingLogin(); }
        catch (UnauthorizedAccessException) { return Failure(UsageProviderState.Error, "Kimi CLI credentials cannot be read. Check their file permissions."); }
        catch (JsonException) { return Failure(UsageProviderState.Error, "Kimi credentials or usage response contain malformed JSON."); }
        catch (IOException) { return Failure(UsageProviderState.Error, "Kimi CLI credentials, device identity, or usage response could not be read."); }
        catch (HttpRequestException) { return Failure(UsageProviderState.Error, "Kimi usage could not be reached. Check the network and online AI privacy settings."); }
        catch (ArgumentException) { return Failure(UsageProviderState.Error, "Kimi login configuration is invalid."); }
        catch (FormatException) { return Failure(UsageProviderState.Error, "Kimi saved login or device identity is malformed."); }
    }

    internal static IReadOnlyList<UsageWindow> ParseWindows(JsonElement root)
    {
        var windows = new List<UsageWindow>(4);
        if (root.ValueKind != JsonValueKind.Object)
            return windows;
        if (root.TryGetProperty("usages", out var pools) && pools.ValueKind == JsonValueKind.Object)
        {
            AddRatio("limit_5h", "5 hours");
            AddRatio("limit_7d", "Weekly");
            AddRatio("limit_month_total", "Monthly total");
            // limit_month_code is a share of the same monthly total, not a separate allowance.
        }
        if (root.TryGetProperty("usage", out var weekly) && TryCountWindow(weekly, "Weekly", out var weeklyWindow))
            AddLegacy(weeklyWindow!);
        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var limit in limits.EnumerateArray())
            {
                if (limit.ValueKind != JsonValueKind.Object || !limit.TryGetProperty("detail", out var detail)
                    || !limit.TryGetProperty("window", out var window) || window.ValueKind != JsonValueKind.Object
                    || !TryNumber(window, "duration", out double duration) || duration <= 0)
                    continue;
                double minutes = AiUsageHttp.GetString(window, "timeUnit") switch
                {
                    "TIME_UNIT_MINUTE" => duration,
                    "TIME_UNIT_HOUR" => duration * 60,
                    "TIME_UNIT_DAY" => duration * 1440,
                    _ => double.NaN
                };
                if (!double.IsFinite(minutes))
                    continue;
                string label = minutes == 300 ? "5 hours" : minutes == 10080 ? "Weekly"
                    : minutes % 1440 == 0 ? $"{(minutes / 1440).ToString("G", CultureInfo.InvariantCulture)} days"
                    : minutes % 60 == 0 ? $"{(minutes / 60).ToString("G", CultureInfo.InvariantCulture)} hours"
                    : $"{minutes.ToString("G", CultureInfo.InvariantCulture)} minutes";
                if (TryCountWindow(detail, label, out var countWindow))
                    AddLegacy(countWindow!);
            }
        }
        return windows;

        void AddRatio(string key, string name)
        {
            if (!pools.TryGetProperty(key, out var pool) || pool.ValueKind != JsonValueKind.Object
                || !pool.TryGetProperty("used_ratio", out var ratio) || ratio.ValueKind != JsonValueKind.Number
                || !ratio.TryGetDouble(out double usedRatio) || !double.IsFinite(usedRatio) || usedRatio < 0 || usedRatio > 1)
                return;
            windows.Add(new UsageWindow(name, usedRatio * 100, ParseReset(pool)));
        }

        void AddLegacy(UsageWindow window)
        {
            int existingIndex = windows.FindIndex(existing => existing.Name == window.Name);
            if (existingIndex < 0)
            {
                windows.Add(window);
                return;
            }
            var existing = windows[existingIndex];
            // Borrow a more constrained legacy count only when it describes the same reset period.
            if (existing.ResetsAt is { } existingReset && window.ResetsAt is { } legacyReset
                && Math.Abs((existingReset - legacyReset).TotalSeconds) <= 2 && window.UsedPercent > existing.UsedPercent)
                windows[existingIndex] = window;
        }
    }

    private static bool TryCountWindow(JsonElement detail, string name, out UsageWindow? window)
    {
        window = null;
        if (detail.ValueKind != JsonValueKind.Object || !TryNumber(detail, "limit", out double limit) || limit <= 0)
            return false;
        if (!TryNumber(detail, "used", out double used))
        {
            if (!TryNumber(detail, "remaining", out double remaining) || remaining < 0 || remaining > limit)
                return false;
            used = limit - remaining;
        }
        if (used < 0 || used > limit)
            return false;
        window = new UsageWindow(name, used / limit * 100, ParseReset(detail));
        return true;
    }

    private static bool TryNumber(JsonElement value, string key, out double number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field)
            && (field.ValueKind == JsonValueKind.Number && field.TryGetDouble(out number)
                || field.ValueKind == JsonValueKind.String && double.TryParse(field.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            && double.IsFinite(number);
    }

    private static DateTimeOffset? ParseReset(JsonElement value)
    {
        string? text = AiUsageHttp.GetString(value, "resetTime") ?? AiUsageHttp.GetString(value, "resetAt")
            ?? AiUsageHttp.GetString(value, "reset_time") ?? AiUsageHttp.GetString(value, "reset_at");
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var reset) ? reset : null;
    }

    private static Uri? ResolveEndpoint(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return ChinaUsageEndpoint;
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath.TrimEnd('/') is not ("" or "/coding" or "/coding/v1"))
            return null;
        return uri.Host.ToLowerInvariant() switch
        {
            "api.kimi.com" => ChinaUsageEndpoint,
            "api.kimi.ai" => InternationalUsageEndpoint,
            _ => null
        };
    }

    private static bool HasCustomOAuthHost() =>
        IsCustomHost(Environment.GetEnvironmentVariable("KIMI_CODE_OAUTH_HOST"))
        || IsCustomHost(Environment.GetEnvironmentVariable("KIMI_OAUTH_HOST"));

    private static bool IsCustomHost(string? host) => !string.IsNullOrWhiteSpace(host)
        && !string.Equals(host.TrimEnd('/'), "https://auth.kimi.com", StringComparison.OrdinalIgnoreCase);

    private static async Task<CredentialDocument> ReadCredentialsAsync(string root, bool allowLegacyHome, string home, CancellationToken token)
    {
        try
        {
            return new CredentialDocument(root, await AiUsageHttp.ReadLocalJsonAsync(Path.Combine(root, "credentials", "kimi-code.json"), token).ConfigureAwait(false));
        }
        catch (Exception exception) when (allowLegacyHome && exception is FileNotFoundException or DirectoryNotFoundException)
        {
            string legacyRoot = Path.Combine(home, ".kimi-code");
            return new CredentialDocument(legacyRoot, await AiUsageHttp.ReadLocalJsonAsync(Path.Combine(legacyRoot, "credentials", "kimi-code.json"), token).ConfigureAwait(false));
        }
    }

    private static async Task<string> ReadDeviceIdAsync(string path, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024, FileOptions.Asynchronous);
        byte[] buffer = new byte[1025];
        int count = 0;
        while (count < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(count), token).ConfigureAwait(false);
            if (read == 0)
                break;
            count += read;
        }
        string deviceId = Encoding.UTF8.GetString(buffer, 0, count).Trim();
        if (count == buffer.Length || deviceId.Length == 0 || deviceId.Any(character => character < 33 || character > 126))
            throw new FormatException("Invalid device identity.");
        return deviceId;
    }

    private static string AsciiHeader(string value)
    {
        foreach (char character in value)
        {
            if (character is < ' ' or > '~')
                return string.Create(value.Length, value, static (target, source) =>
                {
                    for (int index = 0; index < source.Length; index++)
                        target[index] = source[index] is >= ' ' and <= '~' ? source[index] : '?';
                });
        }
        return value;
    }

    private static string ExpandHome(string path, string home) => path == "~" ? home
        : path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(home, path[2..]) : path;

    private ProviderUsage MissingLogin() => Failure(UsageProviderState.NotSignedIn,
        "Sign in in the official Kimi CLI, or set KIMI_CODE_API_KEY. Browser/Desktop-only sessions and Moonshot Open Platform keys are not subscription login sources here.");

    private ProviderUsage Failure(UsageProviderState state, string status) => AiUsageHttp.Failure(Id, DisplayName, state, status);

    private sealed record CredentialDocument(string Home, JsonDocument Document) : IDisposable
    {
        public void Dispose() => Document.Dispose();
    }

    public void Dispose() => _client.Dispose();
}
