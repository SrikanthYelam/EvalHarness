using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EvalHarness.Core;
using EvalHarness.Core.Http;
using Microsoft.Extensions.Logging;

namespace EvalHarness.RagClient;

/// <summary>
/// Calls the RAG API over HTTP. Retries and per-attempt timeouts come from <see cref="RetryHandler"/> in the
/// HttpClient pipeline; this class turns whatever is left into a <see cref="RagApiException"/>.
/// </summary>
public sealed class HttpRagClient(
    HttpClient http,
    RagApiOptions options,
    ILogger<HttpRagClient>? logger = null,
    TimeSpan? readinessPollInterval = null) : IRagClient
{
    private readonly RagApiContract _contract = new(options);
    private readonly TimeSpan _pollInterval = readinessPollInterval ?? TimeSpan.FromSeconds(2);

    public async Task<RagResponse> AskAsync(string question, CancellationToken cancellationToken)
    {
        var url = UrlFor(options.AskPath);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(_contract.BuildRequestBody(question), Encoding.UTF8, "application/json"),
        };
        AddHeaders(request);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new RagApiException($"RAG API returned HTTP {(int)response.StatusCode}: {Truncate(body)}", (int)response.StatusCode);

            using var json = JsonDocument.Parse(body);
            var (answer, status, chunks) = _contract.Parse(json.RootElement);

            var info = request.Options.TryGetValue(RetryInfo.Key, out var retry) ? retry : null;
            var latency = (info?.LastAttemptDuration ?? stopwatch.Elapsed).TotalMilliseconds;
            return new RagResponse(question, answer, status, chunks, latency, info?.Attempts ?? 1);
        }
        catch (HttpRequestException ex)
        {
            throw new RagApiException($"Could not reach the RAG API at {url}: {ex.Message}", (int?)ex.StatusCode, ex);
        }
        catch (TimeoutException ex)
        {
            throw new RagApiException($"The RAG API at {url} timed out: {ex.Message}", inner: ex);
        }
        catch (JsonException ex)
        {
            throw new RagApiException($"The RAG API returned invalid JSON: {ex.Message}", inner: ex);
        }
    }

    public async Task WaitUntilReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var url = UrlFor(options.ReadinessPath);
        var deadline = Stopwatch.StartNew();
        string lastProblem = "no response";

        while (true)
        {
            using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                AddHeaders(request);
                using var response = await http.SendAsync(request, probeTimeout.Token);
                if ((int)response.StatusCode < 500) return;
                lastProblem = $"HTTP {(int)response.StatusCode}";
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                lastProblem = ex.Message;
            }

            if (deadline.Elapsed + _pollInterval >= timeout)
                throw new RagApiException($"The RAG API at {url} was not ready after {timeout.TotalSeconds:0.#}s (last: {lastProblem}).");

            logger?.LogInformation("Waiting for the RAG API at {Url} ({Problem})", url, lastProblem);
            await Task.Delay(_pollInterval, cancellationToken);
        }
    }

    private string UrlFor(string path) => $"{options.BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

    private void AddHeaders(HttpRequestMessage request)
    {
        foreach (var (name, value) in options.Headers) request.Headers.TryAddWithoutValidation(name, value);
    }

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300] + "…";
}
