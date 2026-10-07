using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using AnyPortProxy.Core;
using Spectre.Console;

namespace AnyPortProxy.Cli;

internal sealed class CliArgs
{
    private static readonly HashSet<string> Valued = new(StringComparer.OrdinalIgnoreCase)
    {
        "--name", "-n", "--http", "--https", "--to", "--lines", "--address", "--port", "--game",
    };

    private readonly Dictionary<string, string?> _flags = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Positional { get; } = new();

    public static CliArgs Parse(string[] args)
    {
        var a = new CliArgs();
        for (int i = 0; i < args.Length; i++)
        {
            var t = args[i];
            if (t.StartsWith('-') && !int.TryParse(t, out _))
            {
                int eq = t.IndexOf('=');
                if (eq > 0) a._flags[t[..eq]] = t[(eq + 1)..];
                else if (Valued.Contains(t) && i + 1 < args.Length) a._flags[t] = args[++i];
                else a._flags[t] = null;
            }
            else
            {
                a.Positional.Add(t);
            }
        }
        return a;
    }

    public bool Has(string flag) => _flags.ContainsKey(flag);
    public string? Get(string flag) => _flags.TryGetValue(flag, out var v) ? v : null;
    public string Pos(int i) => i < Positional.Count ? Positional[i] : "";
    public string Rest(int from) => string.Join(' ', Positional.Skip(from));
}

internal static class CliApp
{
    private static string[] _raw = [];

    private static readonly string[] Commands =
    [
        "status", "setup", "install", "uninstall", "start", "stop", "restart", "sites", "site", "domain",
        "ports", "port", "forward", "check", "logs", "gui", "run", "help", "version",
    ];

    public static async Task<int> RunAsync(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        _raw = args;
        var a = CliArgs.Parse(args);
        try
        {
            return await DispatchAsync(a);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Ui.Error(ex.Message);
            return 1;
        }
        finally
        {
            if (a.Has("--pause"))
            {
                AnsiConsole.MarkupLine("\n[grey]Press any key to close this window…[/]");
                Console.ReadKey(true);
            }
        }
    }

    /// <summary>Returns true if we're admin. Otherwise reopens this exact command in an elevated window.</summary>
    public static bool RequireAdmin()
    {
        // ANYPORTPROXY_DATA = developer test mode with a private settings folder; don't force elevation.
        if (Elevation.IsAdmin || Environment.GetEnvironmentVariable("ANYPORTPROXY_DATA") is { Length: > 0 }) return true;
        Ui.Warn("This needs Administrator rights. Asking Windows for permission — the result will show in a new window.");
        RelaunchElevated(_raw.Append("--pause"));
        return false;
    }

