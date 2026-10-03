using System.Text;
using EvalHarness.Core;

namespace EvalHarness.Tests;

public class DatasetLoaderTests
{
    private const string ValidCase = """
        { "id": "a", "category": "c", "difficulty": "easy", "question": "Q?", "expectedAnswer": "A",
          "expectedSources": [{ "documentName": "doc.md" }] }
        """;

    private static Dataset Parse(string json, IReadOnlyCollection<string>? known = null) =>
        DatasetLoader.Parse(Encoding.UTF8.GetBytes(json), "ds.json", known);

    private static IReadOnlyList<string> Errors(string json, IReadOnlyCollection<string>? known = null) =>
        Assert.Throws<DatasetValidationException>(() => Parse(json, known)).Errors;

    [Fact]
    public void Valid_dataset_loads_with_name_and_hash()
    {
        var ds = Parse($$"""{ "name": "mine", "testCases": [ {{ValidCase}} ] }""");

        Assert.Equal("mine", ds.Name);
        Assert.Single(ds.TestCases);
        Assert.Equal(64, ds.Sha256.Length);
    }

    [Fact]
    public void Name_defaults_to_file_name()
    {
        Assert.Equal("ds", Parse($$"""{ "testCases": [ {{ValidCase}} ] }""").Name);
    }

    [Fact]
    public void Invalid_json_is_reported()
    {
        var errors = Errors("{ not json");
        Assert.Contains("Invalid JSON", Assert.Single(errors));
    }

    [Fact]
    public void Missing_test_cases_array_is_reported()
    {
        Assert.Contains("testCases", Assert.Single(Errors("{}")));
    }

    [Fact]
    public void Empty_test_cases_is_reported()
    {
        Assert.Contains("at least one", Assert.Single(Errors("""{ "testCases": [] }""")));
    }

    [Fact]
    public void Missing_required_fields_are_all_reported_together()
    {
        var errors = Errors("""{ "testCases": [ { "id": "x" } ] }""");

        Assert.Contains(errors, e => e.Contains("'category' is required"));
        Assert.Contains(errors, e => e.Contains("'question' is required"));
        Assert.Contains(errors, e => e.Contains("'expectedAnswer' is required"));
        Assert.Contains(errors, e => e.Contains("'difficulty' is required"));
        Assert.Contains(errors, e => e.Contains("'expectedSources' needs at least one"));
    }

    [Fact]
    public void Duplicate_ids_are_reported_case_insensitively()
    {
        var errors = Errors($$"""{ "testCases": [ {{ValidCase}}, {{ValidCase.Replace("\"a\"", "\"A\"")}} ] }""");
        Assert.Contains("duplicate id", Assert.Single(errors));
    }

    [Fact]
    public void Unknown_difficulty_is_reported()
    {
        var errors = Errors($$"""{ "testCases": [ {{ValidCase.Replace("easy", "impossible")}} ] }""");
        Assert.Contains("'difficulty' must be one of", Assert.Single(errors));
    }

    [Fact]
    public void Expected_source_without_any_identifier_is_reported()
    {
        var errors = Errors($$"""{ "testCases": [ {{ValidCase.Replace("""{ "documentName": "doc.md" }""", "{}")}} ] }""");
        Assert.Contains("expectedSources[0]", Assert.Single(errors));
    }

    [Fact]
    public void Misspelled_field_is_reported_instead_of_ignored()
    {
        var errors = Errors($$"""{ "testCases": [ {{ValidCase.Replace("expectedAnswer", "expectedAnser")}} ] }""");
        Assert.Contains("Invalid JSON", Assert.Single(errors));
    }

    [Fact]
    public void Unknown_per_test_evaluator_is_reported_when_known_names_are_given()
    {
        var json = $$"""{ "testCases": [ {{ValidCase.Replace("\"question\"", "\"evaluators\": [\"nope\"], \"question\"")}} ] }""";

        Assert.Contains("unknown evaluator 'nope'", Assert.Single(Errors(json, EvaluatorNames.All)));
        Assert.Single(Parse(json).TestCases); // not checked without a list of known names
    }

    [Fact]
    public void Missing_file_is_reported()
    {
        var ex = Assert.Throws<DatasetValidationException>(() => DatasetLoader.Load(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid() + ".json")));
        Assert.Contains("Cannot read", ex.Errors[0]);
    }

    [Fact]
    public void Shipped_sample_dataset_is_valid()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../datasets/acme-handbook.json"));
        var ds = DatasetLoader.Load(path, EvaluatorNames.All);
        Assert.True(ds.TestCases.Count >= 10);
    }
}
