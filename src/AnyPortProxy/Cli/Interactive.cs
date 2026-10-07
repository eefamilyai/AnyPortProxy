using AnyPortProxy.Core;
using Spectre.Console;

namespace AnyPortProxy.Cli;

/// <summary>The menu-driven terminal UI and the guided flows shared with the plain commands.</summary>
internal static class Interactive
{
    private const string ThisPc = "127.0.0.1";

    public static async Task<int> RunAsync()
    {
        if (!Elevation.IsAdmin)
        {
            Ui.Banner();
            Ui.Warn("You're not running as Administrator, so you can look around but not change anything.");
            if (AnsiConsole.Confirm("Reopen as Administrator now?", true))
            {
                CliApp.RelaunchElevated([]);
                return 0;
            }
        }
        else if (ServiceManager.GetState() == ServiceState.NotInstalled)
        {
            Ui.Banner();
            if (AnsiConsole.Confirm("Welcome! AnyPortProxy isn't installed yet. Start the guided setup?", true))
                await SetupAsync();
        }
        else if (!ConfigStore.Load().Onboarded)
        {
            Ui.Banner();
            if (AnsiConsole.Confirm("New here? Take the 2-minute tour of how AnyPortProxy works?", true))
                await TourAsync();
        }

        while (true)
        {
            AnsiConsole.Clear();
            Ui.Banner();
            CliApp.Status();
            AnsiConsole.WriteLine();

            var state = ServiceManager.GetState();
            var items = new List<(string Label, Func<Task<bool>> Run)>
            {
                ("📖  Learn how AnyPortProxy works (2-minute tour)", async () => { await TourAsync(); return false; }),
                ("🌐  Websites — choose which computer each address goes to", async () => { await WebsitesMenuAsync(); return false; }),
                ("🎮  Open a port for a game or app", async () => { await OpenPortAsync(ConfigStore.Load()); return true; }),
                ("🔍  Check a port (why can't people connect?)", async () => { await CheckPortAsync(); return true; }),
                ("🔒  Close a port", async () => { await ClosePortAsync(ConfigStore.Load()); return true; }),
                ("🔀  All-ports forwarding settings", async () => { await ForwardMenuAsync(); return false; }),
                ("📡  Send a port to another computer (TCP / UDP)", () => { PortMapMenu(); return Task.FromResult(false); }),
                ("🩺  Health check — find and fix problems", async () => { await CliApp.CheckAsync(false); return true; }),
                ("📜  Watch live activity", () => { WatchLogs(); return Task.FromResult(false); }),
            };
            switch (state)
            {
                case ServiceState.NotInstalled:
                    items.Insert(0, ("⭐  Install AnyPortProxy (guided setup)", async () => { await SetupAsync(); return true; }));
                    break;
                case ServiceState.Stopped:
                    items.Insert(0, ("▶   Start AnyPortProxy", () => Do(ServiceManager.Start, "Started.")));
                    break;
                case ServiceState.Running:
                    items.Add(("🔄  Restart AnyPortProxy", () => Do(ServiceManager.Restart, "Restarted.")));
                    items.Add(("⏹   Stop AnyPortProxy", () => Do(ServiceManager.Stop, "Stopped.")));
                    break;
            }
            items.Add(("🏷   Set my domain name", () => { SetDomain(); return Task.FromResult(true); }));
            items.Add(("🖥   Open the app (window)", () => { CliApp.RunAsync(["gui"]).Wait(); return Task.FromResult(false); }));
            if (state != ServiceState.NotInstalled)
            {
                items.Add(("📦  Update program files (install again)", async () => { await Task.Run(() => Installer.Install(m => Ui.Info(m))); return true; }));
                items.Add(("🗑   Uninstall", async () => { await UninstallAsync(); return true; }));
            }
            items.Add(("❌  Exit", () => Task.FromResult(false)));

            var choice = AnsiConsole.Prompt(new SelectionPrompt<(string Label, Func<Task<bool>> Run)>()
                .Title("[bold]What would you like to do?[/] [grey](arrow keys + enter)[/]")
                .PageSize(20)
                .UseConverter(i => Ui.E(i.Label))
                .AddChoices(items));
            if (choice.Label.Contains("Exit")) return 0;

            AnsiConsole.WriteLine();
            try
            {
                if (await choice.Run()) Ui.PressAnyKey();
            }
            catch (Exception ex)
            {
                Ui.Error(ex.Message);
                Ui.PressAnyKey();
            }
        }
    }

