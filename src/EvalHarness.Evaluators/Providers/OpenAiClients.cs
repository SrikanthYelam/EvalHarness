using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EvalHarness.Core;

namespace EvalHarness.Evaluators.Providers;

/// <summary>An LLM/embedding provider call failed after retries. Evaluators surface this as an evaluator error.</summary>
public sealed class ProviderException(string message, Exception? inner = null) : Exception(message, inner);

internal static class ProviderHttp
{
    public static async Task<JsonDocument> PostJsonAsync(
        HttpClient http, string provider, string url, string apiKey, object payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        try
        {
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                // Auth errors can echo part of the key back, so only report the status for those.
                var detail = (int)response.StatusCode is 401 or 403 ? "" : $": {(body.Length <= 300 ? body : body[..300] + "…")}";
                throw new ProviderException($"{provider} returned HTTP {(int)response.StatusCode}{detail}");
            }
            return JsonDocument.Parse(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or JsonException)
        {
            throw new ProviderException($"{provider} call failed: {ex.Message}", ex);
        }
    }
}

/// <summary>Chat completions against OpenAI or any OpenAI-compatible endpoint (Azure OpenAI proxies, Ollama, vLLM, ...).</summary>
public sealed class OpenAiChatClient(HttpClient http, LlmOptions options) : ILlmClient
{
    private readonly string _apiKey = options.ResolveApiKey();

    public async Task<string> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        var payload = new
        {
            model = options.Model,
            temperature = 0,
            response_format = new { type = "json_object" },
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
        };

        using var json = await ProviderHttp.PostJsonAsync(
            http, "LLM provider", $"{options.BaseUrl.TrimEnd('/')}/chat/completions", _apiKey, payload, cancellationToken);

        if (json.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.String)
            return content.GetString()!;

        throw new ProviderException("LLM provider response had no choices[0].message.content.");
    }
}

public sealed class OpenAiEmbeddingClient(HttpClient http, EmbeddingOptions options) : IEmbeddingClient
{
    private readonly string _apiKey = options.ResolveApiKey();

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        var payload = new { model = options.Model, input = text };
        using var json = await ProviderHttp.PostJsonAsync(
            http, "Embedding provider", $"{options.BaseUrl.TrimEnd('/')}/embeddings", _apiKey, payload, cancellationToken);

        if (json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 &&
            data[0].TryGetProperty("embedding", out var embedding) && embedding.ValueKind == JsonValueKind.Array)
            return embedding.EnumerateArray().Select(e => e.GetSingle()).ToArray();

        throw new ProviderException("Embedding provider response had no data[0].embedding.");
    }
}

/// <summary>Caps simultaneous LLM calls across all evaluators and parallel test cases, to respect provider rate limits.</summary>
public sealed class ConcurrencyLimitedLlmClient(ILlmClient inner, int maxConcurrency) : ILlmClient
{
    private readonly SemaphoreSlim _gate = new(Math.Max(1, maxConcurrency));

    public async Task<string> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await inner.CompleteJsonAsync(systemPrompt, userPrompt, cancellationToken); }
        finally { _gate.Release(); }
    }
}

public sealed class ConcurrencyLimitedEmbeddingClient(IEmbeddingClient inner, int maxConcurrency) : IEmbeddingClient
{
    private readonly SemaphoreSlim _gate = new(Math.Max(1, maxConcurrency));

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await inner.EmbedAsync(text, cancellationToken); }
        finally { _gate.Release(); }
    }
}
