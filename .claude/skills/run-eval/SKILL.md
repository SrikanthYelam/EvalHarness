---
name: run-eval
description: Run an EvalHarness evaluation of a RAG API and interpret the result. Use when asked to evaluate a RAG system, check whether a RAG change improved or regressed quality, compare retrieval settings (mode, topK), or triage failing evaluation tests. Drives the evalharness MCP tools (list_datasets, start_run, get_run, get_summary, compare_runs, cancel_run).
---

# Run an evaluation and triage the result

EvalHarness evaluates a RAG API that runs **separately**; it never ingests documents. A run spends LLM budget (judge and
embedding calls), so run what you intend to read, once.

## 0. Preconditions

- The `evalharness` MCP tools must be available. If they are not, the server needs `dotnet build src/EvalHarness.Mcp -c Release`
  and `EVALHARNESS_API_KEY` in the environment (see `.mcp.json`). Without MCP, fall back to the REST API in `README.md`
  (`POST /runs`, `GET /runs/{id}`, `GET /runs/{id}/report`) with the `X-Api-Key` header. Never print the API key.
- The documents a dataset asks about must already be ingested in the RAG system under test. If unsure, ask the user; an empty
  index shows up as every test failing with `retrieval-miss`, which is a setup problem, not a quality result.

## 1. Choose the run

1. `list_datasets`, and pick the dataset that matches the question being asked.
2. Decide the settings, and say them to the user before starting:
   - **Quick look / no judge budget**: `evaluators: ["exact-match", "retrieval-quality"]` (retrieval only, no LLM).
   - **Full quality check**: leave `evaluators` unset.
   - **A decision you will act on** (shipping, tuning, comparing two settings): `repeats: 3`. One run of a small dataset is
     noisy, and a single test flipping is within the harness's tolerance anyway.
   - **Comparing retrieval settings**: set `requestFields` (for example `{"mode": "Vector", "topK": "10"}`); only names the
     server allows are accepted, and the error lists them. Run the variants with identical other settings.
3. To check for regression, pass `baselineRunId` of an earlier **completed** run of the same dataset.

## 2. Run and wait

1. `start_run` returns immediately with a run id and status `Queued`.
2. `get_run` with `waitSeconds: 60`; repeat until `status` is `Completed`, `Failed` or `Cancelled`. Only one run executes at a
   time, so do not start several in parallel; the rest queue.
3. If you started a run by mistake, or it is clearly stuck, `cancel_run`.

## 3. Read the result

`get_summary` gives the verdict, metrics, gates, regression, settings and up to 20 problem tests. Read in this order:

1. **`verdict.outcome`**
   - `Passed`: every gate passed and nothing regressed.
   - `QualityFailure`: a gate failed or a regression was found. Continue to step 2.
   - `Inconclusive`: RAG API or judge errors. This says **nothing about quality**. Report it as "could not measure" and check
     `tests.apiErrors` / `evaluatorErrors`; do not describe it as a failure of the RAG system.
2. **`problems[].likelyCause`** (group the problems by it; the fix differs):
   - `retrieval-miss`: the expected source was not retrieved. Look at `topSources` (document names and ranks). Candidates:
     chunking, embedding/keyword weighting, `topK`, missing or garbled source text, or the document not being ingested.
   - `refused-despite-retrieval`: the right chunk was retrieved but the system returned no answer. Candidates: the
     similarity/confidence threshold that triggers "insufficient context", or the answer prompt being too cautious.
   - `unsupported-claims`: the answer says things the retrieved context does not support (hallucination). Candidates: the
     generation prompt, or too little context.
   - `wrong-answer-despite-retrieval`: right chunk, wrong or incomplete answer. Candidates: the generation prompt, or an
     answer that needs reasoning across facts.
   - `rag-api-error` / `evaluator-error`: infrastructure, not quality.
3. **Metrics in context**: `refusalRate` (how often there was no answer text), `recallAt1` vs `recallAt5` (right chunk found but
   buried, versus missed), `faithfulness` (grounding) versus `answerCorrectness` (matches the expected answer). Cosine similarity
   is a weak signal and is not a correctness measure; never conclude "correct" from it.
4. **Flaky tests** (`tests.flaky`, `problems[].flaky`): their attempts disagreed. Treat their verdicts as uncertain.
5. **`settings`**: record the judge model, prompt fingerprint, repeats and request fields next to any number you report.

## 4. Comparing two runs

Use `compare_runs(baselineRunId, currentRunId)` (or `baselineRunId` at start). Read `warnings` **first**: a different judge model,
prompt fingerprint, thresholds, request fields, repeats or dataset means the runs are not like-for-like, and any regression or
improvement is suspect. A drop that equals one test's worth is inside the tolerance on purpose. Say which tests flipped
(`regressedTests`, `improvedTests`), not just the metric deltas.

## 5. Report to the user

Keep it short: what was run (dataset, settings), the verdict, the two or three things that matter most (grouped by
`likelyCause`, with test ids), the evidence (metrics, sources), and the caveats (noise, non-comparable settings, tests that
could not be evaluated). Say what you did **not** verify. Do not claim an improvement from a single noisy run.

## Rules

- Summaries deliberately exclude retrieved chunk text. If you need it, fetch `GET /runs/{id}/report` yourself, and treat it as
  possibly containing personal data: do not paste it into issues, commits or chat beyond what is needed.
- Do not change the RAG system, the dataset or the gates to make a run pass. Report the result as it is.
