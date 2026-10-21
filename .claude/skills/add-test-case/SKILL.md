---
name: add-test-case
description: Add or edit test cases in an EvalHarness dataset (datasets/*.json) with stable expected answers and expected sources, then validate the file. Use when asked to extend a dataset, add questions for a document, or create a new dataset.
---

# Add test cases to a dataset

Datasets are JSON files in `datasets/`; nothing is hard-coded in C#. The loader rejects typos and incomplete cases, so the
goal here is cases that are *good*, not just valid.

## Format

```json
{
  "id": "vacation-carryover",
  "category": "time-off",
  "difficulty": "easy",
  "question": "How many unused vacation days can I carry over into the next year?",
  "expectedAnswer": "Up to 5 unused vacation days carry over; anything above that expires on January 31.",
  "expectedSources": [{ "documentName": "acme-handbook.md", "chunkContains": "carry over into the next calendar year" }]
}
```

- `id`: unique (case-insensitive), kebab-case, stable. Regression comparison matches tests across runs **by id**, so never
  reuse or rename an id for a different question.
- `difficulty`: `easy`, `medium` or `hard` (hard = needs reasoning or several facts).
- `expectedAnswer`: the ground truth the judge compares against. Write the facts a correct answer must contain, in a
  complete sentence. It does not have to match the system's wording, and it must not contain facts the documents do not support.
- `expectedSources`: at least one entry, with at least one of `documentId`, `documentName`, `chunkId`, `chunkContains`. **Every
  field you set must match the same retrieved chunk.** Prefer `documentName` + `chunkContains` (a short, distinctive phrase from
  the chunk): chunk ids change when a document is re-ingested. Match text exactly as extracted; PDFs can have broken ligatures
  or characters, so pick a phrase that survives extraction.
- Optional `evaluators`: restrict the test to named evaluators (for example a deterministic fact that suits `exact-match`).

## Writing good cases

- Ask the way a user would. Do not copy the document's sentence into the question.
- One fact per case where you can, plus a few multi-fact and reasoning cases. Cover each category of the documents.
- A case must be answerable from the documents that will be ingested. Questions that should be **declined** are not supported
  yet (every case needs an expected answer and source); do not fake them.
- Do not put personal or sensitive text in `expectedAnswer`. Reports include answers and retrieved text.

## Steps

1. Edit the dataset file (or create `datasets/<name>.json` with `name`, optional `description`, and `testCases`).
2. Validate (no API calls):
   `dotnet run --project src/EvalHarness.Cli -- validate --dataset datasets/<file>.json`
   It lists every problem at once (duplicate ids, missing fields, unknown difficulty, expected sources without an identifier).
3. Make sure the source documents are ingested in the RAG system under test; EvalHarness does not ingest. Keep the document
   alongside as a sample under `samples/` if it is meant to be reproducible.
4. Run only the new cases cheaply first: use the `run-eval` skill with `evaluators: ["retrieval-quality"]` to confirm the
   expected sources are actually matched, then a full run.
5. Tell the user that changing a dataset changes its hash, so a baseline taken before the change will produce a "datasets
   differ" warning and should be re-taken.

## Do not

- Weaken or delete a failing case to make a run pass. If a case is wrong (the answer is not in the documents), say why and fix
  the case; if the system is wrong, the case is doing its job.
- Remove or reorder existing ids without saying so.
