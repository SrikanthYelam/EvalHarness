# EvalHarness

EvalHarness is an **independent evaluation harness for RAG question-answering APIs**. It treats your RAG system as a black box behind HTTP, sends it a dataset of questions, scores what comes back from several angles, and compares the result with a previous run. Use it as a **CLI** (CI-friendly exit codes) or run it as an **API service** (start a run with `POST /runs`, poll it, fetch the report).

It is **not** a RAG application. It does not ingest documents, host a vector store, or embed your RAG code. The RAG API stays a separate, replaceable dependency, so the same harness can test a local build, a Docker container, or a hosted service.

```
Evaluation dataset ──► Eval runner ──► RAG API (system under test) ──► Evaluators ──► Metrics ──► Reports
   (JSON)                (parallel,        answer + retrieved          exact match        │         console
                          retries)         chunks over HTTP            cosine             ▼         JSON
                                                                       correctness   Quality gates + baseline
                                                                       faithfulness  comparison ──► exit code
                                                                       retrieval
```

EvalHarness works with any API that takes a question and returns an answer plus retrieved chunks. The default settings expect the response shape shown under [Pointing it at a different API](#pointing-it-at-a-different-api); other shapes are a configuration change.

## Contents

- [Why one metric is not enough](#why-one-metric-is-not-enough)
- [Quick start](#quick-start)
- [Dataset format](#dataset-format)
- [Evaluators](#evaluators)
- [Cosine similarity is not correctness](#cosine-similarity-is-not-correctness)
- [Retrieval evaluation](#retrieval-evaluation)
- [Faithfulness](#faithfulness)
- [Test outcomes: wrong answer vs outage vs evaluator failure](#test-outcomes-wrong-answer-vs-outage-vs-evaluator-failure)
- [Quality gates and regression testing](#quality-gates-and-regression-testing)
- [CLI reference and exit codes](#cli-reference-and-exit-codes)
- [Configuration](#configuration)
- [API service](#api-service)
- [Docker](#docker)
- [CI/CD](#cicd)
- [Adding a new evaluator](#adding-a-new-evaluator)
- [Architecture](#architecture)
- [Limitations](#limitations)

## Why one metric is not enough

A RAG system is a pipeline (retrieve, then generate), and each stage fails differently:

| Failure | What you see | Which signal catches it |
| --- | --- | --- |
| Retriever misses the right chunk | Answer is vague, wrong, or invented | **Retrieval quality** (Recall@K, rank) |
| Retriever finds it, model ignores it | Right context, wrong answer | **Answer correctness** |
| Model adds facts that are not in the context | Fluent, confident, unsupported claims | **Faithfulness** |
| Model says the right thing in different words | Looks "wrong" to string comparison | Exact match fails, **correctness** and **cosine** pass |
| Model says something *similar but false* | Reads like the expected answer, one fact is wrong | Cosine passes, **correctness** fails |
| API is down or slow | No answer at all | Reported as an **error**, not as a wrong answer |

Any single number hides at least one of these. EvalHarness reports them side by side and gates on the ones you choose.

## Quick start

Prerequisites: .NET 8 SDK for local runs, and/or Docker. The judge and embedding evaluators need an OpenAI-compatible API key in `OPENAI_API_KEY`.

```bash
# 1. Check the dataset (no API calls)
dotnet run --project src/EvalHarness.Cli -- validate --dataset datasets/acme-handbook.json

# 2. Start your RAG API and ingest the documents the dataset asks about
#    (samples/acme-handbook.md for the sample dataset).

# 3. Run the evaluation
export OPENAI_API_KEY=sk-...
dotnet run --project src/EvalHarness.Cli -- run \
  --dataset datasets/acme-handbook.json \
  --rag-url http://localhost:8080 \
  --output reports
echo "exit code: $?"
```

No key yet? Run only the evaluators that need no provider:

```bash
dotnet run --project src/EvalHarness.Cli -- run --dataset datasets/acme-handbook.json \
  --evaluators exact-match,retrieval-quality
```

The run prints a console report and writes `reports/eval-<timestamp>-<runid>.json` plus `reports/latest.json`.

To use a report as the baseline for later runs, copy it somewhere stable (for example `baselines/acme-handbook.json`) and pass `--baseline`.

## Dataset format

A dataset is a JSON file. Nothing is hard-coded in C#.

```json
{
  "name": "acme-handbook",
  "description": "Optional free text.",
  "testCases": [
    {
      "id": "vacation-carryover",
      "category": "time-off",
      "difficulty": "easy",
      "question": "How many unused vacation days can I carry over into the next year?",
      "expectedAnswer": "Up to 5 unused vacation days carry over; anything above that expires on January 31.",
      "expectedSources": [
        { "documentName": "acme-handbook.md", "chunkContains": "carry over into the next calendar year" }
      ],
      "evaluators": ["answer-correctness", "retrieval-quality"]
    }
  ]
}
```

| Field | Required | Notes |
| --- | --- | --- |
| `id` | yes | Unique (case-insensitive). Used to match tests across runs for regression detection. |
| `category` | yes | Free text, shown in reports. |
| `difficulty` | yes | `easy`, `medium` or `hard`. |
| `question` | yes | Sent to the RAG API. |
| `expectedAnswer` | yes | Ground truth for exact match, cosine and the correctness judge. |
| `expectedSources` | yes, at least one | What retrieval should surface (below). |
| `evaluators` | no | Restrict this test to the named evaluators. Omitted means all enabled ones. |

Each `expectedSources` entry needs at least one of `documentId`, `documentName`, `chunkId`, `chunkContains`. **Every field you set must match** the same retrieved chunk. `documentName` ignores directories and case (`docs/Handbook.md` matches `handbook.md`). Prefer `documentName` + `chunkContains` when chunk ids change between ingestions.

`validate` (and `run`, before it calls anything) rejects: invalid JSON, misspelled or unknown fields, missing required fields, duplicate ids, unknown difficulty, expected sources with no identifier, and unknown evaluator names. All problems are listed at once.

## Evaluators

`list-evaluators` prints this table from the code.

| Name | Needs | Default | What it measures |
| --- | --- | --- | --- |
| `exact-match` | nothing | on, informational | Equal after normalising case, whitespace and trailing punctuation. For deterministic answers. |
| `cosine-similarity` | embeddings | on, informational | Cosine similarity of the expected and actual answer embeddings. A semantic signal only. |
| `answer-correctness` | LLM judge | on, **gating** | Judge compares the answer with the expected answer on a 1-5 rubric; returns score, pass/fail, explanation. |
| `faithfulness` | LLM judge | on, **gating** | Every claim in the answer is checked against the retrieved context. Finds hallucination. |
| `retrieval-quality` | nothing | on, **gating** | Was the expected source retrieved? Recall@1/3/5 and expected-source rank. |
| `retrieval-relevance` | LLM judge | **off**, informational | Are the retrieved chunks relevant to the question? Kept separate from source recall. |

*Gating* means a failure fails the test case. *Informational* evaluators are still scored, reported and aggregated, they just do not decide pass/fail. Change either with `Evaluation:Evaluators:<name>:Gating` / `Enabled` / `Threshold`.

How scores become verdicts:

- **Answer correctness**: judge score 1-5 is normalised to 0..1; passes at the threshold (default 0.75, so a judge score of 4 or 5). The judge returns JSON; the harness computes pass/fail from the threshold so the verdict is always consistent with the score. Malformed judge output is an *evaluator error*.
- **Faithfulness**: `supported claims / total claims`. An answer with no factual claims, such as a refusal, is vacuously faithful. An answer given with no retrieved context fails.
- **Retrieval quality**: Recall@K is the fraction of the test's expected sources found in the top K chunks. The test passes when Recall@`RetrievalK` (default 5) reaches the threshold (default 1.0).
- **Retrieval relevance**: precision over the top K chunks, as judged by the LLM.

Judge calls use temperature 0 and treat answer/context text as data, but LLM judges are still not perfectly deterministic. That is one reason regression thresholds exist.

## Cosine similarity is not correctness

> **A high cosine similarity does not mean the answer is correct.**

Embedding models place texts that are *about the same thing* close together. They are not fact checkers. Consider:

| | |
| --- | --- |
| Expected | "Full-time employees accrue 1.5 vacation days per month, 18 days per year." |
| Actual | "Full-time employees accrue 1.5 vacation days per month, 8 days per year." |

The two share almost every word, topic and structure, so their embeddings are nearly identical and the cosine score is high, yet the actual answer states a wrong number. The same happens when an answer negates or reverses a policy, swaps a name, or restates the question with no answer. The reverse also occurs: a correct answer phrased very differently, or much more verbosely, can score lower than a wrong one.

So in EvalHarness cosine similarity is:

- a **cheap semantic signal** for spotting answers that are off-topic or empty,
- **informational by default**, not gating,
- always read **alongside** answer correctness (the judge compares facts) and faithfulness (are the claims grounded).

If you do gate on it, treat the threshold as a tripwire for "answers got generally less similar", not as a correctness bar.

## Retrieval evaluation

Retrieval is judged independently of generation, because a good answer can come from luck or world knowledge while a bad answer often traces back to a missed chunk.

For each test, every expected source is located in the ranked list of chunks the API returned (1-based rank, first match wins):

- **Recall@K** = expected sources found in the top K ÷ expected sources. Reported for K = 1, 3, 5.
- **Expected source rank** = rank of the best-placed expected source (the aggregate is the mean over tests where it was found).

Aggregates average per-test recall. Recall@1 tells you if the right chunk is on top; Recall@5 tells you if the generator even had a chance. A dataset with one expected source per question makes these exactly "hit rates".

`retrieval-relevance` (optional) asks a different question: of the chunks returned, how many actually help answer the question? Low relevance with high recall means the right chunk is buried in noise.

## Faithfulness

Faithfulness asks: *is everything the answer says supported by the retrieved context?* The judge breaks the answer into claims and, for each, decides whether the **context alone** supports it. Outside knowledge does not count, so a claim that is true in the real world but absent from the retrieved chunks is flagged as unsupported. That is what makes it a hallucination detector rather than a correctness check.

An answer can be faithful and wrong (it accurately repeats an irrelevant chunk) or correct and unfaithful (right from the model's memory, not from the documents). Hence both evaluators.

## Test outcomes: wrong answer vs outage vs evaluator failure

Every test ends in exactly one outcome, and they are never conflated:

| Outcome | Meaning | Counts toward pass rate? |
| --- | --- | --- |
| `Passed` | RAG answered; no gating evaluator failed | yes |
| `Failed` | RAG answered; a gating evaluator judged it wrong (**an incorrect RAG answer**) | yes |
| `ApiError` | The RAG API failed after retries: connection, timeout, HTTP error, unusable payload (**infrastructure**) | no |
| `EvaluatorError` | A judge/embedding call failed or returned garbage (**the harness's problem, not the RAG's**) | no |

One failing test never stops the run. If a test has both a genuine failure and an evaluator error, it is reported as `Failed`. Per-evaluator results (including `Skipped` and `Error`) are always in the JSON report.

## Quality gates and regression testing

### Absolute quality gates

Configured under `QualityGates`; a `null` value disables a gate. Defaults:

| Gate | Default |
| --- | --- |
| `MinPassRate` | 0.80 (over *evaluated* tests, so outages do not count against it) |
| `MinAnswerCorrectness` | 0.70 |
| `MinFaithfulness` | 0.80 |
| `MinRecallAt5` | 0.80 |
| `MinCosineSimilarity`, `MinRecallAt1`, `MinRecallAt3`, `MaxAverageLatencyMs` | off |
| `MaxApiErrorRate`, `MaxEvaluatorErrorRate` | 0.0 (any error makes the run inconclusive) |

A gate on a metric whose evaluator was not enabled for the run (for example with `--evaluators`) is skipped. A gate whose evaluator *did* run but produced no values is reported as inconclusive, never as a quality failure.

### Baseline vs current

```bash
# Save a known-good run as the baseline
mkdir -p baselines && cp reports/latest.json baselines/acme-handbook.json

# Later: run again and compare in one step...
dotnet run --project src/EvalHarness.Cli -- run --dataset datasets/acme-handbook.json --baseline baselines/acme-handbook.json

# ...or compare two saved reports without calling anything
dotnet run --project src/EvalHarness.Cli -- compare --baseline baselines/acme-handbook.json --current reports/latest.json
```

Tracked: pass rate, answer correctness, faithfulness, cosine similarity, Recall@1/3/5 and average latency. Each quality metric may drop by at most its allowance (absolute, default 0.05); latency may rise by at most `MaxLatencyIncreasePercent` (default 25%). A drop beyond the allowance is a **regression** and fails the run **even if every absolute gate passes**, which is how a slow slide gets caught.

```json
"Regression": {
  "MaxAnswerCorrectnessDrop": 0.03,
  "MaxRecallAt5Drop": 0.0,
  "MaxLatencyIncreasePercent": 50,
  "MaxNewFailingTests": 0
}
```

The report also lists test-level changes: newly failing, newly passing, passed-before-but-errored-now (inconclusive), new and removed tests. `MaxNewFailingTests` (default off) can gate on the number of newly failing tests. Comparing runs of *different* datasets works but produces a warning, since the aggregates are not like-for-like. Reports record the dataset's SHA-256.

## CLI reference and exit codes

The CLI never prompts, so it is safe in CI.

```
EvalHarness run --dataset <file> [--output <dir>] [--baseline <report.json>] [--rag-url <url>]
                [--parallelism <n>] [--evaluators <a,b,...>] [--wait-for-ready <seconds>] [--config <file>]
EvalHarness compare --baseline <report.json> --current <report.json> [--output <file>] [--config <file>]
EvalHarness validate --dataset <file>
EvalHarness list-evaluators
```

| Exit code | Meaning |
| --- | --- |
| `0` | All quality gates passed and no regression |
| `1` | A quality gate failed, or a regression against the baseline was detected |
| `2` | Usage, configuration or dataset/baseline error (nothing was evaluated) |
| `3` | **Inconclusive**: RAG API or evaluator errors, or the API never became ready, with no quality failure |
| `130` | Cancelled (Ctrl+C / `docker stop`); a partial report is still written |

Exit `3` exists so CI can tell "the RAG got worse" (1) from "we could not measure it" (3), and so an outage is never reported as bad answers. If quality failed *and* errors occurred, the code is `1`.

Logs go to **stderr** (set `Logging:Format` to `json` for structured JSON lines); the report goes to **stdout**.

## Configuration

Layered, later wins: `appsettings.json` next to the executable, then `--config <file>`, then `EVALHARNESS_*` environment variables (`__` separates nesting), then command-line options. Key sections:

| Section | Purpose |
| --- | --- |
| `RagApi` | Base URL, paths, timeout (per attempt), retries, headers, request fields, response mapping |
| `Evaluation` | `MaxParallelism`, `RetrievalK`, per-evaluator `Enabled` / `Gating` / `Threshold` |
| `Llm`, `Embedding` | Judge and embedding provider: `BaseUrl`, `Model`, `ApiKeyEnvVar`, `MaxConcurrency`, retries |
| `QualityGates`, `Regression` | See above |
| `Baseline` | Path of a baseline report (same as `--baseline`; handy for Docker) |

Examples:

```bash
EVALHARNESS_RagApi__BaseUrl=https://rag.staging.example.com
EVALHARNESS_RagApi__Headers__Authorization="Bearer $TOKEN"
EVALHARNESS_Evaluation__MaxParallelism=2
EVALHARNESS_Llm__Model=gpt-4o
```

**Secrets**: API keys are read from environment variables (`OPENAI_API_KEY` by default; change the name with `ApiKeyEnvVar`). They are never written to reports, and the report records only the RAG API's scheme, host and path (credentials and query strings are stripped).

### Reliability and rate limits

- RAG API and provider calls share one retry policy: transient failures (HTTP 408, 429, 500, 502, 503, 504, connection errors, per-attempt timeouts) are retried with exponential backoff and jitter, honouring `Retry-After`. Client errors (4xx) are not retried.
- `Evaluation:MaxParallelism` bounds concurrent test cases against the RAG API. `Llm:MaxConcurrency` and `Embedding:MaxConcurrency` bound concurrent provider calls across all tests and evaluators, so you can fan out against your own API without hammering the judge's rate limit.
- Recorded latency is the duration of the final attempt, so retries and backoff do not inflate it. `attempts` is recorded per test.

### Pointing it at a different API

The API's shape is described entirely in `RagApi` configuration; no evaluator knows about it (`RagApiContract` is the single place that does). For an API that replies `{"result":{"text":...},"contexts":[{"doc":{"id","name"},"content","similarity"}]}`:

```json
{
  "RagApi": {
    "BaseUrl": "https://rag.example.com",
    "AskPath": "/v2/query",
    "QuestionField": "query",
    "RequestFields": { "topK": "5", "mode": "Hybrid" },
    "AnswerPath": "result.text",
    "StatusPath": null,
    "ChunksPath": "contexts",
    "ChunkIdField": null,
    "DocumentIdField": "doc.id",
    "DocumentNameField": "doc.name",
    "ChunkTextField": "content",
    "ChunkScoreField": "similarity"
  }
}
```

Paths are dot-separated property names (case-insensitive). `RequestFields` values that look like numbers or booleans are sent as such. If the response shape needs more than field mapping (for example a streaming API), implement `IRagClient` and register it.

Default mapping: the request is `{"question": ...}`; the response is expected to contain `answer`, optional `status`, and a ranked `retrievedChunks` array whose items have `id`, `sourceFile`, `text` and `score`. These are the retrieved context. A `null` answer is scored as "no answer" (the correctness judge fails it, faithfulness treats it as vacuously faithful). `/openapi/v1.json` is the default readiness probe; change `ReadinessPath` for your API.

## API service

The same evaluation engine is available as a long-running HTTP service, so a pipeline, a script or a teammate can start a run on demand without a shell on the server. The CLI and the batch Docker mode are unchanged, and CI can keep using whichever is simpler (the CLI's exit code, or this API's `passed` field).

Runs are **asynchronous**: an evaluation takes seconds to minutes, so `POST /runs` validates the request, queues the run and returns `202 Accepted` with a run id. You poll `GET /runs/{id}` until it finishes and then fetch the report.

### Run it

```powershell
$env:EVALHARNESS_Api__Key = "choose-a-long-random-secret"     # callers must send this as X-Api-Key
$env:OPENAI_API_KEY = "sk-..."                                  # judge and embedding evaluators
$env:EVALHARNESS_RagApi__BaseUrl = "http://localhost:8080"      # default RAG API for runs that do not pass ragUrl
dotnet run --project src/EvalHarness.Api --urls http://localhost:8090
```

Swagger UI is at `http://localhost:8090/swagger` (use *Authorize* to enter the key). With Docker, see [Docker](#docker).

### Endpoints

Everything except `/health` and the Swagger UI needs the `X-Api-Key` header.

| Method and path | Purpose |
| --- | --- |
| `POST /runs` | Start a run. `202` with the run record and a `Location` header; `400`/`404` for a bad request, `429` when the queue is full. |
| `GET /runs/{id}` | Status and progress (`Queued`, `Running`, `Completed`, `Failed`, `Cancelled`). |
| `GET /runs/{id}/report` | The full report: aggregate metrics, every test and evaluator result, quality gates and regression. The same JSON the CLI writes. `409` while the run is still in progress. |
| `POST /runs/{id}/cancel` | Cancel a queued or running run. A running one keeps a partial report. |
| `GET /runs?limit=50` | Recent runs, newest first. |
| `GET /datasets` | Dataset names usable in a request. |
| `GET /evaluators` | The available evaluators and their defaults. |
| `GET /health` | Liveness, no key needed. |

### Starting a run

Only `dataset` is required. Leave the other fields out, or send them blank (`""`, `[]`, `0`), to use the defaults. Placeholder text such as `"string"` is not blank and is rejected with a 400.

```bash
curl -X POST http://localhost:8090/runs \
  -H "X-Api-Key: $KEY" -H "Content-Type: application/json" \
  -d '{
        "dataset": "acme-handbook",
        "ragUrl": "http://host.docker.internal:8080",
        "evaluators": ["answer-correctness", "faithfulness", "retrieval-quality"],
        "parallelism": 4,
        "baselineRunId": "3f2a9c0e5b7d4e19a6c1d2e3f4a5b6c7"
      }'
```

| Field | Notes |
| --- | --- |
| `dataset` | A file name from `GET /datasets` (the `.json` extension is omitted). Datasets are server-side files, so the server never parses arbitrary uploaded content. |
| `ragUrl` | Optional. Overrides the default RAG API for this run, but only if it matches the allowlist (below). |
| `evaluators` | Optional subset, as with the CLI's `--evaluators`. |
| `parallelism` | Optional, 1 to 32. |
| `baselineRunId` | Optional. The id of an earlier **completed** run to compare against for regression detection. |

Then poll and fetch:

```bash
curl -H "X-Api-Key: $KEY" http://localhost:8090/runs/$ID           # status, progress {completed,total}
curl -H "X-Api-Key: $KEY" http://localhost:8090/runs/$ID/report    # when status is Completed
```

A finished run record carries the CLI's verdict: `passed` is true only for exit code 0, and `exitCode` / `outcome` use the same values as the [CLI](#cli-reference-and-exit-codes): `0` / `Passed`, `1` / `QualityFailure`, `3` / `Inconclusive` (RAG API or evaluator errors), `130` / `Cancelled`. A run's `status` says whether it ran to the end, while `passed` and `outcome` say how the RAG system did. A RAG API outage therefore gives `status: Completed` with `outcome: Inconclusive`, not a failure of the service. `status: Failed` means the run itself could not complete (for example the RAG API never became ready, or an internal error); `error` says why.

### Security

- **API key.** The service refuses to start without `Api:Key`, so it cannot be left open by accident. Setting `Api:AllowAnonymous=true` is an explicit opt-out for a network you control. The key is compared in constant time and should come from an environment variable, never a file. It travels in a header, so put TLS in front of the service (a reverse proxy or load balancer) when it is reachable beyond your machine.
- **`ragUrl` allowlist.** A service that calls any URL a caller names can be used to probe your internal network (server-side request forgery, for example cloud metadata addresses). So a request's `ragUrl` must match the configured `RagApi:BaseUrl` or an entry in `Api:AllowedRagUrls` (same scheme, host and port, and a path that starts with the allowed path). Credentials, query strings and fragments in the URL are rejected. Note that any `RagApi:Headers` configured on the server (for example an `Authorization` header) are sent to whichever allowed URL a run uses, so only allowlist hosts that should receive them.
- **Budget.** Every run spends LLM calls, so authenticated callers spend your provider budget. `Api:MaxConcurrentRuns` (default 1) limits how many run at once and `Api:MaxQueuedRuns` (default 10) bounds the wait queue; beyond that, requests get `429`.
- **Paths.** Dataset names and run ids are validated against strict patterns before they touch a file path.

### Settings

Set in `appsettings.json` or as environment variables (`EVALHARNESS_Api__Key`, ...). The evaluation settings (`RagApi`, `Evaluation`, `Llm`, `QualityGates`, `Regression`, ...) work exactly as for the CLI, see [Configuration](#configuration).

| Setting | Default | Meaning |
| --- | --- | --- |
| `Api:Key` | none, required | Value callers send in `X-Api-Key`. |
| `Api:AllowAnonymous` | `false` | Run without authentication (trusted network only). |
| `Api:DatasetsDirectory` | `datasets` | Folder of dataset files (`/datasets` in Docker). |
| `Api:DataDirectory` | `data` | Run history and reports (`/data` in Docker). |
| `Api:AllowedRagUrls` | empty | Extra `ragUrl` values, comma- or semicolon-separated. |
| `Api:MaxConcurrentRuns` | `1` | Runs executing at once. |
| `Api:MaxQueuedRuns` | `10` | Runs allowed to wait for a slot. |

### History and restarts

Each run is stored as two files under `Api:DataDirectory/runs`: `{id}.run.json` (the status record) and `{id}.report.json` (the report, in the CLI's format, so it also works as a CLI `--baseline`). Mount a volume there to keep history across restarts. A run that was queued or running when the service stopped is marked `Failed` ("Interrupted by a service restart") on the next start. On a graceful shutdown, running runs are cancelled and write their partial reports first. The file store is meant for **one service instance**; do not point several replicas at the same folder.

## Docker

The Dockerfile is multi-stage (restore, build, `test`, publish, then slim runtime images that default to a non-root user) and has three targets: `api` (the default), `cli` (the one-shot batch run) and `test`. `docker-compose.yml` runs **only EvalHarness**; the RAG API under test runs separately (locally, in its own container, or remotely) and EvalHarness calls it over HTTP. Datasets are mounted **read-only**.

First, `cp .env.example .env` and fill it in (`.env` is git-ignored; never commit it). Start your RAG API and ingest the documents the dataset asks about (`samples/acme-handbook.md` for the sample dataset).

Inside a container `localhost` is the container itself. Use `http://host.docker.internal:<port>` for an API running on your machine, a service name if the API is on the same Docker network, or a normal URL for a remote one.

### API service (default)

```bash
# .env: EVAL_API_KEY=<secret callers will send>, OPENAI_API_KEY=sk-..., RAG_API_URL=http://host.docker.internal:8080
docker compose up -d --build
# -> http://localhost:8090/swagger   (change the host port with EVAL_API_PORT)
docker compose logs -f evalharness-api
```

Run history is kept in the `evalharness-data` volume. Knobs in `.env`: `EVAL_API_KEY` (required), `RAG_API_URL` (default RAG API), `RAG_ALLOWED_URLS` (extra URLs a request may pass as `ragUrl`, comma-separated), `RAG_WAIT_SECONDS`, `EVAL_MAX_CONCURRENT_RUNS`, `EVAL_API_PORT`. If the container exits immediately, `docker compose logs` will show why (usually a missing `EVAL_API_KEY`).

### One-shot batch run (CI)

```bash
docker compose run --rm --build evalharness
echo "exit code: $?"
```

This waits for the RAG API (`RAG_WAIT_SECONDS`, default 60), runs the dataset, prints the report, writes `./reports/*.json` on your machine and **exits with the quality-gate exit code**. Compose runs it as root unless you set `EVAL_UID`/`EVAL_GID`, so the bind-mounted `./reports` is writable on every platform (on Linux, set `EVAL_UID=$(id -u) EVAL_GID=$(id -g)`). Further knobs: `EVAL_DATASET` (file in `./datasets`) and `EVAL_BASELINE` (e.g. `/baselines/acme-handbook.json`, from `./baselines`). Append CLI arguments to override the default command:

```bash
docker compose run --rm evalharness \
  run --dataset /datasets/acme-handbook.json --output /reports --evaluators exact-match,retrieval-quality
```

For a RAG API that needs auth, add `EVALHARNESS_RagApi__Headers__Authorization` to the service's `environment`.

> **Git Bash on Windows** rewrites arguments that start with `/` into Windows paths. Run `export MSYS_NO_PATHCONV=1` first, or use PowerShell.

### Running the tests in Docker

```bash
docker build --target test .
```

## CI/CD

The exit code is the contract. A GitHub Actions job against a staging RAG API:

```yaml
jobs:
  rag-eval:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - name: Evaluate RAG
        env:
          OPENAI_API_KEY: ${{ secrets.OPENAI_API_KEY }}
          RAG_API_URL: https://rag.staging.example.com
          EVAL_BASELINE: /baselines/acme-handbook.json   # committed under baselines/
          EVAL_UID: 1001
          EVAL_GID: 1001
        run: mkdir -p reports && docker compose run --rm --build evalharness
      - uses: actions/upload-artifact@v4
        if: always()
        with: { name: rag-eval-reports, path: reports/ }
```

The step fails on exit code 1 (quality or regression) and 3 (inconclusive); if you want to treat an outage as a warning rather than a failure, handle code 3 in the shell. Update the baseline deliberately, by committing a new report to `baselines/` when an intended change shifts the metrics.

Without Docker, `dotnet run --project src/EvalHarness.Cli -- run ...` behaves identically.

## Adding a new evaluator

1. **Implement `IEvaluator`** (in `src/EvalHarness.Evaluators`). Return a result, or throw if you cannot produce a verdict (the runner records that as an evaluator error, not a wrong answer).

   ```csharp
   public sealed class MaxLengthEvaluator(int maxWords) : IEvaluator
   {
       public string Name => "max-length";

       public Task<EvaluatorResult> EvaluateAsync(EvaluationContext context, CancellationToken ct)
       {
           var words = (context.Response.Answer ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
           return Task.FromResult(EvaluatorResult.Of(Name, words <= maxWords, words <= maxWords ? 1 : 0,
               $"{words} words (limit {maxWords})."));
       }
   }
   ```

   `context.TestCase` has the dataset entry; `context.Response` has the answer, status, retrieved chunks and latency. Use `EvaluatorResult.Skipped(...)` when it does not apply, and the optional `Metrics` dictionary for extra numbers.
2. **Name it**: add a constant to `EvaluatorNames` in `src/EvalHarness.Core/Abstractions.cs` and to `EvaluatorNames.All`.
3. **Default settings**: add an entry to `EvaluationOptions.Defaults` (enabled, gating, threshold).
4. **Register it**: add a description to `EvaluatorCatalog.All` and a `case` to `EvaluatorCatalog.Create` (in `src/EvalHarness.Evaluators/EvaluatorCatalog.cs`). The factory takes lazy `llm`/`embeddings` accessors so a missing key only matters for evaluators that use them.
5. **Optional: aggregate it**. Per-test results appear in the report automatically. To add a run-level metric, extend `AggregateMetrics` and `ResultAggregator` (then `QualityGateOptions` / `RegressionDetector` if it should be gated or tracked). Bump `EvaluationRun.CurrentSchemaVersion` if you change the report shape incompatibly.
6. **Test it** with `FakeLlm` / `FakeEmbedder` from the test project; no network needed.

To use a different LLM or embedding provider, implement `ILlmClient` / `IEmbeddingClient` (one method each) and register them in `Composition.Build`. Evaluators only depend on those interfaces.

## Architecture

```
src/
  EvalHarness.Core         Models, options, interfaces (IRagClient, IEvaluator, ILlmClient, IEmbeddingClient),
                           dataset loading + validation, JSON settings, HTTP RetryHandler
  EvalHarness.RagClient    HttpRagClient + RagApiContract: the only code that knows the RAG API's JSON shape
  EvalHarness.Evaluators   The six evaluators, retrieval metrics, OpenAI-compatible LLM/embedding clients,
                           concurrency limiters, EvaluatorCatalog (factory)
  EvalHarness.Runner       EvaluationRunner (parallel, fault-isolated), ResultAggregator, QualityGateEvaluator,
                           RegressionDetector, ExitCodes
  EvalHarness.Reporting    ConsoleReportWriter, JsonReportStore (write + read baselines)
  EvalHarness.Hosting      DI composition and PreparedEvaluation (wait for ready, run, gates, regression),
                           shared by the CLI and the API
  EvalHarness.Cli          Argument parsing, configuration, commands, exit codes
  EvalHarness.Api          ASP.NET Core service: API-key auth, run queue, file-based history, ragUrl allowlist
tests/EvalHarness.Tests    Unit + integration tests; all fakes, no network, no real LLM
datasets/                  Evaluation datasets (JSON)
samples/                   Source document(s) the sample dataset asks about; ingest into the RAG API under test
baselines/                 Baseline reports to compare against (create it when you save your first one)
reports/                   Generated reports (git-ignored)
```

Dependencies point inward: evaluators and the runner see only `RagResponse` and interfaces, never HTTP or the API's field names. Tests cover dataset validation, cosine and vector maths, exact match, retrieval metrics and matching, each evaluator against a fake judge, aggregation, retry/backoff/`Retry-After`/timeout behaviour, RAG API failure mapping, cancellation, parallelism limits, regression detection, quality gates and exit codes, report round-tripping, the CLI end to end with fakes, and the API (auth, run lifecycle, validation, allowlist, baselines, cancel, queue limits, persistence) through an in-process test server:

```bash
dotnet test
```

## Limitations

- **API service scope**: one shared API key (no per-user accounts or rate limits per caller), an in-memory queue, and a file-based history intended for a single instance. Runs interrupted by a restart are marked failed, not resumed. Datasets are server-side files; there is no upload endpoint.
- **Single provider shipped**: OpenAI and OpenAI-compatible endpoints (set `BaseUrl`). Others need an `ILlmClient`/`IEmbeddingClient`.
- **Judge variance**: LLM judges are noisy even at temperature 0, and a judge model change shifts scores. Keep regression allowances above that noise, pin the judge model, and re-baseline when you change it.
- **Questions the RAG should decline** (no answer in the documents) are not modelled yet: every test needs an expected answer and source.
- **Single-turn Q&A** over a request/response HTTP API; no streaming or multi-turn conversations.
- Retrieved chunks are scored in the order the API returns them; the harness cannot know the API's internal ranking beyond that.
- The default readiness probe is a plain GET of `ReadinessPath`; any non-5xx response counts as ready.
- **Not verified against live services.** Everything that does not need external services is covered by automated tests, and the Docker image and Compose workflow were exercised end to end against a local stand-in for the RAG API. The OpenAI-backed evaluators were not run live (they need an API key), so expect to adjust prompts and thresholds on first real use.
