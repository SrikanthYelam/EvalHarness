using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace EvalHarness.Api;

/// <summary>
/// Requires the X-Api-Key header on everything except health and the Swagger UI. Compared in constant time.
/// With no key configured the service refuses to start unless Api:AllowAnonymous is set (see Program).
/// </summary>
public sealed class ApiKeyMiddleware(RequestDelegate next, IOptions<ApiOptions> options)
{
    public const string HeaderName = "X-Api-Key";

    public async Task InvokeAsync(HttpContext context)
    {
        var settings = options.Value;
        if (settings.AllowAnonymous || IsOpen(context.Request.Path) || IsValid(context.Request.Headers[HeaderName], settings.Key))
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { title = $"Missing or invalid {HeaderName} header.", status = 401 });
    }

    private static bool IsOpen(PathString path) =>
        path == "/" || path.StartsWithSegments("/health") || path.StartsWithSegments("/swagger");

    private static bool IsValid(string? presented, string? expected)
    {
        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(expected)) return false;
        // Hash first so the comparison is fixed-length regardless of what the caller sends.
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }
}
