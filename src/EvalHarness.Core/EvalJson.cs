using System.Text.Json;
using System.Text.Json.Serialization;

namespace EvalHarness.Core;

/// <summary>
/// JSON settings for reports (written and read back as baselines). Nulls are written explicitly so consumers can
/// tell "not measured" from a missing field.
/// </summary>
public static class EvalJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
