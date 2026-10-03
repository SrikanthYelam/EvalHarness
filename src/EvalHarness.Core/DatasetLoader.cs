using System.Security.Cryptography;
using System.Text.Json;

namespace EvalHarness.Core;

/// <summary>The dataset file is unreadable, not valid JSON, or has invalid test cases. Carries every problem found.</summary>
public sealed class DatasetValidationException(IReadOnlyList<string> errors)
    : Exception($"Dataset is invalid ({errors.Count} problem{(errors.Count == 1 ? "" : "s")}): {string.Join("; ", errors)}")
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public static class DatasetLoader
{
    public static readonly IReadOnlyList<string> Difficulties = ["easy", "medium", "hard"];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // A typo such as "expectedAnser" should be reported, not silently ignored.
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    private sealed class DatasetFile
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<TestCase>? TestCases { get; set; }
    }

    /// <param name="knownEvaluators">When given, per-test evaluator names are checked against it.</param>
    /// <exception cref="DatasetValidationException"/>
    public static Dataset Load(string path, IReadOnlyCollection<string>? knownEvaluators = null)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DatasetValidationException([$"Cannot read dataset file '{path}': {ex.Message}"]);
        }

        return Parse(bytes, path, knownEvaluators);
    }

    public static Dataset Parse(byte[] json, string path, IReadOnlyCollection<string>? knownEvaluators = null)
    {
        DatasetFile? file;
        try
        {
            file = JsonSerializer.Deserialize<DatasetFile>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is { } line ? $" (line {line + 1})" : "";
            throw new DatasetValidationException([$"Invalid JSON{where}: {ex.Message}"]);
        }

        if (file?.TestCases is null)
            throw new DatasetValidationException(["The dataset must be a JSON object with a 'testCases' array."]);

        var errors = Validate(file.TestCases, knownEvaluators);
        if (errors.Count > 0) throw new DatasetValidationException(errors);

        var name = string.IsNullOrWhiteSpace(file.Name) ? System.IO.Path.GetFileNameWithoutExtension(path) : file.Name;
        var sha = Convert.ToHexString(SHA256.HashData(json)).ToLowerInvariant();
        return new Dataset(name, file.Description, path, sha, file.TestCases);
    }

    private static List<string> Validate(IReadOnlyList<TestCase> cases, IReadOnlyCollection<string>? knownEvaluators)
    {
        var errors = new List<string>();
        if (cases.Count == 0) errors.Add("'testCases' must contain at least one test case.");

        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < cases.Count; i++)
        {
            var tc = cases[i];
            if (tc is null)
            {
                errors.Add($"testCases[{i}]: must be an object, not null.");
                continue;
            }

            var label = string.IsNullOrWhiteSpace(tc.Id) ? $"testCases[{i}]" : $"testCases[{i}] (id '{tc.Id}')";

            if (string.IsNullOrWhiteSpace(tc.Id)) errors.Add($"{label}: 'id' is required.");
            else if (seen.TryGetValue(tc.Id, out var first))
                errors.Add($"{label}: duplicate id, already used by testCases[{first}].");
            else seen[tc.Id] = i;

            if (string.IsNullOrWhiteSpace(tc.Category)) errors.Add($"{label}: 'category' is required.");
            if (string.IsNullOrWhiteSpace(tc.Question)) errors.Add($"{label}: 'question' is required.");
            if (string.IsNullOrWhiteSpace(tc.ExpectedAnswer)) errors.Add($"{label}: 'expectedAnswer' is required.");

            if (string.IsNullOrWhiteSpace(tc.Difficulty)) errors.Add($"{label}: 'difficulty' is required.");
            else if (!Difficulties.Contains(tc.Difficulty, StringComparer.OrdinalIgnoreCase))
                errors.Add($"{label}: 'difficulty' must be one of {string.Join(", ", Difficulties)} (was '{tc.Difficulty}').");

            if (tc.ExpectedSources is null || tc.ExpectedSources.Count == 0)
                errors.Add($"{label}: 'expectedSources' needs at least one entry.");
            else
                for (var s = 0; s < tc.ExpectedSources.Count; s++)
                    if (tc.ExpectedSources[s] is not { HasIdentifier: true })
                        errors.Add($"{label}: expectedSources[{s}] needs at least one of documentId, documentName, chunkId, chunkContains.");

            if (tc.Evaluators is not null && knownEvaluators is not null)
                foreach (var name in tc.Evaluators.Where(n => !knownEvaluators.Contains(n, StringComparer.OrdinalIgnoreCase)))
                    errors.Add($"{label}: unknown evaluator '{name}'. Known: {string.Join(", ", knownEvaluators)}.");
        }

        return errors;
    }
}
