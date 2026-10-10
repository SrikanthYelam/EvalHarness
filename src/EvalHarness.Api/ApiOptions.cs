namespace EvalHarness.Api;

public sealed class ApiOptions
{
    public const string SectionName = "Api";

    /// <summary>Callers must send this in the X-Api-Key header. Supply it via EVALHARNESS_Api__Key, never a file.</summary>
    public string? Key { get; set; }

    /// <summary>Explicit opt-out of authentication, for a private network you control. Without a Key the service refuses to start.</summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>Folder of dataset files; a request names one by file name without the .json extension.</summary>
    public string DatasetsDirectory { get; set; } = "datasets";

    /// <summary>Where run records and reports are stored. Mount a volume here to keep history across restarts.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>
    /// Comma- or semicolon-separated URLs a request's ragUrl may point at (scheme, host and port must match; the path
    /// must start with the allowed path). The configured RagApi:BaseUrl is always allowed.
    /// </summary>
    public string AllowedRagUrls { get; set; } = "";

    /// <summary>
    /// Comma- or semicolon-separated names a request's requestFields may set (e.g. the RAG API's retrieval mode or topK).
    /// Anything else is rejected, so a caller cannot rewrite arbitrary parts of the request sent to the RAG API.
    /// </summary>
    public string AllowedRequestFields { get; set; } = "topK,mode";

    /// <summary>Runs executing at once. Each run spends LLM budget and loads the RAG API, so the default is 1.</summary>
    public int MaxConcurrentRuns { get; set; } = 1;

    /// <summary>Runs allowed to wait for a slot before new requests get 429.</summary>
    public int MaxQueuedRuns { get; set; } = 10;
}