    public static void RelaunchElevated(IEnumerable<string> args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.Join(' ', args.Select(Quote)),
            });
        }
        catch (Win32Exception)
        {
            Ui.Error("Cancelled. Tip: right-click your terminal and choose \"Run as administrator\".");
        }
    }

    private static string Quote(string s) =>
        s.Length > 0 && !s.Any(c => char.IsWhiteSpace(c) || c == '"') ? s : "\"" + s.Replace("\"", "\\\"") + "\"";

    private static async Task<int> DispatchAsync(CliArgs a)
    {
        var cmd = a.Pos(0).ToLowerInvariant();
        switch (cmd)
        {
            case "" when Ui.Interactive && !a.Has("--help") && !a.Has("-h"):
                return await Interactive.RunAsync();
            case "" or "help" or "/?":
                Help();
                return 0;
            case "version" or "--version":
                AnsiConsole.WriteLine(AppPaths.Version);
                return 0;
            case "status":
                Status();
                return 0;
            case "tour" or "learn" or "tutorial":
                await Interactive.TourAsync();
                return 0;
            case "setup":
                if (!RequireAdmin()) return 1;
                await Interactive.SetupAsync();
                return 0;
            case "install":
                return Install();
            case "uninstall":
                return await UninstallAsync(a);
            case "start" or "stop" or "restart":
                return ServiceCommand(cmd);
            case "sites" or "websites":
                SitesList();
                return 0;
            case "site" or "website":
                return await SiteAsync(a);
            case "domain":
                return Domain(a);
            case "ports":
                PortsList();
                return 0;
            case "port":
                return await PortAsync(a);
            case "forward":
                return Forward(a);
            case "game" or "games":
                return await GameAsync(a);
            case "update" or "updates":
                return await UpdateAsync(a);
            case "portmap" or "portmaps" or "portrule" or "portrules":
                return PortMap(a);
            case "limits":
                return Limits(a);
            case "check" or "doctor" or "health":
                return await CheckAsync(a.Has("--fix"));
            case "logs" or "log":
                return Logs(a);
            case "gui" or "app":
                return OpenGui();
            default:
                Ui.Error($"I don't know the command \"{cmd}\".");
                var guess = Commands.FirstOrDefault(c => c.StartsWith(cmd[..Math.Min(2, cmd.Length)], StringComparison.Ordinal));
                if (guess is not null) Ui.Hint($"Did you mean: apx {guess}");
                Ui.Hint("Run \"apx help\" to see everything, or just \"apx\" for the menu.");
                return 1;
        }
    }

    private static void Help()
    {
        Ui.Banner();
        AnsiConsole.MarkupLine("[bold]Easiest:[/] just type [deepskyblue1]apx[/] for a menu that walks you through everything.\n");
        var t = new Table().Border(TableBorder.None).HideHeaders().AddColumns("cmd", "what");
        void Row(string c, string w) => t.AddRow($"[deepskyblue1]{Ui.E(c)}[/]", Ui.E(w));
        void Section(string s) => t.AddRow($"\n[bold]{Ui.E(s)}[/]", "");

        Section("Basics");
        Row("apx status", "Is it running? What's set up?");
        Row("apx tour", "Learn how AnyPortProxy works (2 minutes)");
        Row("apx setup", "Guided first-time setup");
        Row("apx check [--fix]", "Health check: find (and fix) problems");
        Row("apx start | stop | restart", "Control the background service");
        Row("apx install | uninstall [--purge]", "Install as a Windows service / remove it");
        Row("apx logs [-f] [-n 50]", "Show recent activity (-f = keep watching)");
        Row("apx gui", "Open the app");

        Section("Websites (by address, on ports 80/443)");
        Row("apx sites", "List websites");
        Row("apx site add <address> <computer> [--http 8080] [--https 8443]", "e.g. apx site add nas 192.168.1.20");
        Row("apx site add <address> this-pc --http 8080 --https off", "Website running on this PC");
        Row("apx site remove <address>", "Remove a website");
        Row("apx site default <computer>", "Where unknown addresses go");
        Row("apx domain <example.com>", "Your domain (so \"nas\" means nas.example.com)");

        Section("Ports (games and apps)");
        Row("apx ports", "Opened ports and forwarding settings");
        Row("apx port open <port|range|name> [--udp|--both] [--router] [--no-firewall]", "e.g. apx port open \"minecraft java\" --router");
        Row("apx port close <port> [--block]", "Close it (--block = also block from the internet)");
        Row("apx port check <port> [--udp] [--router] [--fix]", "Why can't people connect?");
        Row("apx port presets [search]", "Known games/apps and their ports");

        Section("All-ports forwarding");
        Row("apx forward", "Show settings");
        Row("apx forward on | off", "Turn forwarding of every port on/off");
        Row("apx forward allow \"1-49151\"", "Which ports are forwarded");
        Row("apx forward block | unblock <port...>", "Block/unblock specific ports");
        Row("apx forward lan on | off", "Also redirect devices on your home network");
        Row("apx forward to <computer>", "Send forwarded ports to another computer");
        Row("apx forward smart on | off", "Smart routing: ignore scanners, find ::1 apps, direct hand-off");
        Row("apx forward udp on | off", "Also forward UDP (games, voice chat, VPNs)");
        Row("apx limits <total> <per-address>", "Flood protection, e.g. apx limits 20000 300");

        Section("Send a port to another computer (TCP and/or UDP)");
        Row("apx portmap", "List port rules");
        Row("apx portmap add <port|range> <computer>[:port] [--udp|--both] [--name X]", "e.g. apx portmap add 51820 192.168.58.20 --udp --name WireGuard");
        Row("apx portmap remove <port> [--udp|--both]", "Remove a port rule");

        Section("Several game servers on one port, by address (Minecraft Java, web apps)");
        Row("apx games", "List game addresses");
        Row("apx game add <address> <computer>[:port] [--port 25565]", "e.g. apx game add mc2 192.168.58.30  (players type mc2.yourdomain.com)");
        Row("apx game remove <address> [--port 25565]", "Remove a game address");
        Row("apx port open \"minecraft java\" --address mc2 --to <computer>", "Same, from the port helper");

        Section("Updates");
        Row("apx update", "Check for a new version and install it");
        Row("apx update --check", "Only check");
        Row("apx update auto on | off", "Let the service install updates by itself (when nobody is connected)");
        AnsiConsole.Write(t);
        AnsiConsole.MarkupLine("\n[grey]Commands that change things ask Windows for Administrator permission automatically.[/]");
    }

    // ---------------------------------------------------------------- status

    public static void Status()
    {
        var state = ServiceManager.GetState();
        var st = ServiceStatus.Read();
        AppConfig? c = null;
        try { c = ConfigStore.Load(); } catch (Exception ex) { Ui.Error(ex.Message); }

        var (color, text) = state switch
        {
            ServiceState.Running when st is { IsFresh: true } && st.Problems().Any() => ("yellow", "Running, but something needs attention"),
            ServiceState.Running => ("green", "Running"),
            ServiceState.Starting => ("yellow", "Starting…"),
            ServiceState.Stopping => ("yellow", "Stopping…"),
            ServiceState.Stopped => ("red", "Stopped — nobody can reach your stuff right now"),
            _ => ("grey", "Not installed yet (run: apx setup)"),
        };

        var grid = new Grid().AddColumn(new GridColumn().NoWrap().PadRight(3)).AddColumn();
        grid.AddRow("[bold]Service[/]", $"[{color}]● {Ui.E(text)}[/]");
        if (state == ServiceState.Running && st is { IsFresh: true })
        {
            var up = DateTime.UtcNow - st.StartedUtc;
            grid.AddRow("[bold]Websites[/]", string.Join("  ", st.SniffPorts.Select(l => l.Listening ? $"[green]✔ {l.Port}[/]" : $"[red]✖ {l.Port}[/] [grey]{Ui.E(l.Error)}[/]")));
            grid.AddRow("[bold]All ports[/]", st.CatchAll.State switch
            {
                "Running" => $"[green]✔[/] {Ui.E(st.CatchAll.Message)}",
                "Error" => $"[red]✖ {Ui.E(st.CatchAll.Message)}[/]",
                _ => $"[grey]off[/]",
            });
            grid.AddRow("[bold]Connections[/]", $"{st.ActiveConnections} active · {st.TotalConnections} since start · up {(int)up.TotalHours}h {up.Minutes}m");
            grid.AddRow("[bold]Traffic[/]", $"{ServiceStatus.FormatRate(st.BytesPerSec)} · {st.ConnectionsPerSec:0.#} new conn/s · {st.PacketsPerSec:0} packets/s");
            if (st.UdpSessions > 0) grid.AddRow("[bold]UDP[/]", $"{st.UdpSessions} active UDP session(s)");
            if (st.Forwards.Count > 0) grid.AddRow("[bold]Port rules[/]", string.Join("  ", st.Forwards.Select(f => f.Listening ? $"[green]✔ {f.Protocol} {f.Port}→{Ui.E(f.Target)}[/]" : $"[red]✖ {f.Protocol} {f.Port}[/] [grey]{Ui.E(f.Error)}[/]")));
            var smart = new List<string>();
            if (st.DirectConnections > 0) smart.Add($"{st.DirectConnections} handed straight to apps");
            if (st.IgnoredProbes > 0) smart.Add($"{st.IgnoredProbes} scanner probes ignored");
            if (st.RejectedConnections > 0) smart.Add($"[yellow]{st.RejectedConnections} refused by flood limits[/]");
            if (smart.Count > 0) grid.AddRow("[bold]Smart[/]", string.Join(" · ", smart));
        }
        if (c is not null)
        {
            grid.AddRow("[bold]Domain[/]", Ui.E(c.Domain ?? "(not set — apx domain example.com)"));
            grid.AddRow("[bold]Set up[/]", $"{Websites.List(c.Proxy).Count} website(s) · {c.Ports.Count} opened port(s)");
        }
        grid.AddRow("[bold]Settings[/]", $"[grey]{Ui.E(AppPaths.ConfigFile)}[/]");
        AnsiConsole.Write(new Panel(grid).Header("[bold deepskyblue1] AnyPortProxy [/]").Border(BoxBorder.Rounded));

        if (st is { IsFresh: true })
        {
            foreach (var p in st.Problems()) Ui.Warn(p);
            foreach (var n in st.ConfigNotes) Ui.Info($"Setting ignored: {n}");
            foreach (var r in st.Repairs.TakeLast(3)) Ui.Ok($"Self-repair: {r}");
            if (st.Update is not null) Ui.Info($"Updates: {st.Update}");
        }
    }

    // ---------------------------------------------------------------- service

    private static int Install()
    {
        if (!RequireAdmin()) return 1;
        Installer.Install(msg => Ui.Info(msg));
        Ui.Ok("Installed! Open a NEW terminal to use the short command: apx");
        return 0;
    }

    private static async Task<int> UninstallAsync(CliArgs a)
    {
        if (!RequireAdmin()) return 1;
        bool purge = a.Has("--purge");
        if (!a.Has("--yes") && Ui.Interactive &&
            !AnsiConsole.Confirm($"Remove AnyPortProxy{(purge ? " AND delete your settings" : "")}?", false))
            return 0;
        await Installer.UninstallAsync(purge, msg => Ui.Info(msg));
        return 0;
    }

    private static int ServiceCommand(string cmd)
    {
        if (ServiceManager.GetState() == ServiceState.NotInstalled)
        {
            Ui.Error("AnyPortProxy isn't installed yet. Run: apx setup");
            return 1;
        }
        if (!RequireAdmin()) return 1;
        switch (cmd)
        {
            case "start": ServiceManager.Start(); Ui.Ok("Started."); break;
            case "stop": ServiceManager.Stop(); Ui.Ok("Stopped. Nobody can reach your stuff until you start it again."); break;
            default: ServiceManager.Restart(); Ui.Ok("Restarted."); break;
        }
        return 0;
    }

    private static int OpenGui()
    {
        var gui = Path.Combine(AppPaths.AppDir, AppPaths.GuiExe);
        if (!File.Exists(gui)) gui = Path.Combine(AppPaths.InstallDir, AppPaths.GuiExe);
        if (!File.Exists(gui))
        {
            Ui.Error("Couldn't find the app (AnyPortProxyGui.exe).");
            return 1;
        }
        Process.Start(new ProcessStartInfo(gui) { UseShellExecute = true });
        return 0;
    }

    // ---------------------------------------------------------------- websites

    public static void SitesList()
    {
        var c = ConfigStore.Load();
        var sites = Websites.List(c.Proxy);
        if (sites.Count == 0)
        {
            Ui.Info("No websites yet. Add one: apx site add nas.example.com 192.168.1.20");
        }
        else
        {
            var t = new Table().Border(TableBorder.Rounded).AddColumns("Address people type", "http (80) goes to", "https (443) goes to");
            foreach (var s in sites)
            {
                var extra = s.Other.Count > 0 ? $"\n[grey]+ {Ui.E(string.Join(", ", s.Other.Select(o => $"port {o.Port} → {o.Target}")))}[/]" : "";
                t.AddRow(Ui.E(s.Host) + extra, Ui.E(s.HttpText), Ui.E(s.HttpsText));
            }
            AnsiConsole.Write(t);
        }
        Ui.Hint($"Unknown addresses go to: {c.Proxy.DefaultTarget}");
    }

    private static async Task<int> SiteAsync(CliArgs a)
    {
        var sub = a.Pos(1).ToLowerInvariant();
        if (sub is "" or "list")
        {
            SitesList();
            return 0;
        }
        if (!RequireAdmin()) return 1;
        var c = ConfigStore.Load();

        switch (sub)
        {
            case "add" or "set" or "edit":
            {
                if (a.Pos(2) == "" || a.Pos(3) == "")
                {
                    if (Ui.Interactive)
                    {
                        await Interactive.AddWebsiteAsync(c);
                        return 0;
                    }
                    Ui.Error("Usage: apx site add <address> <computer> [--http 80] [--https 443]");
                    return 1;
                }
                var w = new Website
                {
                    Host = Interactive.ExpandHost(c, a.Pos(2)),
                    Computer = Interactive.ExpandComputer(a.Pos(3)),
                };
                bool local = NetInfo.IsThisPc(w.Computer);
                w.HttpPort = ParsePortFlag(a.Get("--http"), local ? null : 80);
                w.HttpsPort = ParsePortFlag(a.Get("--https"), local ? null : 443);
                if (Websites.ValidateHost(w.Host) is { } he) { Ui.Error(he); return 1; }
                if (Websites.ValidateComputer(w.Computer) is { } ce) { Ui.Error(ce); return 1; }
                if (w.HttpPort is null && w.HttpsPort is null)
                {
                    Ui.Error(local
                        ? "The website is on this PC, so tell me which ports it runs on, e.g. --http 8080 --https 8443"
                        : "Both http and https are off — nothing to route.");
                    return 1;
                }
                if (local && (w.HttpPort is 80 || w.HttpsPort is 443))
                {
                    Ui.Error("AnyPortProxy itself uses ports 80/443 on this PC. Run your website on other ports (like 8080 / 8443) and use --http / --https.");
                    return 1;
                }
                await Interactive.SaveWebsiteAsync(c, w, null);
                return 0;
            }
            case "remove" or "rm" or "delete":
            {
                var host = Interactive.ExpandHost(c, a.Pos(2));
                if (!Websites.Remove(c.Proxy, host))
                {
                    Ui.Error($"There's no website called {host}. See: apx sites");
                    return 1;
                }
                ConfigStore.Save(c);
                Ui.Ok($"Removed {host}.");
                return 0;
            }
            case "default":
            {
                var target = Interactive.ExpandComputer(a.Pos(2));
                if (!TargetParser.TryParse(target, out _, out _)) { Ui.Error("Usage: apx site default <computer>"); return 1; }
                c.Proxy.DefaultTarget = target;
                ConfigStore.Save(c);
                Ui.Ok($"Unknown addresses now go to {target}.");
                return 0;
            }
            default:
                Ui.Error("Usage: apx site add|remove|default ...  (see apx help)");
                return 1;
        }
    }

    private static int? ParsePortFlag(string? value, int? fallback)
    {
        if (value is null) return fallback;
        if (value.Equals("off", StringComparison.OrdinalIgnoreCase) || value.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        if (int.TryParse(value, out var p) && p is >= 1 and <= 65535) return p;
        throw new FormatException($"\"{value}\" isn't a port number (or \"off\").");
    }

    private static int Domain(CliArgs a)
    {
        var c = ConfigStore.Load();
        if (a.Pos(1) == "")
        {
            AnsiConsole.WriteLine(c.Domain ?? "(not set)");
            return 0;
        }
        if (!RequireAdmin()) return 1;
        var d = a.Pos(1).Trim().TrimEnd('.').ToLowerInvariant();
        if (d.Contains("://")) d = new Uri(d).Host;
        c.Domain = d;
        ConfigStore.Save(c);
        Ui.Ok($"Domain set to {d}. Now \"nas\" means nas.{d}.");
        return 0;
    }

    // ---------------------------------------------------------------- ports

    public static void PortsList()
    {
        var c = ConfigStore.Load();
        var ca = c.Proxy.CatchAll;
        if (ca.Enabled)
            Ui.Ok($"All-ports forwarding is ON: TCP ports {ca.AllowedPorts} reach {c.CatchAllHost} from the internet (blocked: {string.Join(", ", ca.BlockedPorts)}).");
        else
            Ui.Info("All-ports forwarding is OFF. Turn on with: apx forward on");

        if (c.Ports.Count == 0)
        {
            Ui.Info("No ports opened with the helper yet. Try: apx port open minecraft");
            return;
        }
        var listeners = NetInfo.GetListeners();
        var t = new Table().Border(TableBorder.Rounded).AddColumns("Name", "Port", "Type", "Firewall", "Router", "Running now");
        foreach (var r in c.Ports)
        {
            var running = listeners.Where(l => l.Port >= r.Port && l.Port <= r.Last && r.Protocols().Contains(l.Protocol)).Select(l => l.Process).Distinct().ToList();
            t.AddRow(Ui.E(r.Name), r.Range, r.ProtocolText,
                r.Firewall ? "[green]✔[/]" : "[grey]–[/]",
                r.Router ? "[green]✔[/]" : "[grey]–[/]",
                running.Count > 0 ? $"[green]{Ui.E(string.Join(", ", running))}[/]" : "[yellow]nothing yet[/]");
        }
        AnsiConsole.Write(t);
    }

    private static PortProtocol? ProtocolFlag(CliArgs a) =>
        a.Has("--both") ? PortProtocol.Both : a.Has("--udp") ? PortProtocol.Udp : a.Has("--tcp") ? PortProtocol.Tcp : null;

    private static async Task<int> PortAsync(CliArgs a)
    {
        var sub = a.Pos(1).ToLowerInvariant();
        switch (sub)
        {
            case "" or "list":
                PortsList();
                return 0;
            case "presets" or "games" or "apps":
            {
                var list = Presets.Search(a.Rest(2));
                var t = new Table().Border(TableBorder.Rounded).AddColumns("Name", "Port", "Type", "Notes");
                foreach (var p in list)
                    t.AddRow(Ui.E(p.Name), p.Range, p.ProtocolText, (p.Risk == Risk.High ? "[red]risky[/] " : p.Risk == Risk.Medium ? "[yellow]careful[/] " : "") + Ui.E(p.Description));
                AnsiConsole.Write(t);
                Ui.Hint("Open one with: apx port open \"<name or port>\"");
                return 0;
            }
            case "open" or "add":
            {
                if (!RequireAdmin()) return 1;
                var c = ConfigStore.Load();
                var what = a.Rest(2);
                if (what == "")
                {
                    if (Ui.Interactive) { await Interactive.OpenPortAsync(c); return 0; }
                    Ui.Error("Usage: apx port open <port|range|name>   e.g. apx port open 25565");
                    return 1;
                }
                var req = Interactive.ResolvePortRequest(what, ProtocolFlag(a), a.Get("--name"));
                if (req is null) return 1;
                if (a.Get("--address") is { } addr)
                    return await AddGameAddressAsync(c, req.Name, addr, req.Port, a.Get("--to") ?? "", a.Has("--router"), Presets.ForPort(req.Port));
                req.Firewall = !a.Has("--no-firewall");
                req.Router = a.Has("--router");
                return await Interactive.RunOpenAsync(c, req, a.Has("--yes")) ? 0 : 1;
            }
            case "close" or "remove" or "rm":
            {
                if (!RequireAdmin()) return 1;
                var c = ConfigStore.Load();
                if (!int.TryParse(a.Pos(2).Split('-')[0], out var port))
                {
                    if (Ui.Interactive) { await Interactive.ClosePortAsync(c); return 0; }
                    Ui.Error("Usage: apx port close <port> [--block]");
                    return 1;
                }
                var rule = c.Ports.FirstOrDefault(r => port >= r.Port && port <= r.Last);
                if (rule is null)
                {
                    if (a.Has("--block"))
                    {
                        if (!c.Proxy.CatchAll.BlockedPorts.Contains(port)) c.Proxy.CatchAll.BlockedPorts.Add(port);
                        c.Proxy.CatchAll.BlockedPorts.Sort();
                        ConfigStore.Save(c);
                        Ui.Ok($"Port {port} is now blocked from the internet.");
                        return 0;
                    }
                    Ui.Warn($"Port {port} wasn't opened with the helper.");
                    if (c.Proxy.CatchAll.Enabled && PortRanges.ContainsAll(c.Proxy.CatchAll.AllowedPorts, port, port) && !c.Proxy.CatchAll.BlockedPorts.Contains(port))
                        Ui.Hint($"It's still reachable through all-ports forwarding. Block it with: apx port close {port} --block");
                    return 1;
                }
                var results = await Ui.Busy("Closing…", p => PortHelper.CloseAsync(c, rule, a.Has("--block"), p));
                Ui.Results(results);
                return 0;
            }
            case "check" or "test":
            {
                if (!int.TryParse(a.Pos(2), out var port))
                {
                    Ui.Error("Usage: apx port check <port> [--udp] [--router]");
                    return 1;
                }
                var c = ConfigStore.Load();
                var results = await Ui.Busy("Checking…", p => PortHelper.CheckAsync(c, port, ProtocolFlag(a) ?? Presets.ForPort(port)?.Protocol ?? PortProtocol.Tcp, a.Has("--router"), p));
                Ui.Results(results);
                await Ui.OfferFixesAsync(results, a.Has("--fix"), $"apx port check {port} --fix");
                return 0;
            }
            default:
                Ui.Error("Usage: apx port open|close|check|presets ...  (see apx help)");
                return 1;
        }
    }

    private static int Forward(CliArgs a)
    {
        var c = ConfigStore.Load();
        var ca = c.Proxy.CatchAll;
        var sub = a.Pos(1).ToLowerInvariant();
        if (sub == "")
        {
            Ui.Info($"Forwarding: {(ca.Enabled ? "ON" : "OFF")}");
            Ui.Info($"Forwarded ports: {ca.AllowedPorts}");
            Ui.Info($"Blocked ports: {string.Join(", ", ca.BlockedPorts)}");
            Ui.Info($"Goes to: {c.CatchAllHost}");
            Ui.Info($"Home-network devices redirected too: {(ca.InterceptLan ? "yes" : "no")}");
            Ui.Info($"Smart routing: {(ca.SmartRouting ? "on" : "off")}");
            Ui.Info($"UDP forwarding: {(ca.Udp ? "on" : "off")}");
            return 0;
        }
        if (!RequireAdmin()) return 1;

        IEnumerable<int> Ports() => a.Positional.Skip(2).SelectMany(s => s.Split(',', StringSplitOptions.RemoveEmptyEntries))
            .Select(s => int.TryParse(s.Trim(), out var p) && p is >= 1 and <= 65535 ? p : throw new FormatException($"\"{s}\" isn't a port"));

        switch (sub)
        {
            case "on": ca.Enabled = true; Ui.Ok("All-ports forwarding turned on."); break;
            case "off": ca.Enabled = false; Ui.Ok("All-ports forwarding turned off."); break;
            case "allow":
                PortRanges.Parse(a.Rest(2));
                ca.AllowedPorts = a.Rest(2);
                Ui.Ok($"Forwarded ports: {ca.AllowedPorts}");
                break;
            case "block":
                foreach (var p in Ports())
                {
                    if (!ca.BlockedPorts.Contains(p)) ca.BlockedPorts.Add(p);
                    Ui.Ok($"Blocked {p}.");
                }
                ca.BlockedPorts.Sort();
                break;
            case "unblock":
                foreach (var p in Ports())
                {
                    if (Presets.Warning(p) is { Risk: Risk.High } w)
                    {
                        Ui.Error($"Port {p}: {w.Why}");
                        bool sure = a.Has("--yes") || (Ui.Interactive && AnsiConsole.Confirm($"Unblock {p} anyway?", false));
                        if (!sure)
                        {
                            Ui.Hint($"Kept {p} blocked. (Add --yes if you really mean it.)");
                            continue;
                        }
                    }
                    ca.BlockedPorts.Remove(p);
                    Ui.Ok($"Unblocked {p}.");
                }
                break;
            case "udp":
                ca.Udp = a.Pos(2).Equals("on", StringComparison.OrdinalIgnoreCase);
                Ui.Ok(ca.Udp ? "UDP forwarding on." : "UDP forwarding off (TCP only).");
                break;
            case "smart":
                ca.SmartRouting = a.Pos(2).Equals("on", StringComparison.OrdinalIgnoreCase);
                Ui.Ok(ca.SmartRouting ? "Smart routing on." : "Smart routing off — every allowed port is proxied.");
                break;
            case "lan":
                ca.InterceptLan = a.Pos(2).Equals("on", StringComparison.OrdinalIgnoreCase);
                Ui.Ok(ca.InterceptLan ? "Home-network devices are now redirected too." : "Only internet visitors are redirected.");
                break;
            case "to":
                var target = Interactive.ExpandComputer(a.Pos(2));
                if (Websites.ValidateComputer(target) is { } err) { Ui.Error(err); return 1; }
                ca.Target = target;
                Ui.Ok($"Forwarded ports now go to {target}.");
                break;
            default:
                Ui.Error("Usage: apx forward on|off|allow|block|unblock|lan|to ...");
                return 1;
        }
        ConfigStore.Save(c);
        Ui.Hint("Applied — the proxy picks this up automatically.");
        return 0;
    }

    public static void PortMapList(AppConfig c)
    {
        if (c.Proxy.Forwards.Count == 0)
        {
            Ui.Info("No port rules yet. Example: apx portmap add 51820 192.168.58.20 --udp --name WireGuard");
            return;
        }
        var t = new Table().Border(TableBorder.Rounded).AddColumns("Name", "Port", "Type", "Goes to");
        foreach (var f in c.Proxy.Forwards) t.AddRow(Ui.E(f.Name), f.Range, f.ProtocolText, Ui.E(f.Target));
        AnsiConsole.Write(t);
    }

    private static int PortMap(CliArgs a)
    {
        var c = ConfigStore.Load();
        var sub = a.Pos(1).ToLowerInvariant();
        if (sub is "" or "list")
        {
            PortMapList(c);
            return 0;
        }
        if (!RequireAdmin()) return 1;
        var proto = ProtocolFlag(a);
        switch (sub)
        {
            case "add":
            {
                if (!PortRanges.TryParseOne(a.Pos(2), out var lo, out var hi) || a.Pos(3) == "")
                {
                    Ui.Error("Usage: apx portmap add <port|range> <computer>[:port] [--udp|--both] [--name X]");
                    return 1;
                }
                var preset = Presets.ForPort(lo);
                var rule = new PortForward
                {
                    Port = lo,
                    EndPort = hi > lo ? hi : null,
                    Protocol = proto ?? (preset is not null && preset.Port == lo ? preset.Protocol : PortProtocol.Tcp),
                    Target = Interactive.ExpandComputer(a.Pos(3)),
                    Name = a.Get("--name") ?? (preset is not null && preset.Port == lo ? preset.Name : ""),
                };
                if (proto is null && preset is not null && preset.Port == lo) Ui.Info($"Port {lo} is usually {preset.Name} ({preset.ProtocolText}).");
                if (PortForwards.Validate(c.Proxy, rule) is { } err)
                {
                    Ui.Error(err);
                    return 1;
                }
                if (Presets.WarningForRange(lo, hi) is { Risk: Risk.High } w && !a.Has("--yes"))
                {
                    Ui.Error($"Warning: {w.Why}");
                    if (!Ui.Interactive || !AnsiConsole.Confirm("Send it anyway?", false)) return 1;
                }
                c.Proxy.Forwards.Add(rule);
                ConfigStore.Save(c);
                Ui.Ok($"{rule.ProtocolText} {rule.Range} on this PC now goes to {rule.Target}. Applied immediately.");
                Ui.Hint("Make sure your router forwards this port (TCP and/or UDP) to this PC.");
                return 0;
            }
            case "remove" or "rm" or "delete":
            {
                if (!int.TryParse(a.Pos(2).Split('-')[0], out var port))
                {
                    Ui.Error("Usage: apx portmap remove <port> [--udp|--both]");
                    return 1;
                }
                int removed = c.Proxy.Forwards.RemoveAll(f => port >= f.Port && port <= f.Last && (proto is null || f.Protocol == proto));
                if (removed == 0)
                {
                    Ui.Error($"No port rule covers port {port}. See: apx portmap");
                    return 1;
                }
                ConfigStore.Save(c);
                Ui.Ok($"Removed {removed} port rule(s).");
                return 0;
            }
            default:
                Ui.Error("Usage: apx portmap [add|remove] ...  (see apx help)");
                return 1;
        }
    }

    // ---------------------------------------------------------------- game addresses

    public static void GamesList(AppConfig c)
    {
        var games = GameAddresses.List(c.Proxy);
        if (games.Count == 0)
        {
            Ui.Info("No game addresses yet. Example: apx game add mc2 192.168.58.30   (Minecraft Java, port 25565)");
            return;
        }
        var t = new Table().Border(TableBorder.Rounded).AddColumns("Players type", "Port", "Goes to");
        foreach (var g in games.OrderBy(g => g.Port).ThenBy(g => g.IsFallback))
            t.AddRow(g.IsFallback ? "[grey](any other address / IP)[/]" : Ui.E(GameAddresses.ConnectAddress(g.Host, g.Port)), g.Port.ToString(), Ui.E(g.Target));
        AnsiConsole.Write(t);
    }

    private static async Task<int> GameAsync(CliArgs a)
    {
        var c = ConfigStore.Load();
        var sub = a.Pos(1).ToLowerInvariant();
        if (sub is "" or "list")
        {
            GamesList(c);
            return 0;
        }
        if (!RequireAdmin()) return 1;
        int port = 25565;
        if (a.Get("--port") is { } ps && (!int.TryParse(ps, out port) || port is < 1 or > 65535))
        {
            Ui.Error("--port must be a number from 1 to 65535.");
            return 1;
        }
        var game = a.Get("--game")?.ToLowerInvariant();
        if (game is not null && game is not ("minecraft" or "mc" or "web" or "http" or "https"))
        {
            var preset = Presets.Search(game).FirstOrDefault();
            Ui.Error(GameAddresses.WhyNot(preset?.Name ?? game, preset?.Port ?? port));
            return 1;
        }
        switch (sub)
        {
            case "add":
                if (a.Pos(2) == "" || a.Pos(3) == "")
                {
                    Ui.Error("Usage: apx game add <address> <computer>[:port] [--port 25565]   e.g. apx game add mc2 192.168.58.30");
                    return 1;
                }
                return await AddGameAddressAsync(c, "", a.Pos(2), port, a.Pos(3), a.Has("--router"), Presets.ForPort(port));
            case "remove" or "rm" or "delete":
            {
                var host = Websites.Expand(c.Domain, a.Pos(2));
                if (!GameAddresses.Remove(c.Proxy, host, port))
                {
                    Ui.Error($"No game address {host} on port {port}. See: apx games");
                    return 1;
                }
                ConfigStore.Save(c);
                Ui.Ok($"Removed {host}." + (c.Proxy.SniffPorts.Contains(port) ? "" : $" Port {port} is back to normal forwarding."));
                return 0;
            }
            default:
                Ui.Error("Usage: apx game add|remove ...  (see apx help)");
                return 1;
        }
    }

    public static async Task<int> AddGameAddressAsync(AppConfig c, string name, string address, int port, string computer, bool router, PortPreset? preset)
    {
        if (preset is { ByAddress: false } && preset.Port == port)
        {
            Ui.Error(GameAddresses.WhyNot(preset.Name, port));
            return 1;
        }
        if (computer.Length == 0)
        {
            Ui.Error("Which computer runs this server? Add --to <computer>, e.g. --to 192.168.58.30 or --to this-pc:25566");
            return 1;
        }
        var host = Websites.Expand(c.Domain, address);
        var target = Interactive.ExpandComputer(computer);
        // "this-pc" without a port: suggest the next port, since AnyPortProxy now answers on this one.
        if (NetInfo.IsThisPc(target) && TargetParser.TryParse(target, out _, out var tp) && tp is null)
        {
            Ui.Error($"A server on this PC must use another port, because AnyPortProxy answers on {port}. Try: this-pc:{port + 1} (and set the server to port {port + 1}).");
            return 1;
        }
        var results = await Ui.Busy("Setting it up…", p => PortHelper.AddGameAddressAsync(c, name.Length > 0 ? name : host, host, port, target, router, p));
        Ui.Results(results);
        return results.Any(r => r.Status == CheckStatus.Fail) ? 1 : 0;
    }

    // ---------------------------------------------------------------- updates

    private static async Task<int> UpdateAsync(CliArgs a)
    {
        var c = ConfigStore.Load();
        if (a.Pos(1).Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            if (a.Pos(2) == "")
            {
                Ui.Info($"Automatic updates: {(c.Updates.AutoInstall ? "on" : "off")}");
                return 0;
            }
            if (!RequireAdmin()) return 1;
            c.Updates.AutoInstall = a.Pos(2).Equals("on", StringComparison.OrdinalIgnoreCase);
            ConfigStore.Save(c);
            Ui.Ok(c.Updates.AutoInstall ? "Updates will install automatically when nobody is connected." : "Automatic updates off. Run apx update to update.");
            return 0;
        }

        var repo = Updater.Repo(c);
        if (repo is null)
        {
            Ui.Warn("This copy wasn't built with an update source (no GitHub repository). Rebuild with build.ps1 after adding your GitHub remote.");
            return 1;
        }
        Ui.Info($"You have version {AppPaths.Version}. Checking github.com/{repo}…");
        UpdateInfo? update;
        try
        {
            update = await Updater.CheckAsync(repo);
        }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException or TaskCanceledException)
        {
            Ui.Error(ex.Message);
            return 1;
        }
        if (update is null)
        {
            Ui.Ok("You're up to date.");
            return 0;
        }
        Ui.Ok($"Version {update.Version} is available.");
        if (!string.IsNullOrWhiteSpace(update.Notes))
            AnsiConsole.Write(new Panel(Ui.E(update.Notes.Length > 1200 ? update.Notes[..1200] + "…" : update.Notes)).Header("[bold] What's new [/]").Border(BoxBorder.Rounded));
        if (a.Has("--check")) return 0;
        if (!a.Has("--yes") && Ui.Interactive && !AnsiConsole.Confirm("Install it now? (connections drop for a few seconds; settings are kept)", true)) return 0;
        if (!RequireAdmin()) return 1;

        string path;
        try
        {
            path = await AnsiConsole.Progress().StartAsync(async ctx =>
            {
                var task = ctx.AddTask($"Downloading {Ui.E(update.AssetName)}", maxValue: 1.0);
                return await Updater.DownloadAsync(update, new Progress<double>(v => task.Value = v));
            });
        }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException or IOException or TaskCanceledException)
        {
            Ui.Error(ex.Message);
            return 1;
        }
        Ui.Ok("Downloaded and verified. Installing (this window can close)…");
        Updater.RunInstaller(path, reopenApp: false);
        return 0;
    }

    private static int Limits(CliArgs a)
    {
        var c = ConfigStore.Load();
        var l = c.Proxy.Limits;
        if (a.Pos(1) == "")
        {
            Ui.Info($"Max open connections: {l.MaxConnections}");
            Ui.Info($"Max open connections per internet address: {l.MaxConnectionsPerIp} (home-network addresses are exempt)");
            return 0;
        }
        if (!int.TryParse(a.Pos(1), out var total) || (a.Pos(2) != "" && !int.TryParse(a.Pos(2), out _)))
        {
            Ui.Error("Usage: apx limits <total> [per-address]   e.g. apx limits 20000 300");
            return 1;
        }
        if (!RequireAdmin()) return 1;
        l.MaxConnections = total;
        if (int.TryParse(a.Pos(2), out var perIp)) l.MaxConnectionsPerIp = perIp;
        ConfigSanitizer.Sanitize(c.Proxy); // clamp to safe ranges
        ConfigStore.Save(c);
        Ui.Ok($"Limits: {l.MaxConnections} total, {l.MaxConnectionsPerIp} per internet address. Applied immediately.");
        return 0;
    }

    // ---------------------------------------------------------------- health / logs

    public static async Task<int> CheckAsync(bool fix)
    {
        if (fix && !RequireAdmin()) return 1;
        var c = ConfigStore.Load();
        var results = new List<CheckResult>();
        await AnsiConsole.Status().Spinner(Spinner.Known.Dots).StartAsync("Running health check…", async ctx =>
        {
            await Diagnostics.RunAsync(c, r =>
            {
                results.Add(r);
                Ui.Result(r);
            }, s => ctx.Status(Ui.E(s)));
        });

        int fails = results.Count(r => r.Status == CheckStatus.Fail), warns = results.Count(r => r.Status == CheckStatus.Warn);
        AnsiConsole.WriteLine();
        if (fails == 0 && warns == 0) Ui.Ok("Everything looks good!");
        else AnsiConsole.MarkupLine($"[bold]{fails} problem(s), {warns} warning(s).[/]");
        await Ui.OfferFixesAsync(results, fix);
        return fails > 0 ? 2 : 0;
    }

    private static int Logs(CliArgs a)
    {
        int n = int.TryParse(a.Get("-n") ?? a.Get("--lines"), out var x) ? x : 40;
        foreach (var line in LogReader.Tail(n)) PrintLog(line);
        if (!a.Has("-f") && !a.Has("--follow")) return 0;

        AnsiConsole.MarkupLine("[grey]Watching for new activity… (press Q or Ctrl+C to stop)[/]");
        var follower = new LogReader.Follower();
        while (true)
        {
            foreach (var line in follower.ReadNew()) PrintLog(line);
            if (!Console.IsInputRedirected && Console.KeyAvailable && Console.ReadKey(true).Key is ConsoleKey.Q or ConsoleKey.Escape) return 0;
            Thread.Sleep(500);
        }
    }

    public static void PrintLog(string line)
    {
        var color = line.Contains(" FAIL ") || line.Contains(" CRIT ") ? "red" : line.Contains(" WARN ") ? "yellow" : line.Contains(" DBUG ") ? "grey" : "default";
        AnsiConsole.MarkupLine($"[{color}]{Ui.E(line)}[/]");
    }
}
