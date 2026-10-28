using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace VNotch.Services.AiUsage;

/// <summary>
/// Refreshes independent provider adapters and discovers opt-in custom HTTPS sources on every refresh.
/// Built-in providers are owned by this service; explicitly supplied adapters remain caller-owned.
/// </summary>
public sealed class AiUsageService : IDisposable
{
    private readonly IAiUsageProvider[] _providers;
    private readonly bool _ownsProviders;
    private readonly string _configurationPath;
    private readonly HttpClient _customHttp = AiUsageHttp.CreateClient();
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;

    public static string DefaultConfigurationPath => Path.Combine(AppPaths.LocalAppDataRoot, "usage-providers.json");

    /// <param name="providers">Null uses Codex, Claude and Kimi; a supplied list replaces the built-ins.</param>
    /// <param name="configurationPath">Optional override for the opt-in custom-source file, reloaded on every refresh.</param>
    public AiUsageService(IEnumerable<IAiUsageProvider>? providers = null, string? configurationPath = null)
    {
        _ownsProviders = providers is null;
        _providers = providers?.ToArray() ?? [new CodexUsageProvider(), new ClaudeUsageProvider(), new KimiUsageProvider()];
        _configurationPath = configurationPath ?? DefaultConfigurationPath;
    }

    public async Task<IReadOnlyList<ProviderUsage>> FetchAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var token = lifetime.Token;
        token.ThrowIfCancellationRequested();
        var tasks = new List<Task<ProviderUsage>>(_providers.Length);
        foreach (var provider in _providers) tasks.Add(FetchOneAsync(provider, token));
        await AddConfiguredProvidersAsync(tasks, token).ConfigureAwait(false);
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return results;
    }

    private static async Task<ProviderUsage> FetchOneAsync(IAiUsageProvider provider, CancellationToken token)
    {
        try
        {
            return await provider.FetchAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return AiUsageHttp.Failure(provider.Id, provider.DisplayName, UsageProviderState.Unavailable, "Usage request timed out or was interrupted.");
        }
        catch (Exception)
        {
            // Never surface exception text: third-party providers may include credentials or response bodies.
            return AiUsageHttp.Failure(provider.Id, provider.DisplayName, UsageProviderState.Error, "Unable to read usage from this provider.");
        }
    }

    private async Task AddConfiguredProvidersAsync(List<Task<ProviderUsage>> tasks, CancellationToken token)
    {
        try
        {
            using var document = await AiUsageHttp.ReadLocalJsonAsync(_configurationPath, 256 * 1024, token).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("providers", out var providers) ||
                providers.ValueKind != JsonValueKind.Array || providers.GetArrayLength() > 64) throw new JsonException();
            foreach (var property in root.EnumerateObject())
                if (property.Name != "providers") throw new JsonException();
            var ids = new HashSet<string>(_providers.Select(provider => provider.Id), StringComparer.OrdinalIgnoreCase);
            int index = 0;
            foreach (var item in providers.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                ++index;
                try
                {
                    var definition = CustomJsonUsageProvider.ReadDefinition(item);
                    if (!ids.Add(definition.Id)) throw new JsonException();
                    // All configured adapters share this service-owned, privacy-aware, redirect-disabled client.
                    var provider = new CustomJsonUsageProvider(definition, _customHttp);
                    tasks.Add(FetchOneAsync(provider, token));
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
                {
                    tasks.Add(Task.FromResult(AiUsageHttp.Failure($"usage-configuration-{index}", $"Custom provider {index}",
                        UsageProviderState.Error, "Invalid or duplicate provider definition. Use HTTPS, a unique id and an environment-variable credential reference only.")));
                }
            }
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            tasks.Add(Task.FromResult(AiUsageHttp.Failure("usage-configuration", "Custom providers", UsageProviderState.Error,
                "Cannot read usage-providers.json. Expected an object with a providers array; credentials must not be embedded.")));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _shutdown.Dispose();
        if (_ownsProviders)
            foreach (var provider in _providers)
                if (provider is IDisposable disposable) disposable.Dispose();
        _customHttp.Dispose();
    }
}
