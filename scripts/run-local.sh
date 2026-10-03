#!/usr/bin/env bash
# Full local environment: DocRAG + corpus seeding + EvalHarness. Exits with EvalHarness's exit code
# (0 pass, 1 quality gate/regression, 2 usage/config, 3 inconclusive). Extra arguments replace the default command.
set -u
cd "$(dirname "$0")/.."

# Stop Git Bash on Windows from rewriting /datasets/... style arguments into Windows paths.
export MSYS_NO_PATHCONV=1

docker compose run --rm --build evalharness "$@"
code=$?
docker compose down
exit $code