    private static async Task<bool> Do(Action action, string done)
    {
        await Ui.Busy("Working…", async _ => { await Task.Run(action); return true; });
        Ui.Ok(done);
        return true;
    }

    // ---------------------------------------------------------------- setup

    public static async Task SetupAsync()
    {
        AnsiConsole.Write(new Panel(
                "AnyPortProxy lets people on the internet reach things running on your computers:\n\n" +
                "  [bold]•[/] [deepskyblue1]Websites[/]: nas.example.com → your NAS, theo.example.com → another PC (ports 80/443)\n" +
                "  [bold]•[/] [deepskyblue1]Everything else[/]: start something on any port on this PC and it's reachable at example.com:PORT\n\n" +
                "[grey]Setup takes about a minute. You can change everything later.[/]")
            .Header("[bold] Welcome [/]").Border(BoxBorder.Rounded));

        if (ServiceManager.GetState() == ServiceState.NotInstalled || AnsiConsole.Confirm("Reinstall / update the program files?", false))
        {
            AnsiConsole.MarkupLine("\n[bold]Step 1 of 4 — Install[/]");
            await Task.Run(() => Installer.Install(m => Ui.Info(m)));
        }

        var c = ConfigStore.Load();
        AnsiConsole.MarkupLine("\n[bold]Step 2 of 4 — Your domain[/]");
        Ui.Hint("The domain you own (like example.com). Leave empty if you don't have one.");
        var domain = AnsiConsole.Prompt(new TextPrompt<string>("Domain:").AllowEmpty().DefaultValue(c.Domain ?? "").ShowDefaultValue(c.Domain is not null));
        if (!string.IsNullOrWhiteSpace(domain))
        {
            c.Domain = domain.Trim().TrimEnd('.').ToLowerInvariant();
            ConfigStore.Save(c);
            Ui.Ok($"Domain: {c.Domain}");
        }

        AnsiConsole.MarkupLine("\n[bold]Step 3 of 4 — Websites[/]");
        while (AnsiConsole.Confirm(Websites.List(c.Proxy).Count == 0 ? "Add a website (like nas.example.com)?" : "Add another website?", Websites.List(c.Proxy).Count == 0))
        {
            await AddWebsiteAsync(c);
            c = ConfigStore.Load();
        }

        AnsiConsole.MarkupLine("\n[bold]Step 4 of 4 — Games and apps[/]");
        Ui.Hint("Every TCP port on this PC is already reachable from the internet. Opening a port here also sets up");
        Ui.Hint("Windows Firewall (for devices at home), your router, and UDP games like Minecraft Bedrock.");
        while (AnsiConsole.Confirm("Open a port for a game or app?", false))
        {
            await OpenPortAsync(c);
            c = ConfigStore.Load();
        }

        AnsiConsole.MarkupLine("\n[bold]All done! Running a quick health check…[/]\n");
        await CliApp.CheckAsync(false);
    }

    private static void SetDomain()
    {
        var c = ConfigStore.Load();
        var d = AnsiConsole.Prompt(new TextPrompt<string>("Your domain (like example.com):").AllowEmpty().DefaultValue(c.Domain ?? "").ShowDefaultValue(c.Domain is not null));
        c.Domain = string.IsNullOrWhiteSpace(d) ? null : d.Trim().TrimEnd('.').ToLowerInvariant();
        ConfigStore.Save(c);
        Ui.Ok(c.Domain is null ? "Domain cleared." : $"Domain set to {c.Domain}.");
    }

    private static async Task UninstallAsync()
    {
        if (!AnsiConsole.Confirm("Really uninstall AnyPortProxy?", false)) return;
        bool purge = AnsiConsole.Confirm("Also delete your settings (websites, ports)?", false);
        await Installer.UninstallAsync(purge, m => Ui.Info(m));
        Environment.Exit(0);
    }

    // ---------------------------------------------------------------- websites

