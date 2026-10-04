using System.Text.Json;
using System.Text.RegularExpressions;
using EvalHarness.Core;
using EvalHarness.Reporting;

namespace EvalHarness.Api;

/// <summary>
/// File-based run history: {id}.run.json (the <see cref="RunRecord"/>) and {id}.report.json (the same report format
/// the CLI writes, so a report can be used as a CLI baseline and vice versa) under {DataDirectory}/runs.
/// </summary>
public sealed partial class RunStore
{
    private readonly string _dir;

    public RunStore(string dataDirectory)
    {
        _dir = Path.Combine(dataDirectory, "runs");
        Directory.CreateDirectory(_dir);
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex IdPattern();

    /// <summary>Ids are always generated here; checking the shape also keeps request input out of file paths.</summary>
    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    public static string NewId() => Guid.NewGuid().ToString("N");

    private string RecordPath(string id) => Path.Combine(_dir, $"{id}.run.json");
    private string ReportPath(string id) => Path.Combine(_dir, $"{id}.report.json");

    public bool ReportExists(string id) => IsValidId(id) && File.Exists(ReportPath(id));

    public async Task SaveRecordAsync(RunRecord record)
    {
        // Write-then-rename so a crash never leaves a half-written record behind.
        var path = RecordPath(record.Id);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(record, EvalJson.Options));
        File.Move(temp, path, overwrite: true);
    }

    public Task SaveReportAsync(string id, EvaluationRun run) => JsonReportStore.WriteAsync(run, ReportPath(id));

    public Task<EvaluationRun> ReadReportAsync(string id, CancellationToken ct = default) =>
        JsonReportStore.ReadAsync(ReportPath(id), ct);

    public IReadOnlyList<RunRecord> LoadRecords()
    {
        var records = new List<RunRecord>();
        foreach (var file in Directory.EnumerateFiles(_dir, "*.run.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(file), EvalJson.Options) is { } record &&
                    IsValidId(record.Id))
                    records.Add(record);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // A damaged record should not stop the service from starting.
            }
        }
        return records;
    }
}
