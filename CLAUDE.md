# CLAUDE.md

Guidance for Claude Code (and other AI assistants) working in this repository.

## What this is

EvalHarness evaluates a **separately running RAG question-answering API** over HTTP: it sends dataset questions, scores the
answers and retrieved sources with several evaluators, applies quality gates, and compares against a baseline. It is **not** a
RAG application: it never ingests documents or embeds the system under test. It ships as a CLI (CI, exit codes), an API
service (`POST /runs`, async), and an MCP server (so an agent can drive the API). See `README.md` for the full manual.

## Layout

```
src/EvalHarness.Core        models, options, interfaces, dataset loading, RetryHandler
src/EvalHarness.RagClient   HttpRagClient + RagApiContract (the ONLY code that knows the RAG API's JSON shape)
src/EvalHarness.Evaluators  the six evaluators, OpenAI clients, EvaluatorCatalog (factory), JudgeInput/JudgePrompts
src/EvalHarness.Runner      EvaluationRunner, TestResultMerger (repeats), ResultAggregator, gates, RegressionDetector, ExitCodes
src/EvalHarness.Reporting   ConsoleReportWriter, JsonReportStore
src/EvalHarness.Hosting     Composition (DI) + PreparedEvaluation, shared by CLI and API
src/EvalHarness.Cli         argument parsing and commands
src/EvalHarness.Api         ASP.NET Core service: RunService, RunStore, API-key middleware, ragUrl/requestFields allowlists
src/EvalHarness.Mcp         MCP server (stdio) wrapping the API as tools
tests/EvalHarness.Tests     all tests; fakes only, no network, no real LLM
datasets/  samples/         evaluation datasets, and the documents the sample dataset asks about
.claude/skills/             run-eval, add-evaluator, add-test-case
```

## Commands

Run from the repository root (the solution is `EvalHarness.sln`).

```bash
dotnet build EvalHarness.sln                 # always build from the root; a build from a subfolder leaves the rest stale
dotnet test                                   # whole suite, ~6 s
dotnet test --filter "FullyQualifiedName~ApiTests"
docker build --target test .                  # the suite on Linux

dotnet run --project src/EvalHarness.Cli -- validate --dataset datasets/acme-handbook.json
dotnet run --project src/EvalHarness.Cli -- run --dataset datasets/acme-handbook.json --evaluators exact-match,retrieval-quality
docker compose up -d --build                  # API service on http://localhost:8090/swagger
dotnet build src/EvalHarness.Mcp -c Release   # needed once before the MCP server in .mcp.json can start
```

Secrets live in `.env` (git-ignored; copy `.env.example`). Never commit `.env`, `reports/` or an API key.

## Design rules that must keep holding

- **Evaluators and the runner see only `RagResponse` and interfaces**, never HTTP or the RAG API's field names. API-shape
  assumptions belong in `RagApiContract` and `RagApiOptions` configuration only.
- **Keep the four test outcomes distinct**: `Passed`, `Failed` (a wrong answer), `ApiError` (RAG outage) and `EvaluatorError`
  (judge/embedding failure). An outage must never read as a wrong answer, and exit code `3` ("inconclusive") must stay
  distinct from `1` ("quality failed"). `ExitCodes.For(run)` is the single source of truth.
- **Anything that changes the numbers goes into `RunSettings`** (judge/embedding model, prompt fingerprint, thresholds,
  `RetrievalK`, repeats, request fields) so `RegressionDetector.SettingsWarnings` can flag non-comparable runs.
- **Report changes are additive**: new fields are nullable or defaulted and go at the end of the positional record, so old
  baselines still deserialize. Bump `EvaluationRun.CurrentSchemaVersion` only for a breaking change.
- **Judge inputs are JSON-encoded, never string-pasted** (`JudgeInput.Serialize`). Retrieved chunk text and answers are
  untrusted. Editing a judge prompt changes `JudgePrompts.Fingerprint`, which is intended.
- **API inputs are validated with strict patterns/allowlists**: dataset names, run ids, `ragUrl` (`RagUrlPolicy`) and
  `requestFields` (`Api:AllowedRequestFields`). Request text must never reach a file path or a configuration key unchecked.
- **Never log or echo secrets**: API keys, `Authorization` headers, URLs containing credentials. The MCP tools return
  summaries without retrieved chunk text on purpose (size, and it can contain personal data).

## Testing conventions

- Tests use fakes (`FakeRag`, `FakeLlm`, `FakeEmbedder`, `StubHandler`); the API is tested through `ApiFactory`
  (`WebApplicationFactory`). Nothing may need a network or an API key.
- `ApiFactory` reads `Settings` when the host starts, which is lazy (first request). Set settings before the first request.
- To stub the real HTTP RAG client, register `services.AddHttpClient("IRagClient").ConfigurePrimaryHttpMessageHandler(...)`.
- New behaviour needs a test that **fails without the change**. After writing one, break the code on purpose and confirm it
  goes red, then restore it. Run the full suite repeatedly (30 times) after touching anything concurrent.
- Tests that look at "regression" on tiny datasets must account for the one-test tolerance floor
  (`Regression:AllowSingleTestVariance`), or turn it off in the test.

## Gotchas

- **Git Bash on Windows rewrites arguments starting with `/`** (`/datasets/x.json` becomes a Windows path). Use
  `export MSYS_NO_PATHCONV=1` or PowerShell for Docker commands.
- Raw string literals (judge prompts) take the checkout's line endings; the fingerprint normalises CRLF so Windows and
  Docker agree. Keep it that way.
- `docker-compose.yml` has two services: `evalharness-api` (default) and `evalharness` (profile `batch`, one-shot CLI).
- `.mcp.json` runs the MCP server with `--no-build`; build it first (`dotnet build src/EvalHarness.Mcp -c Release`).

## Known gaps (do not forget, do not silently work around)

- A RAG URL containing credentials (`user:pass@`) can appear in logs and in error messages stored in reports.
- The LLM concurrency limit is per run, not global, so `Api:MaxConcurrentRuns > 1` weakens the rate-limit guarantee.
- No retention or delete for stored runs; reports contain retrieved chunk text.
- The judge is not calibrated against human-labelled answers; there is no "should decline" test type.

## Working principles

1. **Think before coding.** State assumptions, surface tradeoffs, and ask when something is genuinely unclear. Push back when a
   simpler approach exists.
2. **Simplicity first.** The minimum code that solves the problem; nothing speculative.
3. **Surgical changes.** Touch only what the task needs; mention unrelated problems instead of fixing them.
4. **Goal-driven execution.** Turn tasks into verifiable goals (a failing test, then a passing one) and loop until verified.
   Report results faithfully: say what was and was not run.

## AI workflow in this repo

- `.claude/skills/run-eval` - run an evaluation through the MCP tools and triage the result.
- `.claude/skills/add-evaluator` - the checklist for adding an evaluator without breaking comparability.
- `.claude/skills/add-test-case` - add a dataset case with stable expected sources, then validate it.
- `.mcp.json` registers the `evalharness` MCP server (`list_datasets`, `start_run`, `get_run`, `get_summary`,
  `compare_runs`, `cancel_run`). It needs `EVALHARNESS_API_KEY` (and optionally `EVALHARNESS_URL`) in the environment.
