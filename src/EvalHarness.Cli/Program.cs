using System.Runtime.InteropServices;
using EvalHarness.Cli;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // First Ctrl+C stops the run gracefully so the partial report is still written.
    e.Cancel = true;
    cts.Cancel();
};
// `docker stop` sends SIGTERM.
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    cts.Cancel();
});

return await CliApp.RunAsync(args, Console.Out, Console.Error, cts.Token);
