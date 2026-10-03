# syntax=docker/dockerfile:1

# ---- restore: only project files, so the NuGet layer is cached until a dependency changes ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS restore
WORKDIR /src
COPY global.json Directory.Build.props EvalHarness.sln ./
COPY src/EvalHarness.Core/EvalHarness.Core.csproj src/EvalHarness.Core/
COPY src/EvalHarness.RagClient/EvalHarness.RagClient.csproj src/EvalHarness.RagClient/
COPY src/EvalHarness.Evaluators/EvalHarness.Evaluators.csproj src/EvalHarness.Evaluators/
COPY src/EvalHarness.Runner/EvalHarness.Runner.csproj src/EvalHarness.Runner/
COPY src/EvalHarness.Reporting/EvalHarness.Reporting.csproj src/EvalHarness.Reporting/
COPY src/EvalHarness.Cli/EvalHarness.Cli.csproj src/EvalHarness.Cli/
COPY tests/EvalHarness.Tests/EvalHarness.Tests.csproj tests/EvalHarness.Tests/
RUN dotnet restore EvalHarness.sln

FROM restore AS build
COPY src/ src/
COPY tests/ tests/
COPY datasets/ datasets/
RUN dotnet build EvalHarness.sln -c Release --no-restore

# `docker build --target test .` runs the unit/integration tests (no network or LLM needed).
FROM build AS test
RUN dotnet test EvalHarness.sln -c Release --no-build

FROM build AS publish
RUN dotnet publish src/EvalHarness.Cli -c Release --no-build -o /app

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app
COPY --from=publish /app .
# Datasets are mounted read-only at /datasets and reports written to /reports (a bind mount), so nothing is lost
# when the container exits. No secrets are baked in: pass OPENAI_API_KEY etc. at run time.
ENV EVALHARNESS_Logging__Format=simple
VOLUME ["/reports"]
USER $APP_UID
ENTRYPOINT ["dotnet", "EvalHarness.Cli.dll"]
CMD ["run", "--dataset", "/datasets/acme-handbook.json", "--output", "/reports"]
