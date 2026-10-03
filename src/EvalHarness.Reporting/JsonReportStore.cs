using System.Text.Json;
using EvalHarness.Core;

namespace EvalHarness.Reporting;

/// <summary>Reads and writes the machine-readable JSON report. The same file serves as a baseline for later runs.</summary>
public static class JsonReportStore
{
    /// <summary>
    /// Writes eval-{timestamp}-{runId}.json plus latest.json (a copy, so CI can always find the newest report)
    /// into the directory, creating it if needed. Returns the timestamped path.
    /// </summary>
    public static async Task<string> WriteToDirectoryAsync(EvaluationRun run, string directory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        var name = $"eval-{run.Run.StartedAt.UtcDateTime:yyyyMMdd-HHmmss}-{run.Run.RunId[..8]}.json";
        var path = Path.Combine(directory, name);
        await WriteAsync(run, path, cancellationToken);
        File.Copy(path, Path.Combine(directory, "latest.json"), overwrite: true);
        return path;
    }

    public static async Task WriteAsync(EvaluationRun run, string path, CancellationToken cancellationToken = default)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } dir) Directory.CreateDirectory(dir);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, run, EvalJson.Options, cancellationToken);
    }

    /// <exception cref="InvalidDataException">The file is missing, not a report, or from an incompatible version.</exception>
    public static async Task<EvaluationRun> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var run = await JsonSerializer.DeserializeAsync<EvaluationRun>(stream, EvalJson.Options, cancellationToken);
            if (run is null) throw new InvalidDataException($"'{path}' is empty.");
            if (run.SchemaVersion != EvaluationRun.CurrentSchemaVersion)
                throw new InvalidDataException(
                    $"'{path}' has report schema version {run.SchemaVersion}; this harness reads version {EvaluationRun.CurrentSchemaVersion}.");
            return run;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Cannot read evaluation report '{path}': {ex.Message}", ex);
        }
    }
}
