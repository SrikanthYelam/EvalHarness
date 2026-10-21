using EvalHarness.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// An MCP server over stdio: stdout carries only protocol messages, so every log line goes to stderr.
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

var options = EvalHarnessOptions.FromEnvironment();
builder.Services.AddSingleton(options);
builder.Services.AddHttpClient<EvalHarnessClient>();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var host = builder.Build();

var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("EvalHarness.Mcp");
if (string.IsNullOrWhiteSpace(options.ApiKey))
    log.LogWarning("EVALHARNESS_API_KEY is not set; tools will report an error until it is.");
if (Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
    log.LogWarning("EVALHARNESS_URL uses http:// for a non-local host, so the API key travels unencrypted. Use https:// when you can.");

await host.RunAsync();