    /// <summary>"nas" → "nas.example.com" when a domain is set.</summary>
    public static string ExpandHost(AppConfig c, string host)
    {
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.StartsWith("http://")) host = host[7..];
        if (host.StartsWith("https://")) host = host[8..];
        host = host.TrimEnd('/');
        if (host.Length > 0 && !host.Contains('.') && host != "*" && !string.IsNullOrWhiteSpace(c.Domain)) host = $"{host}.{c.Domain}";
        return host;
    }

    public static string ExpandComputer(string computer) =>
        computer.Trim().ToLowerInvariant() is "this-pc" or "thispc" or "this" or "local" or "localhost" or "me" ? ThisPc : computer.Trim();

    private static async Task WebsitesMenuAsync()
    {
        while (true)
        {
            AnsiConsole.Clear();
            Ui.Banner();
            AnsiConsole.MarkupLine("[bold]Websites[/] — when someone visits one of these addresses, they're sent to the computer you chose.\n");
            CliApp.SitesList();
            var c = ConfigStore.Load();
            var sites = Websites.List(c.Proxy);

            var choices = new List<string> { "Add a website" };
            if (sites.Count > 0) choices.AddRange(["Change a website", "Remove a website", "Test the websites"]);
            choices.AddRange(["Change where unknown addresses go", "Back"]);
            var choice = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("\nWhat now?").AddChoices(choices));
            try
            {
                switch (choice)
                {
                    case "Add a website":
                        await AddWebsiteAsync(c);
                        Ui.PressAnyKey();
                        break;
                    case "Change a website":
                    {
                        var w = PickSite(sites, "Which one?");
                        await AddWebsiteAsync(c, w);
                        Ui.PressAnyKey();
                        break;
                    }
                    case "Remove a website":
                    {
                        var w = PickSite(sites, "Remove which one?");
                        if (AnsiConsole.Confirm($"Remove {w.Host}?", false))
                        {
                            Websites.Remove(c.Proxy, w.Host);
                            ConfigStore.Save(c);
                            Ui.Ok("Removed.");
                        }
                        Ui.PressAnyKey();
                        break;
                    }
                    case "Test the websites":
                        foreach (var w in sites) await TestWebsiteAsync(w);
                        Ui.PressAnyKey();
                        break;
                    case "Change where unknown addresses go":
                    {
                        var t = AskComputer("Send visitors of unknown addresses to which computer?");
                        c.Proxy.DefaultTarget = t;
                        ConfigStore.Save(c);
                        Ui.Ok($"Unknown addresses now go to {t}.");
                        Ui.PressAnyKey();
                        break;
                    }
                    default:
                        return;
                }
            }
            catch (Exception ex)
            {
                Ui.Error(ex.Message);
                Ui.PressAnyKey();
            }
        }
    }

    private static Website PickSite(List<Website> sites, string title) =>
        AnsiConsole.Prompt(new SelectionPrompt<Website>().Title(title).UseConverter(w => Ui.E(w.Host)).AddChoices(sites));

    private static string AskComputer(string title)
    {
        var lan = NetInfo.GetLanAddress();
        var choice = AnsiConsole.Prompt(new SelectionPrompt<string>().Title(title)
            .AddChoices($"This PC ({lan?.ToString() ?? "localhost"})", "Another computer on my network (I'll type its IP address)"));
        if (choice.StartsWith("This PC")) return ThisPc;
        Ui.Hint("Find a device's IP in your router's device list, or run \"ipconfig\" on that computer. Example: 192.168.1.20");
        return AnsiConsole.Prompt(new TextPrompt<string>("IP address:")
            .Validate(s => Websites.ValidateComputer(s) is { } e ? ValidationResult.Error($"[red]{Ui.E(e)}[/]") : ValidationResult.Success())).Trim();
    }

    private static int? AskPort(string question, int? suggested)
    {
        var text = AnsiConsole.Prompt(new TextPrompt<string>($"{question} [grey](number, or \"none\")[/]")
            .DefaultValue(suggested?.ToString() ?? "none")
            .Validate(s => s.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) || (int.TryParse(s, out var p) && p is >= 1 and <= 65535)
                ? ValidationResult.Success()
                : ValidationResult.Error("[red]Type a port number from 1 to 65535, or none[/]")));
        return int.TryParse(text, out var port) ? port : null;
    }

    public static async Task AddWebsiteAsync(AppConfig c, Website? existing = null)
    {
        if (!string.IsNullOrWhiteSpace(c.Domain)) Ui.Hint($"Tip: just type the first part (like \"nas\") and I'll add .{c.Domain}");
        var hostPrompt = new TextPrompt<string>("Address people will type:")
            .Validate(s => Websites.ValidateHost(ExpandHost(c, s)) is { } e ? ValidationResult.Error($"[red]{Ui.E(e)}[/]") : ValidationResult.Success());
        if (existing is not null) hostPrompt.DefaultValue(existing.Host);
        var host = ExpandHost(c, AnsiConsole.Prompt(hostPrompt));

        var computer = AskComputer($"Which computer is {host} on?");
        var w = new Website { Host = host, Computer = computer, Other = existing?.Other ?? new() };

        if (computer == ThisPc)
        {
            Ui.Hint("AnyPortProxy itself uses ports 80/443 on this PC, so your website needs other ports (for example 8080 and 8443).");
            w.HttpPort = AskPort("Port your website uses for http on this PC", existing?.HttpPort is int hp && hp != 80 ? hp : 8080);
            w.HttpsPort = AskPort("Port your website uses for https on this PC", existing?.HttpsPort is int sp && sp != 443 ? sp : null);
            while (w.HttpPort is 80 || w.HttpsPort is 443)
            {
                Ui.Error("80/443 on this PC would loop back into AnyPortProxy. Pick other ports.");
                w.HttpPort = AskPort("http port", 8080);
                w.HttpsPort = AskPort("https port", null);
            }
        }
        else if (AnsiConsole.Confirm("Does that computer use the normal web ports (80 for http, 443 for https)?", true))
        {
            w.HttpPort = 80;
            w.HttpsPort = 443;
        }
        else
        {
            w.HttpPort = AskPort("Port for http visitors on that computer", existing?.HttpPort ?? 80);
            w.HttpsPort = AskPort("Port for https visitors on that computer", existing?.HttpsPort ?? 443);
        }

        if (w.HttpPort is null && w.HttpsPort is null)
        {
            Ui.Warn("Both are \"none\", so there's nothing to route. Nothing saved.");
            return;
        }
        await SaveWebsiteAsync(c, w, existing?.Host);
    }

    public static async Task SaveWebsiteAsync(AppConfig c, Website w, string? originalHost)
    {
        Websites.Upsert(c.Proxy, w, originalHost);
        ConfigStore.Save(c);
        Ui.Ok($"Saved: {w.Host} → http {w.HttpText} · https {w.HttpsText}");
        await TestWebsiteAsync(w);

        try
        {
            if (!w.Host.Contains('*'))
            {
                var ip = await NetInfo.GetPublicIpAsync();
                var addrs = await System.Net.Dns.GetHostAddressesAsync(w.Host);
                if (ip is not null && !addrs.Contains(ip))
                    Ui.Warn($"{w.Host} doesn't point to your internet address ({ip}) yet. Add/update an A record at your domain provider.");
                else if (ip is not null)
                    Ui.Ok($"{w.Host} points to your internet address.");
            }
        }
        catch
        {
            Ui.Warn($"{w.Host} doesn't exist on the internet yet. Add an A record for it at your domain provider.");
        }
    }

    private static async Task TestWebsiteAsync(Website w)
    {
        foreach (var (label, port) in new[] { ("http", w.HttpPort), ("https", w.HttpsPort) })
        {
            if (port is not int p) continue;
            var ok = await NetInfo.CanConnectAsync(w.Computer, p);
            if (ok) Ui.Ok($"{w.Host} ({label}): {TargetParser.Format(w.Computer, p)} is answering");
            else Ui.Warn($"{w.Host} ({label}): {TargetParser.Format(w.Computer, p)} isn't answering — is that computer on and the website running?");
        }
    }

    // ---------------------------------------------------------------- ports

    /// <summary>Turns "25565", "2456-2458" or "minecraft java" into a request; asks when ambiguous.</summary>
    public static OpenPortRequest? ResolvePortRequest(string what, PortProtocol? protocol, string? name)
    {
        if (PortRanges.TryParseOne(what, out var lo, out var hi))
        {
            var preset = Presets.ForPort(lo);
            if (preset is not null && name is null) Ui.Info($"Port {lo} is usually {preset.Name}.");
            return new OpenPortRequest
            {
                Port = lo,
                EndPort = hi > lo ? hi : null,
                Protocol = protocol ?? (preset is not null && preset.Port == lo ? preset.Protocol : PortProtocol.Tcp),
                Name = name ?? (preset is not null && preset.Port == lo ? preset.Name : ""),
            };
        }

        var matches = Presets.Search(what);
        PortPreset chosen;
        if (matches.Count == 0)
        {
            Ui.Error($"I don't know \"{what}\". Use a port number, or see the list: apx port presets");
            return null;
        }
        if (matches.Count == 1)
        {
            chosen = matches[0];
        }
        else if (Ui.Interactive)
        {
            chosen = AnsiConsole.Prompt(new SelectionPrompt<PortPreset>().Title($"Which one did you mean by \"{Ui.E(what)}\"?")
                .UseConverter(p => Ui.E(p.ToString())).AddChoices(matches));
        }
        else
        {
            Ui.Error($"\"{what}\" matches several things: {string.Join(", ", matches.Select(m => m.Name))}. Be more specific.");
            return null;
        }
        Ui.Info($"{chosen.Name} uses {chosen.ProtocolText} {chosen.Range}.");
        return new OpenPortRequest
        {
            Port = chosen.Port,
            EndPort = chosen.EndPort,
            Protocol = protocol ?? chosen.Protocol,
            Name = name ?? chosen.Name,
        };
    }

    public static async Task OpenPortAsync(AppConfig c)
    {
        const string custom = "✏  Something else — I'll type the port number";
        var options = new List<string> { custom };
        options.AddRange(Presets.All.Select(p => p.ToString()));
        var pick = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title("[bold]What do you want to run?[/] [grey](type to search)[/]")
            .PageSize(15)
            .EnableSearch()
            .UseConverter(Ui.E)
            .AddChoices(options));

        OpenPortRequest? req;
        if (pick == custom)
        {
            var text = AnsiConsole.Prompt(new TextPrompt<string>("Port number (or range like 2456-2458):")
                .Validate(s => PortRanges.TryParseOne(s, out _, out _) ? ValidationResult.Success() : ValidationResult.Error("[red]Type a number from 1 to 65535[/]")));
            req = ResolvePortRequest(text, null, null);
            if (req is null) return;
            req.Protocol = AnsiConsole.Prompt(new SelectionPrompt<PortProtocol>()
                .Title("What kind? [grey](if unsure: TCP; games often say UDP)[/]")
                .UseConverter(p => p switch { PortProtocol.Tcp => "TCP (most apps and websites)", PortProtocol.Udp => "UDP (many games, voice chat, VPNs)", _ => "Both TCP and UDP" })
                .AddChoices(req.Protocol, req.Protocol == PortProtocol.Tcp ? PortProtocol.Udp : PortProtocol.Tcp, PortProtocol.Both));
            req.Name = AnsiConsole.Prompt(new TextPrompt<string>("Give it a name:").DefaultValue(string.IsNullOrEmpty(req.Name) ? $"Port {text}" : req.Name));
        }
        else
        {
            var preset = Presets.All.First(p => p.ToString() == pick);
            Ui.Hint(preset.Description);
            req = new OpenPortRequest { Name = preset.Name, Port = preset.Port, EndPort = preset.EndPort, Protocol = preset.Protocol };
        }

        req.Firewall = true;
        Ui.Hint("Your router needs to send this port to this PC. I can ask it automatically (UPnP).");
        Ui.Hint("Skip that if you've already forwarded all ports on your router.");
        req.Router = AnsiConsole.Confirm("Ask the router to forward it automatically?", true);
        await RunOpenAsync(c, req, false);
    }

    public static async Task<bool> RunOpenAsync(AppConfig c, OpenPortRequest req, bool yes)
    {
        if (PortHelper.Validate(c, req.Port, req.EndPort, req.Protocol) is { } err)
        {
            Ui.Error(err);
            return false;
        }
        if (Presets.WarningForRange(req.Port, req.EndPort ?? req.Port) is { } w)
        {
            if (w.Risk == Risk.High) Ui.Error($"Warning: {w.Why}");
            else Ui.Warn(w.Why);
            if (!yes)
            {
                if (!Ui.Interactive)
                {
                    Ui.Hint("Add --yes if you really want to do this.");
                    return false;
                }
                if (!AnsiConsole.Confirm("Open it anyway?", false)) return false;
            }
        }

        var results = await Ui.Busy($"Opening {req.Name}…", p => PortHelper.OpenAsync(c, req, p));
        AnsiConsole.WriteLine();
        Ui.Results(results);
        return results.All(r => r.Status != CheckStatus.Fail);
    }

    public static async Task ClosePortAsync(AppConfig c)
    {
        if (c.Ports.Count == 0)
        {
            Ui.Info("No ports were opened with the helper.");
            return;
        }
        var rule = AnsiConsole.Prompt(new SelectionPrompt<PortRule>().Title("Close which port?")
            .UseConverter(r => Ui.E($"{r.Name}  ({r.Range} {r.ProtocolText})")).AddChoices(c.Ports));
        bool block = rule.Protocol != PortProtocol.Udp && c.Proxy.CatchAll.Enabled &&
                     AnsiConsole.Confirm("Also block it completely from the internet? (Otherwise all-ports forwarding still lets TCP through.)", false);
        var results = await Ui.Busy("Closing…", p => PortHelper.CloseAsync(c, rule, block, p));
        Ui.Results(results);
    }

    private static async Task CheckPortAsync()
    {
        var port = AnsiConsole.Prompt(new TextPrompt<int>("Which port?").Validate(p => p is >= 1 and <= 65535));
        var c = ConfigStore.Load();
        var proto = Presets.ForPort(port)?.Protocol ?? PortProtocol.Tcp;
        bool router = AnsiConsole.Confirm("Also ask the router?", true);
        var results = await Ui.Busy("Checking…", p => PortHelper.CheckAsync(c, port, proto, router, p));
        Ui.Results(results);
        await Ui.OfferFixesAsync(results, false);
    }

    private static async Task ForwardMenuAsync()
    {
        while (true)
        {
            AnsiConsole.Clear();
            Ui.Banner();
            var c = ConfigStore.Load();
            var ca = c.Proxy.CatchAll;
            AnsiConsole.MarkupLine("[bold]All-ports forwarding[/] — any TCP port that isn't 80/443 is sent to a computer, keeping the same port number.\n");
            CliApp.RunAsync(["forward"]).Wait();
            var choices = new List<string>
            {
                ca.Enabled ? "Turn it OFF" : "Turn it ON",
                "Change which ports are forwarded",
                "Block a port",
                "Unblock a port",
                ca.InterceptLan ? "Only redirect internet visitors (recommended)" : "Also redirect devices on my home network",
                "Send forwarded ports to a different computer",
                ca.Udp ? "Turn UDP forwarding OFF" : "Turn UDP forwarding ON",
                "Back",
            };
            var choice = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("\nWhat now?").AddChoices(choices));
            switch (choice)
            {
                case "Back":
                    return;
                case "Turn it OFF" or "Turn it ON":
                    ca.Enabled = !ca.Enabled;
                    break;
                case "Change which ports are forwarded":
                    ca.AllowedPorts = AnsiConsole.Prompt(new TextPrompt<string>("Ports [grey](like 1-49151 or 3000-3999, 25565)[/]:").DefaultValue(ca.AllowedPorts)
                        .Validate(s => PortRanges.TryParse(s, out _, out var e) ? ValidationResult.Success() : ValidationResult.Error($"[red]{Ui.E(e)}[/]")));
                    break;
                case "Block a port":
                {
                    var p = AnsiConsole.Prompt(new TextPrompt<int>("Port to block:").Validate(x => x is >= 1 and <= 65535));
                    if (!ca.BlockedPorts.Contains(p)) ca.BlockedPorts.Add(p);
                    ca.BlockedPorts.Sort();
                    break;
                }
                case "Unblock a port":
                {
                    if (ca.BlockedPorts.Count == 0) break;
                    var p = AnsiConsole.Prompt(new SelectionPrompt<int>().Title("Unblock which?")
                        .UseConverter(x => Ui.E($"{x}  {Presets.Warning(x)?.Why ?? Presets.Describe(x) ?? ""}")).AddChoices(ca.BlockedPorts));
                    if (Presets.Warning(p) is { Risk: Risk.High } && !AnsiConsole.Confirm("This port is dangerous to open. Unblock anyway?", false)) break;
                    ca.BlockedPorts.Remove(p);
                    break;
                }
                case "Turn UDP forwarding OFF" or "Turn UDP forwarding ON":
                    ca.Udp = !ca.Udp;
                    break;
                case "Send forwarded ports to a different computer":
                    ca.Target = AskComputer("Send forwarded ports to which computer?");
                    break;
                default:
                    ca.InterceptLan = !ca.InterceptLan;
                    break;
            }
            ConfigStore.Save(c);
        }
    }

    /// <summary>The getting-started tour, terminal edition.</summary>
    public static async Task TourAsync()
    {
        var c = ConfigStore.Load();
        var domain = string.IsNullOrWhiteSpace(c.Domain) ? "yourdomain.com" : c.Domain;
        var lan = NetInfo.GetLanAddress()?.ToString() ?? "this PC's address";
        var gw = NetInfo.GetGatewayAddress()?.ToString();

        var pages = new (string Title, string Body)[]
        {
            ("Welcome 👋",
                "AnyPortProxy lets people on the internet reach things running on your computers at home — " +
                "your NAS, a website, a game server, a dev project — using your own domain.\n\n" +
                "  [deepskyblue1]People online[/]  →  [yellow]your router[/]  →  [green]this PC (AnyPortProxy)[/]  →  [mediumpurple]your computers[/]\n\n" +
                "This tour takes about 2 minutes."),
            ("Two ways in",
                "[bold]🌐 Websites — the address decides[/]  (ports 80/443)\n" +
                $"  nas.{domain}  →  AnyPortProxy reads the address  →  your NAS\n\n" +
                "[bold]🎮 Games & apps — the port decides[/]  (every other port, TCP and UDP)\n" +
                $"  {domain}:25565  →  AnyPortProxy keeps the port  →  Minecraft on this PC\n\n" +
                "[grey]The address only matters for websites; for games and apps every name that points to you works the same.[/]"),
            ("Your domain",
                "The name people type, bought once from a domain provider (Cloudflare, Namecheap…).\n" +
                (c.Domain is null ? "Set it with:  [deepskyblue1]apx domain example.com[/]\n\n" : $"Yours: [bold]{c.Domain}[/]\n\n") +
                "At your provider, add two A records pointing to your internet address:\n" +
                "  [bold]@[/]  (the domain itself)\n  [bold]*[/]  (any subdomain — so new names work without touching DNS again)\n\n" +
                "[grey]Cloudflare users: set them to \"DNS only\" (grey cloud) for games and apps.[/]"),
            ("Your router",
                "Your router blocks everything from the internet until you tell it once: \"send everything to this PC\".\n\n" +
                $"  1. Open its settings page{(gw is null ? "" : $" ([deepskyblue1]http://{gw}[/])")} — the login is often on a sticker.\n" +
                "  2. Find [bold]DMZ[/] (under Advanced, NAT or Firewall).\n" +
                $"  3. Set the DMZ host to [bold]{lan}[/] and save.\n\n" +
                $"No DMZ? Forward ports 1–65535, TCP and UDP, to {lan}.\n" +
                "[grey]Dangerous ports (Remote Desktop, file sharing…) stay blocked by AnyPortProxy.[/]"),
            ("Websites",
                "A website rule: \"visitors to this address go to that computer\". Certificates stay on that computer.\n\n" +
                "  [deepskyblue1]apx site add nas 192.168.1.20[/]                   (http→80, https→443 there)\n" +
                "  [deepskyblue1]apx site add theo 192.168.1.30 --http 5000 --https 5001[/]\n" +
                "  [deepskyblue1]apx site add white this-pc --http 8080 --https off[/]   (a site on this PC)\n" +
                "  [deepskyblue1]apx sites[/]                                        (list them)"),
            ("Games & apps",
                $"Every port is already forwarded: start a server on this PC and people connect to {domain}:PORT.\n\n" +
                "\"Open a port\" adds a firewall rule (for devices at home), opens your router (UPnP) and gives the app the fastest path:\n" +
                "  [deepskyblue1]apx port open \"minecraft java\" --router[/]\n" +
                "  [deepskyblue1]apx port check 25565[/]                          (why can't people connect?)\n\n" +
                "Server on another computer? Send the port there:\n" +
                "  [deepskyblue1]apx portmap add 51820 192.168.1.20 --udp --name WireGuard[/]"),
            ("Check everything",
                "The health check looks at the service, firewall, router, DNS and websites, and fixes most problems:\n" +
                "  [deepskyblue1]apx check[/]          [deepskyblue1]apx check --fix[/]\n\n" +
                "Watch connections live:  [deepskyblue1]apx logs -f[/]\n" +
                "Test from outside: your phone on mobile data (not Wi-Fi)."),
            ("You're ready 🎉",
                "  [deepskyblue1]apx[/]            the menu (easiest)\n" +
                "  [deepskyblue1]apx status[/]     is everything running?\n" +
                "  [deepskyblue1]apx help[/]       every command\n" +
                "  [deepskyblue1]apx gui[/]        the app, with the same features and this tour\n" +
                "  [deepskyblue1]apx tour[/]       this tour again"),
        };

        for (int i = 0; i < pages.Length; i++)
        {
            if (Ui.Interactive) AnsiConsole.Clear();
            Ui.Banner();
            AnsiConsole.Write(new Panel(new Markup(pages[i].Body))
                .Header($"[bold] {i + 1}/{pages.Length}  {Ui.E(pages[i].Title)} [/]")
                .Border(BoxBorder.Rounded)
                .Padding(2, 1));
            if (!Ui.Interactive) continue;
            AnsiConsole.MarkupLine(i < pages.Length - 1
                ? "\n[grey]Enter = next · B = back · Q = quit the tour[/]"
                : "\n[grey]Press any key to finish.[/]");
            var key = Console.ReadKey(true).Key;
            if (key == ConsoleKey.Q || key == ConsoleKey.Escape) break;
            if (key == ConsoleKey.B && i > 0) i -= 2;
        }

        if (!c.Onboarded)
        {
            try
            {
                c = ConfigStore.Load();
                c.Onboarded = true;
                ConfigStore.Save(c);
            }
            catch
            {
                // Not admin: fine, the tour flag just isn't remembered.
            }
        }

        if (Ui.Interactive && Elevation.IsAdmin && Websites.List(ConfigStore.Load().Proxy).Count == 0
            && AnsiConsole.Confirm("\nSet things up now with the guided setup?", true))
            await SetupAsync();
    }

    private static void PortMapMenu()
    {
        while (true)
        {
            AnsiConsole.Clear();
            Ui.Banner();
            AnsiConsole.MarkupLine("[bold]Send a port to another computer[/] — e.g. UDP 51820 → your NAS for WireGuard, or TCP 22 → a Raspberry Pi.\n");
            var c = ConfigStore.Load();
            CliApp.PortMapList(c);
            var choices = new List<string> { "Add a port rule" };
            if (c.Proxy.Forwards.Count > 0) choices.Add("Remove a port rule");
            choices.Add("Back");
            var choice = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("\nWhat now?").AddChoices(choices));
            try
            {
                if (choice == "Back") return;
                if (choice == "Remove a port rule")
                {
                    var rule = AnsiConsole.Prompt(new SelectionPrompt<PortForward>().Title("Remove which?")
                        .UseConverter(f => Ui.E($"{f.ProtocolText} {f.Range} → {f.Target}  {f.Name}")).AddChoices(c.Proxy.Forwards));
                    c.Proxy.Forwards.Remove(rule);
                    ConfigStore.Save(c);
                    Ui.Ok("Removed.");
                    Ui.PressAnyKey();
                    continue;
                }

                var text = AnsiConsole.Prompt(new TextPrompt<string>("Port on this PC (or range like 2456-2458):")
                    .Validate(s => PortRanges.TryParseOne(s, out _, out _) ? ValidationResult.Success() : ValidationResult.Error("[red]Type a number from 1 to 65535[/]")));
                PortRanges.TryParseOne(text, out var lo, out var hi);
                var preset = Presets.ForPort(lo);
                if (preset is not null) Ui.Info($"Port {lo} is usually {preset.Name} ({preset.ProtocolText}).");
                var proto = AnsiConsole.Prompt(new SelectionPrompt<PortProtocol>()
                    .Title("What kind? [grey](games, voice and VPNs usually use UDP)[/]")
                    .UseConverter(p => p switch { PortProtocol.Tcp => "TCP", PortProtocol.Udp => "UDP", _ => "Both TCP and UDP" })
                    .AddChoices(new[] { preset?.Protocol ?? PortProtocol.Tcp, PortProtocol.Tcp, PortProtocol.Udp, PortProtocol.Both }.Distinct()));
                Ui.Hint("The computer's IP address, e.g. 192.168.1.20. Add :port to use a different port there.");
                var target = AnsiConsole.Prompt(new TextPrompt<string>("Send it to:"));
                var name = AnsiConsole.Prompt(new TextPrompt<string>("Name:").DefaultValue(preset?.Name ?? $"Port {text}"));
                var f = new PortForward { Port = lo, EndPort = hi > lo ? hi : null, Protocol = proto, Target = ExpandComputer(target), Name = name };
                if (PortForwards.Validate(c.Proxy, f) is { } err)
                {
                    Ui.Error(err);
                }
                else
                {
                    c.Proxy.Forwards.Add(f);
                    ConfigStore.Save(c);
                    Ui.Ok($"{f.ProtocolText} {f.Range} now goes to {f.Target}. Make sure your router forwards it to this PC.");
                }
            }
            catch (Exception ex)
            {
                Ui.Error(ex.Message);
            }
            Ui.PressAnyKey();
        }
    }

    private static void WatchLogs()
    {
        AnsiConsole.Clear();
        Ui.Banner();
        AnsiConsole.MarkupLine("[grey]Live activity — press any key to go back.[/]\n");
        foreach (var line in LogReader.Tail(25)) CliApp.PrintLog(line);
        var follower = new LogReader.Follower();
        while (!Console.KeyAvailable)
        {
            foreach (var line in follower.ReadNew()) CliApp.PrintLog(line);
            Thread.Sleep(400);
        }
        Console.ReadKey(true);
    }
}
