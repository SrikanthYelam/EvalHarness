using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EvalHarness.Core;

namespace EvalHarness.RagClient;

/// <summary>
/// The single place that knows the RAG API's JSON shape. It is driven entirely by <see cref="RagApiOptions"/>,
/// so another API can be evaluated by changing configuration; evaluators only ever see <see cref="RagResponse"/>.
/// </summary>
public sealed class RagApiContract(RagApiOptions options)
{
    public string BuildRequestBody(string question)
    {
        var body = new JsonObject { [options.QuestionField] = question };
        foreach (var (name, value) in options.RequestFields) body[name] = ToScalar(value);
        return body.ToJsonString();
    }

    /// <exception cref="RagApiException">The payload does not look like the configured shape.</exception>
    public (string? Answer, string? Status, IReadOnlyList<RetrievedChunk> Chunks) Parse(JsonElement root)
    {
        var hasAnswer = TryGet(root, options.AnswerPath, out var answerElement);
        var hasChunks = TryGet(root, options.ChunksPath, out var chunksElement);
        if (!hasAnswer && !hasChunks)
            throw new RagApiException(
                $"The response has neither '{options.AnswerPath}' nor '{options.ChunksPath}'; check the RagApi mapping settings.");

        string? status = null;
        if (options.StatusPath is not null && TryGet(root, options.StatusPath, out var statusElement))
            status = AsString(statusElement);

        var chunks = new List<RetrievedChunk>();
        if (hasChunks && chunksElement.ValueKind != JsonValueKind.Null)
        {
            if (chunksElement.ValueKind != JsonValueKind.Array)
                throw new RagApiException($"'{options.ChunksPath}' in the response is not an array.");
            foreach (var chunk in chunksElement.EnumerateArray())
                chunks.Add(new RetrievedChunk(
                    chunks.Count + 1,
                    Field(chunk, options.DocumentIdField),
                    Field(chunk, options.DocumentNameField),
                    Field(chunk, options.ChunkIdField),
                    Field(chunk, options.ChunkTextField) ?? "",
                    Number(chunk, options.ChunkScoreField)));
        }

        return (hasAnswer ? AsString(answerElement) : null, status, chunks);
    }

    private static JsonNode? ToScalar(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? JsonValue.Create(l)
        : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? JsonValue.Create(d)
        : bool.TryParse(value, out var b) ? JsonValue.Create(b)
        : JsonValue.Create(value);

    private static string? Field(JsonElement element, string? path) =>
        path is not null && TryGet(element, path, out var value) ? AsString(value) : null;

    private static double? Number(JsonElement element, string? path) =>
        path is not null && TryGet(element, path, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static string? AsString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        _ => null,
    };

    /// <summary>Walks a dot-separated path of property names (case-insensitive).</summary>
    private static bool TryGet(JsonElement element, string path, out JsonElement value)
    {
        value = element;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (value.ValueKind != JsonValueKind.Object) return false;
            var found = false;
            foreach (var property in value.EnumerateObject())
            {
                if (!property.NameEquals(segment) && !string.Equals(property.Name, segment, StringComparison.OrdinalIgnoreCase))
                    continue;
                value = property.Value;
                found = true;
                break;
            }
            if (!found) return false;
        }
        return true;
    }
}
