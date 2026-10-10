using System.Net;
using EvalHarness.Core;
using EvalHarness.Core.Http;
using EvalHarness.Evaluators;
using EvalHarness.Evaluators.Providers;

namespace EvalHarness.Tests;

public class AnswerCorrectnessEvaluatorTests
{
    private static Task<EvaluatorResult> Run(string judgeJson, string? answer = "18 days", double threshold = 0.75) =>
        new AnswerCorrectnessEvaluator(new FakeLlm((_, _) => judgeJson), threshold)
            .EvaluateAsync(Build.Context(response: Build.Response(answer)), default);

    [Theory]
    [InlineData(5, 1.0, EvaluatorStatus.Passed)]
    [InlineData(4, 0.75, EvaluatorStatus.Passed)]
    [InlineData(3, 0.5, EvaluatorStatus.Failed)]
    [InlineData(1, 0.0, EvaluatorStatus.Failed)]
    public async Task Maps_judge_score_to_normalised_score_and_verdict(int judgeScore, double normalised, EvaluatorStatus status)
    {
        var result = await Run($$"""{"score": {{judgeScore}}, "explanation": "because"}""");

        Assert.Equal(status, result.Status);
        Assert.Equal(normalised, result.Score!.Value, 6);
        Assert.Contains("because", result.Explanation);
        Assert.Equal(judgeScore, result.Metrics!["judgeScore"]);
    }

    [Fact]
    public async Task Tolerates_markdown_fenced_json()
    {
        var result = await Run("```json\n{\"score\": 5, \"explanation\": \"ok\"}\n```");
        Assert.Equal(EvaluatorStatus.Passed, result.Status);
    }

