using System.Net;
using EvalHarness.Core;
using EvalHarness.Core.Http;
using EvalHarness.RagClient;

namespace EvalHarness.Tests;

public class RetryHandlerTests
{
    private static HttpClient Client(StubHandler inner, int maxRetries = 3, List<TimeSpan>? delays = null, TimeSpan? attemptTimeout = null)
    {
        var handler = new RetryHandler(
            new RetryPolicy { MaxRetries = maxRetries, BaseDelay = TimeSpan.FromMilliseconds(100), AttemptTimeout = attemptTimeout ?? TimeSpan.FromSeconds(30) },
            delay: (d, _) => { delays?.Add(d); return Task.CompletedTask; })
        { InnerHandler = inner };
        return new HttpClient(handler);
    }

    private static HttpRequestMessage Post(string body = """{"q":1}""") =>
        new(HttpMethod.Post, "http://rag.test/ask") { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task Transient_status_is_retried_until_success(HttpStatusCode transient)
    {
        var inner = new StubHandler((_, call) => Task.FromResult(StubHandler.JsonResponse(call < 3 ? transient : HttpStatusCode.OK, "{}")));

        using var response = await Client(inner).SendAsync(Post());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task Request_body_is_resent_on_every_attempt()
    {
        var inner = new StubHandler((_, call) => Task.FromResult(StubHandler.JsonResponse(call < 2 ? HttpStatusCode.BadGateway : HttpStatusCode.OK, "{}")));

        using var _ = await Client(inner).SendAsync(Post("""{"question":"hello"}"""));

        Assert.Equal(2, inner.Bodies.Count);
        Assert.All(inner.Bodies, b => Assert.Equal("""{"question":"hello"}""", b));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Client_errors_are_not_retried(HttpStatusCode status)
    {
        var inner = StubHandler.Json(status, "{}");

        using var response = await Client(inner).SendAsync(Post());

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Gives_up_after_max_retries_and_returns_the_last_response()
    {
        var inner = StubHandler.Json(HttpStatusCode.ServiceUnavailable, "{}");

        using var response = await Client(inner, maxRetries: 2).SendAsync(Post());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(3, inner.Calls); // first attempt + 2 retries
    }

    [Fact]
    public async Task Connection_errors_are_retried_then_rethrown()
    {
        var inner = new StubHandler((_, _) => throw new HttpRequestException("connection refused"));

        await Assert.ThrowsAsync<HttpRequestException>(() => Client(inner, maxRetries: 2).SendAsync(Post()));

        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task Backoff_grows_exponentially()
    {
        var delays = new List<TimeSpan>();
        var inner = StubHandler.Json(HttpStatusCode.InternalServerError, "{}");

        using var _ = await Client(inner, maxRetries: 3, delays).SendAsync(Post());

        Assert.Equal(3, delays.Count);
        // 100ms, 200ms, 400ms, each with up to +25% jitter.
        Assert.InRange(delays[0].TotalMilliseconds, 100, 125);
        Assert.InRange(delays[1].TotalMilliseconds, 200, 250);
        Assert.InRange(delays[2].TotalMilliseconds, 400, 500);
    }

    [Fact]
    public async Task Retry_after_header_is_honoured()
    {
        var delays = new List<TimeSpan>();
        var inner = new StubHandler((_, call) =>
        {
            var response = StubHandler.JsonResponse(call == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK, "{}");
            if (call == 1) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return Task.FromResult(response);
        });

        using var _ = await Client(inner, delays: delays).SendAsync(Post());

        Assert.Equal([TimeSpan.FromSeconds(7)], delays);
    }

    [Fact]
    public async Task Attempt_count_and_duration_are_reported()
    {
        var inner = new StubHandler((_, call) => Task.FromResult(StubHandler.JsonResponse(call < 3 ? HttpStatusCode.BadGateway : HttpStatusCode.OK, "{}")));
        using var request = Post();

        using var _ = await Client(inner).SendAsync(request);

        Assert.True(request.Options.TryGetValue(RetryInfo.Key, out var info));
        Assert.Equal(3, info!.Attempts);
    }

    [Fact]
    public async Task A_slow_attempt_times_out_and_is_retried()
    {
        var inner = new StubHandler(async (req, call) =>
        {
            if (call == 1) await Task.Delay(TimeSpan.FromSeconds(10)); // cancelled by the per-attempt timeout
            return StubHandler.JsonResponse(HttpStatusCode.OK, "{}");
        });
        // The stub ignores its token, so use a handler that observes it.
        var observing = new CancellableHandler(inner);
        var handler = new RetryHandler(
            new RetryPolicy { MaxRetries = 1, BaseDelay = TimeSpan.Zero, AttemptTimeout = TimeSpan.FromMilliseconds(100) },
            delay: (_, _) => Task.CompletedTask)
        { InnerHandler = observing };

        using var response = await new HttpClient(handler).SendAsync(Post());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Calls);
    }

    private sealed class CancellableHandler(StubHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var send = base.SendAsync(request, CancellationToken.None);
            var cancelled = Task.Delay(Timeout.Infinite, cancellationToken);
            if (await Task.WhenAny(send, cancelled) == cancelled) cancellationToken.ThrowIfCancellationRequested();
            return await send;
        }
    }

    [Fact]
    public async Task Caller_cancellation_stops_retrying_immediately()
    {
        using var cts = new CancellationTokenSource();
        var inner = new StubHandler((_, _) =>
        {
            cts.Cancel();
            return Task.FromResult(StubHandler.JsonResponse(HttpStatusCode.ServiceUnavailable, "{}"));
        });
        var handler = new RetryHandler(
            new RetryPolicy { MaxRetries = 5, BaseDelay = TimeSpan.Zero },
            delay: (d, ct) => Task.Delay(Timeout.Infinite, ct))
        { InnerHandler = inner };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HttpClient(handler).SendAsync(Post(), cts.Token));

        Assert.Equal(1, inner.Calls);
    }
}

public class HttpRagClientTests
{
    private const string DocRagResponse = """
        {
          "question": "How many vacation days?",
          "status": "Answered",
          "answer": "18 days per year [1].",
          "sources": [],
          "retrievedChunks": [
            { "id": "c-1", "score": 0.032, "vectorScore": 0.81, "source": "Hybrid", "sourceFile": "acme-handbook.md", "page": null,
              "headingPath": ["Time Off", "Vacation"], "text": "Full-time employees accrue 1.5 vacation days per month." },
            { "id": "c-2", "score": 0.016, "source": "Keyword", "sourceFile": "other.md", "text": "Other text." }
          ]
        }
        """;

    private static HttpRagClient Client(StubHandler handler, RagApiOptions? options = null) =>
        new(new HttpClient(handler), options ?? new RagApiOptions { BaseUrl = "http://rag.test" }, readinessPollInterval: TimeSpan.FromMilliseconds(10));

    [Fact]
    public async Task Maps_the_default_docrag_response()
    {
        var response = await Client(StubHandler.Json(HttpStatusCode.OK, DocRagResponse)).AskAsync("How many vacation days?", default);

        Assert.Equal("18 days per year [1].", response.Answer);
        Assert.Equal("Answered", response.Status);
        Assert.Equal(2, response.RetrievedChunks.Count);
        var first = response.RetrievedChunks[0];
        Assert.Equal((1, "c-1", "acme-handbook.md"), (first.Rank, first.ChunkId, first.DocumentName));
        Assert.Contains("1.5 vacation days", first.Text);
        Assert.Equal(0.032, first.Score);
        Assert.Equal(2, response.RetrievedChunks[1].Rank);
    }

    [Fact]
    public async Task Insufficient_context_has_a_null_answer_but_is_still_a_valid_response()
    {
        var body = """{"question":"q","status":"InsufficientContext","answer":null,"sources":[],"retrievedChunks":[]}""";

        var response = await Client(StubHandler.Json(HttpStatusCode.OK, body)).AskAsync("q", default);

        Assert.Null(response.Answer);
        Assert.Equal("InsufficientContext", response.Status);
        Assert.Empty(response.RetrievedChunks);
    }

    [Fact]
    public async Task Request_body_uses_configured_fields_and_types()
    {
        var handler = StubHandler.Json(HttpStatusCode.OK, DocRagResponse);
        var options = new RagApiOptions
        {
            BaseUrl = "http://rag.test/", QuestionField = "query",
            RequestFields = { ["topK"] = "7", ["mode"] = "Hybrid", ["rerank"] = "true" },
        };

        await Client(handler, options).AskAsync("hi?", default);

        Assert.Equal("""{"query":"hi?","topK":7,"mode":"Hybrid","rerank":true}""", handler.Bodies.Single());
    }

    [Fact]
    public async Task Url_and_headers_come_from_options()
    {
        HttpRequestMessage? seen = null;
        var handler = new StubHandler((req, _) => { seen = req; return Task.FromResult(StubHandler.JsonResponse(HttpStatusCode.OK, DocRagResponse)); });
        var options = new RagApiOptions { BaseUrl = "https://rag.example/api/", AskPath = "/v2/ask", Headers = { ["Authorization"] = "Bearer abc" } };

        await Client(handler, options).AskAsync("q", default);

        Assert.Equal("https://rag.example/api/v2/ask", seen!.RequestUri!.ToString());
        Assert.Equal("Bearer abc", seen.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Another_api_shape_can_be_mapped_through_configuration()
    {
        var body = """{"result":{"text":"Yes."},"contexts":[{"doc":{"id":"d1","name":"x.pdf"},"content":"Chunk","similarity":0.9}]}""";
        var options = new RagApiOptions
        {
            BaseUrl = "http://rag.test", AnswerPath = "result.text", StatusPath = null, ChunksPath = "contexts",
            ChunkIdField = null, DocumentIdField = "doc.id", DocumentNameField = "doc.name", ChunkTextField = "content", ChunkScoreField = "similarity",
        };

        var response = await Client(StubHandler.Json(HttpStatusCode.OK, body), options).AskAsync("q", default);

        Assert.Equal("Yes.", response.Answer);
        var chunk = Assert.Single(response.RetrievedChunks);
        Assert.Equal(("d1", "x.pdf", "Chunk", 0.9), (chunk.DocumentId, chunk.DocumentName, chunk.Text, chunk.Score));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Http_errors_become_rag_api_exceptions_with_the_status(HttpStatusCode status)
    {
        var ex = await Assert.ThrowsAsync<RagApiException>(() => Client(StubHandler.Json(status, "oops")).AskAsync("q", default));

        Assert.Equal((int)status, ex.StatusCode);
        Assert.Contains("oops", ex.Message);
    }

    [Fact]
    public async Task Unreachable_api_becomes_a_rag_api_exception()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("connection refused"));

        var ex = await Assert.ThrowsAsync<RagApiException>(() => Client(handler).AskAsync("q", default));

        Assert.Contains("connection refused", ex.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"unrelated": true}""")]
    [InlineData("""{"answer": "x", "retrievedChunks": "not-an-array"}""")]
    public async Task Unusable_payload_becomes_a_rag_api_exception(string body) =>
        await Assert.ThrowsAsync<RagApiException>(() => Client(StubHandler.Json(HttpStatusCode.OK, body)).AskAsync("q", default));

    [Fact]
    public async Task Cancellation_propagates_as_cancellation_not_as_an_api_failure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new StubHandler((_, _) => throw new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(handler).AskAsync("q", cts.Token));
    }

    [Fact]
    public async Task Latency_and_attempts_come_from_the_retry_handler()
    {
        var inner = new StubHandler((_, call) => Task.FromResult(StubHandler.JsonResponse(call < 2 ? HttpStatusCode.BadGateway : HttpStatusCode.OK, DocRagResponse)));
        var pipeline = new RetryHandler(new RetryPolicy { BaseDelay = TimeSpan.Zero }, delay: (_, _) => Task.CompletedTask) { InnerHandler = inner };
        var client = new HttpRagClient(new HttpClient(pipeline), new RagApiOptions { BaseUrl = "http://rag.test" });

        var response = await client.AskAsync("q", default);

        Assert.Equal(2, response.Attempts);
        Assert.True(response.LatencyMs >= 0);
    }

    [Fact]
    public async Task Wait_until_ready_polls_until_the_api_answers()
    {
        var handler = new StubHandler((_, call) => Task.FromResult(StubHandler.JsonResponse(call < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, "{}")));

        await Client(handler).WaitUntilReadyAsync(TimeSpan.FromSeconds(5), default);

        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task Wait_until_ready_gives_up_after_the_timeout()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("refused"));

        var ex = await Assert.ThrowsAsync<RagApiException>(() => Client(handler).WaitUntilReadyAsync(TimeSpan.FromMilliseconds(100), default));

        Assert.Contains("not ready", ex.Message);
    }
}
