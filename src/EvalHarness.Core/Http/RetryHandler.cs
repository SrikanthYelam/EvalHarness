using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;

namespace EvalHarness.Core.Http;

/// <summary>Filled in by <see cref="RetryHandler"/>: how many attempts were made and how long the last one took.</summary>
public sealed class RetryInfo
{
    public static readonly HttpRequestOptionsKey<RetryInfo> Key = new("EvalHarness.RetryInfo");

    public int Attempts { get; set; }
    public TimeSpan LastAttemptDuration { get; set; }
}

public sealed class RetryPolicy
{
    /// <summary>Retries after the first attempt.</summary>
    public int MaxRetries { get; init; } = 3;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Applied to each attempt, not to the whole retry sequence.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Retries transient failures (408, 429, 500, 502, 503, 504, connection errors, per-attempt timeouts) with
/// exponential backoff and jitter, honouring Retry-After. Used for both the RAG API and the LLM/embedding providers.
/// After the last attempt the final response is returned (or the final exception thrown) for the caller to interpret.
/// </summary>
public sealed class RetryHandler(RetryPolicy policy, ILogger? logger = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    : DelegatingHandler
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // A request cannot be sent twice, so buffer the body once and rebuild the message for every attempt.
        byte[]? body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var info = new RetryInfo();
        request.Options.Set(RetryInfo.Key, info);

        for (var attempt = 1; ; attempt++)
        {
            var last = attempt > policy.MaxRetries;
            using var attemptRequest = Clone(request, body);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(policy.AttemptTimeout);
            var stopwatch = Stopwatch.StartNew();
            info.Attempts = attempt;

            HttpResponseMessage? response = null;
            Exception? failure = null;
            try
            {
                response = await base.SendAsync(attemptRequest, timeout.Token);
                // Buffer inside the timeout so a stalled body is also retried.
                await response.Content.ReadAsByteArrayAsync(timeout.Token);
            }
            catch (HttpRequestException ex)
            {
                failure = ex;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                failure = new TimeoutException($"Request timed out after {policy.AttemptTimeout.TotalSeconds:0.#}s.", ex);
            }
            finally
            {
                info.LastAttemptDuration = stopwatch.Elapsed;
            }

            if (failure is null && !IsTransient(response!.StatusCode)) return response;
            if (last)
            {
                if (failure is not null) throw failure;
                return response!;
            }

            var wait = BackoffFor(attempt, response);
            logger?.LogWarning("Transient failure calling {Url} (attempt {Attempt}/{Max}): {Reason}. Retrying in {DelayMs}ms",
                request.RequestUri, attempt, policy.MaxRetries + 1, failure?.Message ?? $"HTTP {(int)response!.StatusCode}",
                (int)wait.TotalMilliseconds);
            response?.Dispose();
            await _delay(wait, cancellationToken);
        }
    }

    private TimeSpan BackoffFor(int attempt, HttpResponseMessage? response)
    {
        var retryAfter = response?.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta) return Min(delta, policy.MaxDelay);
        if (retryAfter?.Date is { } date) return Min(date - DateTimeOffset.UtcNow, policy.MaxDelay);

        var exponential = policy.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        var jitter = 1 + Random.Shared.NextDouble() * 0.25;
        return Min(TimeSpan.FromMilliseconds(exponential * jitter), policy.MaxDelay);
    }

    private static TimeSpan Min(TimeSpan value, TimeSpan max) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value > max ? max : value;

    private static HttpRequestMessage Clone(HttpRequestMessage original, byte[]? body)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri) { Version = original.Version };
        foreach (var header in original.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (body is not null)
        {
            clone.Content = new ByteArrayContent(body);
            clone.Content.Headers.ContentType = original.Content!.Headers.ContentType;
        }
        return clone;
    }
}
