# Full local environment: DocRAG + corpus seeding + EvalHarness. Exits with EvalHarness's exit code
# (0 pass, 1 quality gate/regression, 2 usage/config, 3 inconclusive). Extra arguments replace the default command.
Set-Location (Join-Path $PSScriptRoot "..")

docker compose run --rm --build evalharness @args
$code = $LASTEXITCODE
docker compose down
exit $code
