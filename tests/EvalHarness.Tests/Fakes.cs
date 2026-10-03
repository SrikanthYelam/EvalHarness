using System.Net;
using System.Text;
using EvalHarness.Core;

namespace EvalHarness.Tests;

/// <summary>Scripted HTTP handler: the function receives the request and the 1-based call number.</summary>
public sealed class StubHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    private int _calls;
    public int Calls => _calls;
    public List<string> Bodies { get; } = [];

    public static StubHandler Json(HttpStatusCode status, string body) =>
        new((_, _) => Task.FromResult(JsonResponse(status, body)));

    public static HttpResponseMessage JsonResponse(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _calls);
        string? requestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        if (requestBody is not null)
            lock (Bodies) Bodies.Add(requestBody);
        return await respond(request, call);
    }
}

public sealed class FakeLlm(Func<string, string, string> respond) : ILlmClient
{
    public int Calls;
    public Task<string> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(respond(systemPrompt, userPrompt));
    }
}

public sealed class FakeEmbedder(Func<string, float[]> embed) : IEmbeddingClient
{
    public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken) => Task.FromResult(embed(text));
}

public sealed class FakeRag(Func<string, CancellationToken, Task<RagResponse>> ask) : IRagClient
{
    private int _active;
    private int _maxActive;
    public int MaxConcurrency => _maxActive;
    public int Calls;

    public async Task<RagResponse> AskAsync(string question, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        var now = Interlocked.Increment(ref _active);
        int seen;
        while ((seen = _maxActive) < now && Interlocked.CompareExchange(ref _maxActive, now, seen) != seen) { }
        try { return await ask(question, cancellationToken); }
        finally { Interlocked.Decrement(ref _active); }
    }

    public Task WaitUntilReadyAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class DelegateEvaluator(string name, Func<EvaluationContext, EvaluatorResult> evaluate) : IEvaluator
{
    public string Name => name;
    public Task<EvaluatorResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken) =>
        Task.FromResult(evaluate(context));
}

public static class Build
{
    public static TestCase Case(string id = "t1", string question = "Q?", string expected = "A", params ExpectedSource[] sources) =>
        new()
        {
            Id = id, Category = "cat", Difficulty = "easy", Question = question, ExpectedAnswer = expected,
            ExpectedSources = sources.Length > 0 ? sources : [new ExpectedSource { DocumentName = "doc.md" }],
        };

    public static RetrievedChunk Chunk(int rank, string text = "text", string? doc = "doc.md", string? id = null) =>
        new(rank, null, doc, id ?? $"c{rank}", text, 1.0 / rank);

    public static RagResponse Response(string? answer = "A", params RetrievedChunk[] chunks) =>
        new("Q?", answer, "Answered", chunks.Length > 0 ? chunks : [Chunk(1)], 10, 1);

    public static Dataset Dataset(params TestCase[] cases) => new("ds", null, "ds.json", "sha-1", cases);

    public static EvaluationContext Context(TestCase? test = null, RagResponse? response = null) =>
        new(test ?? Case(), response ?? Response());

    public static ConfiguredEvaluator Passing(string name = "ok", bool gating = true) =>
        new(new DelegateEvaluator(name, _ => EvaluatorResult.Of(name, true, 1, "fine")), gating);

    public static ConfiguredEvaluator Failing(string name = "bad", bool gating = true) =>
        new(new DelegateEvaluator(name, _ => EvaluatorResult.Of(name, false, 0, "wrong")), gating);
}
