namespace EvalHarness.Api;

/// <summary>
/// Decides which RAG API URLs a caller may ask the service to call. Without a policy, an authenticated caller could
/// make the server send requests to any address it can reach (server-side request forgery).
/// </summary>
public sealed class RagUrlPolicy
{
    private readonly List<Uri> _allowed = [];

    public RagUrlPolicy(string configuredBaseUrl, string allowedUrls)
    {
        if (TryParse(configuredBaseUrl, out var configured)) _allowed.Add(configured);
        foreach (var entry in allowedUrls.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParse(entry, out var uri))
                throw new InvalidOperationException($"Api:AllowedRagUrls contains an invalid http(s) URL: '{entry}'.");
            _allowed.Add(uri);
        }
    }

    public IEnumerable<string> Allowed => _allowed.Select(u => u.GetLeftPart(UriPartial.Path));

    /// <summary>Returns null when allowed, otherwise the reason it is not.</summary>
    public string? Check(string requested)
    {
        if (!TryParse(requested, out var uri)) return "ragUrl must be an absolute http(s) URL.";
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return "ragUrl must not contain credentials, a query string or a fragment.";

        var requestedPath = uri.AbsolutePath.TrimEnd('/');
        var ok = _allowed.Any(a =>
            a.Scheme == uri.Scheme &&
            string.Equals(a.Host, uri.Host, StringComparison.OrdinalIgnoreCase) &&
            a.Port == uri.Port &&
            // Segment-aware prefix: /api allows /api and /api/v2 but not /apiary.
            (requestedPath.Equals(a.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
             requestedPath.StartsWith(a.AbsolutePath.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)));

        return ok ? null : $"ragUrl is not on the allowlist. Allowed: {string.Join(", ", Allowed)}.";
    }

    private static bool TryParse(string value, out Uri uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri!) && uri.Scheme is "http" or "https";
}
