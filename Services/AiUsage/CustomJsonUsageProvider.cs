using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace VNotch.Services.AiUsage;

/// <summary>
/// Opt-in HTTPS source returning {"windows":[{"name":"Weekly","usedPercent":42.5,"resetsAt":"2026-10-30T00:00:00Z"}]}.
/// usedPercent is a finite number from 0 through 100. resetsAt may be absent or null; windows must be nonempty.
/// Credentials are referenced only by an environment-variable name, never embedded in definitions or URLs.
/// </summary>
public sealed record CustomUsageProviderDefinition(
    string Id,
    string DisplayName,
    Uri Endpoint,
    string? BearerTokenEnvironmentVariable = null);

public sealed class CustomJsonUsageProvider : IAiUsageProvider, IDisposable
{
    private readonly CustomUsageProviderDefinition _definition;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public string Id => _definition.Id;
    public string DisplayName => _definition.DisplayName;

    public CustomJsonUsageProvider(CustomUsageProviderDefinition definition)
        : this(definition, null) { }

    internal CustomJsonUsageProvider(CustomUsageProviderDefinition definition, HttpClient? http)
    {
        ValidateDefinition(definition);
        _definition = definition;
        _http = http ?? AiUsageHttp.CreateClient();
        _ownsHttp = http is null;
    }

    public async Task<ProviderUsage> FetchAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(AiUsageHttp.RequestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, _definition.Endpoint);
            request.Headers.UserAgent.ParseAdd("VNotch/usage");
            if (_definition.BearerTokenEnvironmentVariable is { } variable)
            {
                string? token = Environment.GetEnvironmentVariable(variable);
                if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace))
                    return AiUsageHttp.Failure(Id, DisplayName, UsageProviderState.NotSignedIn,
                        "The configured bearer-token environment variable is missing or invalid.");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return AiUsageHttp.Failure(Id, DisplayName, UsageProviderState.NotSignedIn, "The configured usage source rejected authentication.");
            if (!response.IsSuccessStatusCode)
                return AiUsageHttp.Failure(Id, DisplayName, UsageProviderState.Unavailable,
                    $"Usage source unavailable (HTTP {(int)response.StatusCode}).");
            using var document = await AiUsageHttp.ReadJsonAsync(response, timeout.Token).ConfigureAwait(false);
            return ParseUsage(_definition, document.RootElement);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AiUsageHttp.Failure(Id, DisplayName, UsageProviderState.Unavailable, "Usage request timed out or network permission changed.");
        }
        catch (HttpRequestException)
        {
            return AiUsageHttp.Failure(Id, DisplayName, UsageProviderState.Unavailable, "Cannot reach usage source. Check network and privacy settings.");
        }
        catch (FormatException)
        {
            return AiUsageHttp.Failure(Id, DisplayName, UsageProviderState.NotSignedIn, "The configured bearer-token environment variable is invalid.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or System.IO.IOException)
        {
            return AiUsageHttp.Failure(Id, DisplayName, UsageProviderState.Error, "Usage source returned invalid normalized usage data.");
        }
    }

    public static ProviderUsage ParseUsage(CustomUsageProviderDefinition definition, JsonElement root, DateTimeOffset? updatedAt = null)
    {
        ValidateDefinition(definition);
        try
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("windows", out var items) ||
                items.ValueKind != JsonValueKind.Array || items.GetArrayLength() is < 1 or > 64) throw new JsonException();
            var windows = new List<UsageWindow>(items.GetArrayLength());
            foreach (var item in items.EnumerateArray())
            {
                string? name = AiUsageHttp.GetString(item, "name");
                if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl) ||
                    !item.TryGetProperty("usedPercent", out var used) || used.ValueKind != JsonValueKind.Number ||
                    !used.TryGetDouble(out double percent) || !double.IsFinite(percent) || percent < 0 || percent > 100)
                    throw new JsonException();
                DateTimeOffset? resetsAt = null;
                if (item.TryGetProperty("resetsAt", out var reset) && reset.ValueKind != JsonValueKind.Null)
                {
                    if (reset.ValueKind != JsonValueKind.String || !reset.TryGetDateTimeOffset(out var timestamp)) throw new JsonException();
                    resetsAt = timestamp;
                }
                windows.Add(new UsageWindow(name, percent, resetsAt));
            }
            return new ProviderUsage(definition.Id, definition.DisplayName, UsageProviderState.Available,
                "Subscription usage", windows, updatedAt ?? DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return AiUsageHttp.Failure(definition.Id, definition.DisplayName, UsageProviderState.Error,
                "Usage source returned invalid normalized usage data.");
        }
    }

    internal static CustomUsageProviderDefinition ReadDefinition(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new JsonException();
        foreach (var property in item.EnumerateObject())
            if (property.Name is not ("id" or "displayName" or "endpoint" or "bearerTokenEnvironmentVariable"))
                throw new JsonException("Unknown usage-provider setting.");
        string? id = AiUsageHttp.GetString(item, "id");
        string? displayName = AiUsageHttp.GetString(item, "displayName");
        string? endpoint = AiUsageHttp.GetString(item, "endpoint");
        string? variable = AiUsageHttp.GetString(item, "bearerTokenEnvironmentVariable");
        if (item.TryGetProperty("bearerTokenEnvironmentVariable", out var auth) &&
            auth.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)) throw new JsonException();
        if (id is null || displayName is null || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) throw new JsonException();
        var definition = new CustomUsageProviderDefinition(id, displayName, uri, variable);
        ValidateDefinition(definition);
        return definition;
    }

    private static void ValidateDefinition(CustomUsageProviderDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrEmpty(definition.Id) || definition.Id.Length > 64 ||
            !char.IsAsciiLetterOrDigit(definition.Id[0]) ||
            definition.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')) ||
            definition.Id is "codex" or "claude" or "kimi" or "usage-configuration" ||
            definition.Id.StartsWith("usage-configuration-", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Custom provider id must be unique, nonreserved ASCII letters, digits, hyphens or underscores.");
        if (string.IsNullOrWhiteSpace(definition.DisplayName) || definition.DisplayName.Length > 100 || definition.DisplayName.Any(char.IsControl))
            throw new ArgumentException("Custom provider display name is invalid.");
        if (definition.Endpoint is not { IsAbsoluteUri: true } endpoint || endpoint.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(endpoint.Host) || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new ArgumentException("Usage endpoint must be HTTPS without credentials, query or fragment.");
        if (definition.BearerTokenEnvironmentVariable is { } variable &&
            (variable.Length is < 1 or > 128 || (!char.IsAsciiLetter(variable[0]) && variable[0] != '_') ||
             variable.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')))
            throw new ArgumentException("Bearer-token environment-variable name is invalid.");
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
