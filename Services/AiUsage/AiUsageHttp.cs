using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace VNotch.Services.AiUsage;

internal static class AiUsageHttp
{
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private const int MaximumResponseBytes = 1024 * 1024;

    internal static HttpClient CreateClient() => new(NetworkPrivacy.Handler(NetworkFeature.Ai,
        new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false }))
    {
        Timeout = RequestTimeout
    };

    internal static ProviderUsage Failure(string id, string name, UsageProviderState state, string status) =>
        new(id, name, state, status, Array.Empty<UsageWindow>(), null);

    internal static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            throw new JsonException("Usage response exceeds the size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return await ReadJsonAsync(stream, MaximumResponseBytes, token).ConfigureAwait(false);
    }
    internal static Task<JsonDocument> ReadLocalJsonAsync(string path, CancellationToken token) =>
        ReadLocalJsonAsync(path, MaximumResponseBytes, token);


    internal static async Task<JsonDocument> ReadLocalJsonAsync(string path, int maximumBytes, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maximumBytes) throw new JsonException("Local JSON exceeds the size limit.");
        return await ReadJsonAsync(stream, maximumBytes, token).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReadJsonAsync(Stream stream, int maximumBytes, CancellationToken token)
    {
        using var memory = new MemoryStream();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length,
                    maximumBytes - (int)memory.Length + 1)), token).ConfigureAwait(false);
                if (read == 0) break;
                if (memory.Length + read > maximumBytes) throw new JsonException("JSON exceeds the size limit.");
                memory.Write(buffer, 0, read);
            }
            token.ThrowIfCancellationRequested();
            // JsonDocument retains this MemoryStream-owned buffer, never the rented read buffer.
            return JsonDocument.Parse(memory.GetBuffer().AsMemory(0, (int)memory.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