    [Fact]
    public async Task Threshold_is_configurable()
    {
        var result = await Run("""{"score": 4, "explanation": "ok"}""", threshold: 0.9);
        Assert.Equal(EvaluatorStatus.Failed, result.Status);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"score": 9}""")]
    [InlineData("null")]
    public async Task Unusable_judge_output_throws_instead_of_blaming_the_answer(string judgeJson) =>
        await Assert.ThrowsAsync<JudgeResponseException>(() => Run(judgeJson));

    [Fact]
    public async Task Missing_answer_fails_without_calling_the_judge()
    {
        var llm = new FakeLlm((_, _) => throw new InvalidOperationException("must not be called"));
        var result = await new AnswerCorrectnessEvaluator(llm, 0.75)
            .EvaluateAsync(Build.Context(response: Build.Response(null)), default);

        Assert.Equal(EvaluatorStatus.Failed, result.Status);
        Assert.Equal(0, llm.Calls);
    }

    [Fact]
    public async Task Prompt_contains_question_expected_and_actual_answer()
    {
        string? prompt = null;
        var llm = new FakeLlm((_, user) => { prompt = user; return """{"score": 5}"""; });
        await new AnswerCorrectnessEvaluator(llm, 0.75).EvaluateAsync(
            Build.Context(Build.Case(question: "How many?", expected: "Eighteen"), Build.Response("Nineteen")), default);

        Assert.Contains("How many?", prompt);
        Assert.Contains("Eighteen", prompt);
        Assert.Contains("Nineteen", prompt);
    }
}

public class FaithfulnessEvaluatorTests
{
    private static Task<EvaluatorResult> Run(string judgeJson, string? answer = "Some answer", double threshold = 0.8, params RetrievedChunk[] chunks) =>
        new FaithfulnessEvaluator(new FakeLlm((_, _) => judgeJson), threshold)
            .EvaluateAsync(Build.Context(response: Build.Response(answer, chunks)), default);

    [Fact]
    public async Task All_claims_supported_scores_one()
    {
        var result = await Run("""{"claims":[{"claim":"a","supported":true},{"claim":"b","supported":true}]}""");

        Assert.Equal(EvaluatorStatus.Passed, result.Status);
        Assert.Equal(1.0, result.Score);
    }

    [Fact]
    public async Task Unsupported_claims_lower_the_score_and_are_named()
    {
        var result = await Run(
            """{"claims":[{"claim":"c1","supported":true},{"claim":"The CEO is Bob","supported":false,"evidence":"not in context"}]}""");

        Assert.Equal(EvaluatorStatus.Failed, result.Status);
        Assert.Equal(0.5, result.Score);
        Assert.Contains("The CEO is Bob", result.Explanation);
        Assert.Equal(1, result.Metrics!["unsupportedClaims"]);
        Assert.Equal(2, result.Metrics["claims"]);
    }

    [Fact]
    public async Task No_claims_counts_as_faithful() =>
        Assert.Equal(1.0, (await Run("""{"claims":[]}""")).Score);

    [Fact]
    public async Task No_answer_text_is_skipped_not_counted_as_perfectly_faithful_and_skips_the_judge()
    {
        var llm = new FakeLlm((_, _) => throw new InvalidOperationException("must not be called"));
        var result = await new FaithfulnessEvaluator(llm, 0.8).EvaluateAsync(Build.Context(response: Build.Response(null)), default);

        Assert.Equal(EvaluatorStatus.Skipped, result.Status);
        Assert.Null(result.Score);
        Assert.Equal(0, llm.Calls);
    }

    [Fact]
    public async Task Answer_without_retrieved_context_fails()
    {
        var response = Build.Response("Something") with { RetrievedChunks = [] };
        var result = await new FaithfulnessEvaluator(new FakeLlm((_, _) => "{}"), 0.8).EvaluateAsync(Build.Context(response: response), default);

        Assert.Equal(EvaluatorStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Judge_sees_the_retrieved_chunks()
    {
        string? prompt = null;
        var llm = new FakeLlm((_, user) => { prompt = user; return """{"claims":[]}"""; });
        await new FaithfulnessEvaluator(llm, 0.8).EvaluateAsync(
            Build.Context(response: Build.Response("An answer", Build.Chunk(1, "Receipts over $25."), Build.Chunk(2, "Economy class."))), default);

        Assert.Contains("Receipts over $25.", prompt);
        Assert.Contains("Economy class.", prompt);
    }

    [Theory]
    [InlineData("""{"claims": null}""")]
    [InlineData("""{"claims":[{"claim":"x"}]}""")]
    [InlineData("garbage")]
    public async Task Malformed_judge_output_throws(string judgeJson) =>
        await Assert.ThrowsAsync<JudgeResponseException>(() => Run(judgeJson));
}

public class RetrievalRelevanceEvaluatorTests
{
    private static Task<EvaluatorResult> Run(string judgeJson, int k = 5, double threshold = 0.4) =>
        new RetrievalRelevanceEvaluator(new FakeLlm((_, _) => judgeJson), k, threshold).EvaluateAsync(
            Build.Context(response: Build.Response("A", Build.Chunk(1), Build.Chunk(2), Build.Chunk(3))), default);

    [Fact]
    public async Task Score_is_the_fraction_of_relevant_chunks()
    {
        var result = await Run("""{"chunks":[{"index":1,"relevant":true},{"index":2,"relevant":false},{"index":3,"relevant":false}]}""");

        Assert.Equal(1 / 3.0, result.Score!.Value, 6);
        Assert.Equal(EvaluatorStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Only_the_top_k_chunks_are_judged()
    {
        var result = await Run("""{"chunks":[{"index":1,"relevant":true}]}""", k: 1);

        Assert.Equal(1.0, result.Score);
        Assert.Equal(EvaluatorStatus.Passed, result.Status);
    }

    [Fact]
    public async Task Missing_chunk_verdict_throws() =>
        await Assert.ThrowsAsync<JudgeResponseException>(() => Run("""{"chunks":[{"index":1,"relevant":true}]}"""));
}

public class ProviderTests
{
    [Fact]
    public async Task Concurrency_limit_caps_simultaneous_llm_calls()
    {
        int active = 0, max = 0;
        var inner = new SlowLlm(async () =>
        {
            var now = Interlocked.Increment(ref active);
            lock (typeof(ProviderTests)) max = Math.Max(max, now);
            await Task.Delay(30);
            Interlocked.Decrement(ref active);
        });
        var limited = new ConcurrencyLimitedLlmClient(inner, 2);

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => limited.CompleteJsonAsync("s", "u", default)));

        Assert.Equal(2, max);
    }

    private sealed class SlowLlm(Func<Task> work) : ILlmClient
    {
        public async Task<string> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
        {
            await work();
            return "{}";
        }
    }

    [Fact]
    public async Task Chat_client_sends_auth_and_parses_content()
    {
        string? auth = null, body = null;
        var handler = new StubHandler((req, _) =>
        {
            auth = req.Headers.Authorization?.ToString();
            body = req.Content!.ReadAsStringAsync().Result;
            return Task.FromResult(StubHandler.JsonResponse(HttpStatusCode.OK, """{"choices":[{"message":{"content":"{\"score\":5}"}}]}"""));
        });
        var client = new OpenAiChatClient(new HttpClient(handler), new LlmOptions { ApiKey = "sk-test", BaseUrl = "https://example.test/v1/" });

        var content = await client.CompleteJsonAsync("system", "user", default);

        Assert.Equal("""{"score":5}""", content);
        Assert.Equal("Bearer sk-test", auth);
        Assert.Contains("\"model\":\"gpt-4o-mini\"", body);
        Assert.Contains("json_object", body);
    }

    [Fact]
    public async Task Chat_client_http_error_becomes_provider_exception_without_leaking_auth_details()
    {
        var handler = StubHandler.Json(HttpStatusCode.Unauthorized, """{"error":"Incorrect API key provided: sk-secret"}""");
        var client = new OpenAiChatClient(new HttpClient(handler), new LlmOptions { ApiKey = "sk-test" });

        var ex = await Assert.ThrowsAsync<ProviderException>(() => client.CompleteJsonAsync("s", "u", default));

        Assert.Contains("401", ex.Message);
        Assert.DoesNotContain("sk-secret", ex.Message);
    }

    [Fact]
    public async Task Embedding_client_parses_vector()
    {
        var handler = StubHandler.Json(HttpStatusCode.OK, """{"data":[{"embedding":[0.5,0.25,1]}]}""");
        var client = new OpenAiEmbeddingClient(new HttpClient(handler), new EmbeddingOptions { ApiKey = "k" });

        var vector = await client.EmbedAsync("text", default);
        Assert.Equal(new[] { 0.5f, 0.25f, 1f }, vector);
    }

    [Fact]
    public void Missing_api_key_is_a_configuration_error()
    {
        const string variable = "EVALHARNESS_TEST_KEY_THAT_IS_NOT_SET";
        var options = new LlmOptions { ApiKeyEnvVar = variable };

        var ex = Assert.Throws<ConfigurationException>(() => new OpenAiChatClient(new HttpClient(), options));
        Assert.Contains(variable, ex.Message);
    }

    [Fact]
    public async Task Provider_calls_are_retried_through_the_retry_handler()
    {
        var handler = new StubHandler((_, call) => Task.FromResult(call < 3
            ? StubHandler.JsonResponse(HttpStatusCode.TooManyRequests, "{}")
            : StubHandler.JsonResponse(HttpStatusCode.OK, """{"choices":[{"message":{"content":"{}"}}]}""")));
        var retry = new RetryHandler(new RetryPolicy { MaxRetries = 3, BaseDelay = TimeSpan.Zero }, delay: (_, _) => Task.CompletedTask)
        {
            InnerHandler = handler,
        };
        var client = new OpenAiChatClient(new HttpClient(retry), new LlmOptions { ApiKey = "k" });

        Assert.Equal("{}", await client.CompleteJsonAsync("s", "u", default));
        Assert.Equal(3, handler.Calls);
    }
}
