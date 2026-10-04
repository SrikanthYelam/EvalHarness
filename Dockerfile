# syntax=docker/dockerfile:1

# Targets:
#   api   (default)  the EvalHarness API service         docker build .
#   cli              the one-shot batch CLI, for CI      docker build --target cli .
#   test             runs the test suite                 docker build --target test .

# ---- restore: only project files, so the NuGet layer is cached until a dependency changes ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS restore
WORKDIR /src
COPY global.json Directory.Build.props EvalHarness.sln ./
COPY src/EvalHarness.Core/EvalHarness.Core.csproj src/EvalHarness.Core/
COPY src/EvalHarness.RagClient/EvalHarness.RagClient.csproj src/EvalHarness.RagClient/
COPY src/EvalHarness.Evaluators/EvalHarness.Evaluators.csproj src/EvalHarness.Evaluators/
COPY src/EvalHarness.Runner/EvalHarness.Runner.csproj src/EvalHarness.Runner/
COPY src/EvalHarness.Reporting/EvalHarness.Reporting.csproj src/EvalHarness.Reporting/
COPY src/EvalHarness.Hosting/EvalHarness.Hosting.csproj src/EvalHarness.Hosting/
COPY src/EvalHarness.Cli/EvalHarness.Cli.csproj src/EvalHarness.Cli/
COPY src/EvalHarness.Api/EvalHarness.Api.csproj src/EvalHarness.Api/
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

FROM build AS publish-cli
RUN dotnet publish src/EvalHarness.Cli -c Release --no-build -o /app

FROM build AS publish-api
RUN dotnet publish src/EvalHarness.Api -c Release --no-build -o /app

# ---- cli: one-shot batch run; exits with the quality-gate exit code ----
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS cli
WORKDIR /app
COPY --from=publish-cli /app .
# Datasets are mounted read-only at /datasets and reports written to /reports (a bind mount), so nothing is lost
# when the container exits. No secrets are baked in: pass OPENAI_API_KEY etc. at run time.
ENV EVALHARNESS_Logging__Format=simple
VOLUME ["/reports"]
USER $APP_UID
ENTRYPOINT ["dotnet", "EvalHarness.Cli.dll"]
CMD ["run", "--dataset", "/datasets/acme-handbook.json", "--output", "/reports"]

# ---- api (last stage, so it is the default target): long-running service ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS api
WORKDIR /app
COPY --from=publish-api /app .
# Datasets are mounted read-only at /datasets; run history and reports live in /data (mount a volume to keep them).
# The key (EVALHARNESS_Api__Key) and provider keys are passed at run time, never baked in.
ENV ASPNETCORE_URLS=http://+:8080 \
    EVALHARNESS_Api__DatasetsDirectory=/datasets \
    EVALHARNESS_Api__DataDirectory=/data
RUN mkdir -p /data /datasets && chown $APP_UID /data
VOLUME ["/data"]
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "EvalHarness.Api.dll"]
