// See https://aka.ms/new-console-template for more information
using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.RegularExpressions;

namespace WebSocketStress;

public class Configuration
{
    public IPEndPoint ServerEndpoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 0);
    public RunMode RunMode { get; set; }
    public int RandomSeed { get; set; }
    public double CancellationProbability { get; set; }
    public int MaxConnections { get; set; }
    public int MaxBufferLength { get; set; }
    public TimeSpan? MaxExecutionTime { get; set; }
    public TimeSpan DisplayInterval { get; set; }

    public static bool TryParseCli(string[] args, [NotNullWhen(true)] out Configuration? config)
    {
        var cmd = new RootCommand();
        cmd.AddOption(new Option(new[] { "--help", "-h" }, "Display this help text."));
        cmd.AddOption(new Option(new[] { "--mode", "-m" }, "Stress suite execution mode. Defaults to 'both'.") { Argument = new Argument<RunMode>("runMode", RunMode.both) });
        cmd.AddOption(new Option(new[] { "--cancellation-probability", "-p" }, "Cancellation probability 0 <= p <= 1 for a given connection. Defaults to 0.1") { Argument = new Argument<double>("probability", 0.1) });
        cmd.AddOption(new Option(new[] { "--num-connections", "-n" }, "Max number of connections to open concurrently.") { Argument = new Argument<int>("connections", Environment.ProcessorCount) });
        cmd.AddOption(new Option(new[] { "--server-endpoint", "-e" }, "Endpoint to bind to if server, endpoint to listen to if client.") { Argument = new Argument<string>("ipEndpoint", "127.0.0.1:5002") });
        cmd.AddOption(new Option(new[] { "--max-execution-time", "-t" }, "Maximum stress suite execution time, in minutes. Defaults to infinity.") { Argument = new Argument<double?>("minutes", null) });
        cmd.AddOption(new Option(new[] { "--max-buffer-length", "-b" }, "Maximum buffer length to write on ssl stream. Defaults to 8192.") { Argument = new Argument<int>("bytes", 8192) });
        cmd.AddOption(new Option(new[] { "--display-interval", "-i" }, "Client stats display interval, in seconds. Defaults to 5 seconds.") { Argument = new Argument<double>("seconds", 5) });
        cmd.AddOption(new Option(new[] { "--seed", "-s" }, "Seed for generating pseudo-random parameters. Also depends on the -n argument.") { Argument = new Argument<int>("seed", (new Random().Next())) });

        ParseResult parseResult = cmd.Parse(args);
        if (parseResult.Errors.Count > 0 || parseResult.HasOption("-h"))
        {
            foreach (ParseError error in parseResult.Errors)
            {
                Console.WriteLine(error);
            }
            WriteHelpText();
            config = null;
            return false;
        }

        config = new Configuration()
        {
            RunMode = parseResult.ValueForOption<RunMode>("-m"),
            MaxConnections = parseResult.ValueForOption<int>("-n"),
            CancellationProbability = Math.Max(0, Math.Min(1, parseResult.ValueForOption<double>("-p"))),
            ServerEndpoint = ParseEndpoint(parseResult.ValueForOption<string>("-e")),
            MaxExecutionTime = parseResult.ValueForOption<double?>("-t")?.Pipe(TimeSpan.FromMinutes),
            MaxBufferLength = parseResult.ValueForOption<int>("-b"),
            DisplayInterval = TimeSpan.FromSeconds(parseResult.ValueForOption<double>("-i")),
            RandomSeed = parseResult.ValueForOption<int>("-s"),
        };

        return true;

        void WriteHelpText()
        {
            Console.WriteLine();
            new HelpBuilder(new SystemConsole()).Write(cmd);
        }

        static IPEndPoint ParseEndpoint(string value)
        {
            try
            {
                return IPEndPoint.Parse(value);
            }
            catch (FormatException)
            {
                // support hostname:port endpoints
                Match match = Regex.Match(value, "^([^:]+):([0-9]+)$");
                if (match.Success)
                {
                    string hostname = match.Groups[1].Value;
                    int port = int.Parse(match.Groups[2].Value);
                    switch (hostname)
                    {
                        case "+":
                        case "*":
                            return new IPEndPoint(IPAddress.Any, port);
                        default:
                            IPAddress[] addresses = Dns.GetHostAddresses(hostname);
                            return new IPEndPoint(addresses[0], port);
                    }
                }

                throw;
            }
        }
    }
}

static class MiscHelpers
{
    // help transform `(foo != null) ? Bar(foo) : null` expressions into `foo?.Select(Bar)`
    public static S Pipe<T, S>(this T value, Func<T, S> mapper) => mapper(value);
    public static void Pipe<T>(this T value, Action<T> body) => body(value);
}
