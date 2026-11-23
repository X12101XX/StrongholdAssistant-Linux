using System.Net.Sockets;
using System.Reflection;
using System.Text;

namespace StrongholdAssistant.Host;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = ParseArgs(args, out bool showHelp, out bool showVersion);
        if (showHelp)
        {
            PrintHelp();
            return 0;
        }
        if (showVersion)
        {
            var ver = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.3.8-linux";
            Console.WriteLine($"StrongholdAssistant-Linux {ver}");
            return 0;
        }

        // XDG data directory on Linux (~/.local/share/StrongholdAssistant)
        var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(xdgData))
            xdgData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        var root = Environment.GetEnvironmentVariable(AssistantProfile.TestRootVariable)
            ?? Path.Combine(xdgData, "StrongholdAssistant");
        AssistantProfile.Current = new AssistantProfile(root, null);
        if (!string.IsNullOrEmpty(options.Profile))
            AssistantProfile.Current = AssistantProfile.Current.Named(options.Profile);

        if (!string.IsNullOrEmpty(options.TriggerAction))
            return SendTrigger(options.TriggerAction, options.Profile);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

        using var runtime = new StrongholdRuntime(options);
        await runtime.RunAsync(cts.Token);
        return 0;
    }

    private static int SendTrigger(string action, string? profile)
    {
        var path = StrongholdRuntime.GetTriggerSocketPath(profile);
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"StrongholdAssistant is not running (socket not found: {path}).");
            return 1;
        }
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(path));
            var bytes = Encoding.UTF8.GetBytes(action + "\n");
            socket.Send(bytes);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to send trigger '{action}': {ex.Message}");
            return 1;
        }
    }

    private static LinuxLaunchOptions ParseArgs(string[] args, out bool help, out bool version)
    {
        help = false;
        version = false;
        bool headless = false, overlay = true, connectGame = true, devTools = false;
        int port = 0;
        string? profile = null, trigger = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h":
                case "--help":
                    help = true;
                    break;
                case "-v":
                case "--version":
                    version = true;
                    break;
                case "--headless":
                    headless = true;
                    break;
                case "--no-overlay":
                    overlay = false;
                    break;
                case "--no-game":
                    connectGame = false;
                    break;
                case "--devtools":
                    devTools = true;
                    break;
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p):
                    port = p;
                    i++;
                    break;
                case "--profile" when i + 1 < args.Length:
                    profile = args[++i];
                    break;
                case "--trigger" when i + 1 < args.Length:
                    trigger = args[++i];
                    break;
            }
        }
        return new LinuxLaunchOptions(headless, overlay, connectGame, devTools, port, profile, trigger);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            StrongholdAssistant-Linux (Wayland / niri / Lutris+Proton)
            Usage: StrongholdAssistant [options]

            Options:
              --headless              Serve UI over localhost HTTP/WebSocket without opening GTK WebView
              --port <PORT>           Bind UI bridge HTTP/WS server to a fixed localhost port
              --no-overlay            Disable gtk4-layer-shell transparent game overlay
              --no-game               Disable game window tracking and OCR scanning
              --profile <NAME>        Launch with a named profile
              --trigger <ACTION>      Send hotkey trigger to running instance (toggle|rescan|route|field|stop)
              --devtools              Enable WebKitGTK developer inspector
              -v, --version           Show version
              -h, --help              Show this help
            """);
    }
}
