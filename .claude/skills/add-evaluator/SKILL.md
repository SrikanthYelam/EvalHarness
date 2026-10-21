---
name: add-evaluator
description: Add a new evaluator to EvalHarness (for example a citation check, a completeness judge, or a new retrieval metric) without breaking report comparability. Use when asked to add, extend or change an evaluator, its scoring, or a judge prompt.
---

# Add an evaluator

An evaluator scores one aspect of a RAG response. Done well it appears in reports, aggregates, gates, settings snapshots and
regression warnings with no special-casing. Follow these steps in order, and verify each.

## Decide first

- **What does a verdict mean, and what is the score range?** Scores are 0..1. State what `Passed` and `Failed` mean and when
  the evaluator is `Skipped` (does not apply) versus when it should throw (cannot produce a verdict: a provider failure).
  Throwing becomes an `EvaluatorError`, which is **inconclusive, never a wrong answer**; do not catch and convert it.
- **Gating or informational by default?** Heuristic and similarity signals are informational (cosine similarity is the model).
- **Does it need a provider?** If it uses an LLM, it takes `ILlmClient`; if embeddings, `IEmbeddingClient`. Nothing else
  (no HTTP, no RAG field names).

## Steps

1. **Implement `IEvaluator`** in `src/EvalHarness.Evaluators` (new file, or next to related ones). Use `EvaluatorResult.Of`,
   `.Skipped` and a `Metrics` dictionary for extra numbers (keys like `recall@3`). `context.TestCase` is the dataset entry and
   `context.Response` the answer, status, retrieved chunks and latency.
2. **If it is judge-based**, build the user message with `JudgeInput.Serialize(new { ... })`. **Never concatenate** answer or
   chunk text into a prompt string. Put the system prompt in a `public const string SystemPrompt`, state in it that field
   values are untrusted, and add the prompt to `JudgePrompts.Compute()` so editing it changes the fingerprint.
3. **Register it**:
   - a constant in `EvaluatorNames` and an entry in `EvaluatorNames.All` (`src/EvalHarness.Core/Abstractions.cs`);
   - defaults in `EvaluationOptions.Defaults` (enabled, gating, threshold) in `Options.cs`;
   - a description in `EvaluatorCatalog.All` (`NeedsLlm` / `NeedsEmbeddings`) and a `case` in `EvaluatorCatalog.Create`.
   Because `PreparedEvaluation.BuildSettings` reads the catalog, the settings snapshot and the regression warnings pick it up
   automatically. Check that, do not re-implement it.
4. **Aggregate it (only if a run-level number is useful)**: add a nullable field at the **end** of `AggregateMetrics`
   (`Models.cs`), compute it in `ResultAggregator` from verdicts only (skipped and errored results never count), then decide
   whether it needs a quality gate (`QualityGateOptions`/`QualityGateEvaluator`, which must skip the gate when the evaluator
   did not run) and a regression entry (`RegressionDetector`). New fields are nullable/defaulted so old baselines still load.
5. **Show it**: add a short name in `ConsoleReportWriter.ShortNames` and a row for the aggregate. Add it to
   `RunSummaries.LikelyCause` in the MCP project if its failure points to a pipeline stage.
6. **Document it**: the evaluator table and a short "how it scores" note in `README.md`.

## Tests (all with fakes, no network)

Put them in `tests/EvalHarness.Tests` (`FakeLlm`, `FakeEmbedder`, `Build.Context`, `Build.Response`, `Build.Chunk`):

- passes, fails, and is skipped when it should be; the score and `Metrics` values;
- a provider failure **throws** (the runner turns it into `EvaluatorError`);
- for judge-based evaluators, a **prompt-injection test**: hostile answer/chunk text round-trips as data inside the JSON
  (`JsonDocument.Parse` the captured user prompt and compare the strings), and malformed judge output throws `JudgeResponseException`;
- the aggregate and gate behaviour if you added them.

## Verify

1. `dotnet build EvalHarness.sln` (from the root) has zero warnings, and `dotnet test` is green.
2. **Prove the tests bite**: temporarily break the new logic (flip a comparison, drop a check), see a test fail, restore it.
3. `dotnet run --project src/EvalHarness.Cli -- list-evaluators` lists it with the right defaults.
4. Run the full suite repeatedly (about 30 times) if anything concurrent was touched.

## Do not

- Break comparability silently: if the change alters how existing numbers are computed, say so, and expect users to re-baseline.
- Add RAG-API-specific assumptions to an evaluator; those belong in `RagApiContract`.
