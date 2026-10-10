namespace EvalHarness.Cli;

/// <summary>Bad command line. Reported with the usage text and exit code 2.</summary>
public sealed class UsageException(string message) : Exception(message);

/// <summary>`command --name value` / `--name=value` parsing; no interactivity, no prompts.</summary>
public sealed class Arguments
{
    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        ["run"] = ["dataset", "output", "baseline", "rag-url", "config", "parallelism", "evaluators", "wait-for-ready", "repeats"],
        ["compare"] = ["baseline", "current", "output", "config"],
        ["validate"] = ["dataset"],
        ["list-evaluators"] = [],
    };

    public string? Command { get; private init; }
    public bool Help { get; private init; }
    private Dictionary<string, string> Options { get; init; } = new();

    public string? Get(string name) => Options.GetValueOrDefault(name);

    public string Require(string name) =>
        Get(name) ?? throw new UsageException($"Missing required option --{name}.");

    /// <exception cref="UsageException"/>
    public static Arguments Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] is "-h" or "--help" or "help") return new Arguments { Help = true };

        var command = args[0];
        if (!Allowed.TryGetValue(command, out var allowed))
            throw new UsageException($"Unknown command '{command}'.");

        var options = new Dictionary<string, string>();
        for (var i = 1; i < args.Count; i++)
        {
            var token = args[i];
            if (token is "-h" or "--help") return new Arguments { Command = command, Help = true };
            if (!token.StartsWith("--", StringComparison.Ordinal))
                throw new UsageException($"Unexpected argument '{token}'.");

            var name = token[2..];
            string? value = null;
            var eq = name.IndexOf('=');
            if (eq >= 0) (name, value) = (name[..eq], name[(eq + 1)..]);

            if (!allowed.Contains(name)) throw new UsageException($"Unknown option --{name} for '{command}'.");
            if (value is null)
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new UsageException($"Option --{name} needs a value.");
                value = args[++i];
            }
            options[name] = value;
        }

        return new Arguments { Command = command, Options = options };
    }
}
