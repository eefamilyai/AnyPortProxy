using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net;
using AnyPortProxy.Core;

namespace AnyPortProxy.Gui;

/// <summary>
/// The getting-started tour: explains how AnyPortProxy works in plain language, and lets the user do each
/// thing right there (domain, router, first website, first game, check). Shown once after install; reopen anytime.
/// </summary>
internal sealed class OnboardingForm : Form
{
    private static readonly string[] Steps =
    [
        "Welcome",
        "How it works",
        "Your domain",
        "Your router",
        "Websites",
        "Games & apps",
        "Check everything",
        "You're ready",
    ];

    private readonly MainForm _main;
    private readonly Panel _content = new() { Dock = DockStyle.Fill, Padding = new Padding(30, 24, 30, 10), BackColor = Color.White };
    private readonly Panel _steps = new() { Dock = DockStyle.Left, Width = 210, BackColor = Theme.Nav };
    private readonly Button _back, _next, _skip;
    private readonly Label _counter = new() { AutoSize = true, ForeColor = Theme.Gray, UseMnemonic = false, Margin = new Padding(0, 12, 12, 0) };
    private int _step;

    private string Domain => string.IsNullOrWhiteSpace(_main.Config.Domain) ? "yourdomain.com" : _main.Config.Domain!;

    public OnboardingForm(MainForm main)
    {
        _main = main;
        Text = "Getting started with AnyPortProxy";
        Icon = Theme.AppIcon;
        Font = Theme.Body;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(960, 640);
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.White;
        MinimizeBox = false;

        _back = Theme.Secondary("←  Back", (_, _) => Go(_step - 1));
        _next = Theme.Primary("Next  →", (_, _) => Go(_step + 1));
        _next.Font = Theme.H2;
        _skip = Theme.Secondary("Skip the tour", (_, _) => Finish());
        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 62,
            Padding = new Padding(16, 12, 16, 10),
            BackColor = Theme.Light,
        };
        bottom.Controls.AddRange([_next, _back, _counter]);
        var left = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, Padding = new Padding(16, 12, 0, 10), BackColor = Theme.Light };
        left.Controls.Add(_skip);
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 62, BackColor = Theme.Light };
        bottom.Dock = DockStyle.Fill;
        bar.Controls.Add(bottom);
        bar.Controls.Add(left);

        _steps.Paint += PaintSteps;
        Controls.Add(_content);
        Controls.Add(_steps);
        Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Border });
        Controls.Add(bar);
        AcceptButton = _next;
        FormClosing += (_, _) => MarkDone();
        Go(0);
    }

    // ---------------------------------------------------------------- navigation

    private void Go(int step)
    {
        if (step >= Steps.Length)
        {
            Finish();
            return;
        }
        if (step < 0) return;
        SaveDomainIfEdited();
        _step = step;
        _content.SuspendLayout();
        _content.Controls.Clear();
        var page = new StackPanel { Padding = new Padding(0) };
        _content.Controls.Add(page);
        switch (step)
        {
            case 0: Welcome(page); break;
            case 1: HowItWorks(page); break;
            case 2: DomainStep(page); break;
            case 3: RouterStep(page); break;
            case 4: WebsitesStep(page); break;
            case 5: GamesStep(page); break;
            case 6: CheckStep(page); break;
            default: DoneStep(page); break;
        }
        _content.ResumeLayout();
        _back.Visible = step > 0;
        _skip.Visible = step < Steps.Length - 1;
        _next.Text = step == Steps.Length - 1 ? "Start using AnyPortProxy  ✔" : step == 0 ? "Let's go  →" : "Next  →";
        _counter.Text = $"Step {step + 1} of {Steps.Length}";
        _steps.Invalidate();
        _next.Focus();
    }

    private void Finish()
    {
        MarkDone();
        DialogResult = DialogResult.OK;
        Close();
    }

    private void MarkDone()
    {
        SaveDomainIfEdited();
        if (_main.Config.Onboarded) return;
        _main.Config.Onboarded = true;
        _main.SaveConfig();
    }

    private void PaintSteps(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        TextRenderer.DrawText(g, "Getting started", Theme.H2, new Point(20, 22), Theme.Text, TextFormatFlags.NoPrefix);
        int y = 70;
        for (int i = 0; i < Steps.Length; i++, y += 46)
        {
            bool done = i < _step, current = i == _step;
            var circle = new Rectangle(20, y, 26, 26);
            using (var b = new SolidBrush(current ? Theme.Accent : done ? Theme.Green : Color.White))
                g.FillEllipse(b, circle);
            using (var p = new Pen(current ? Theme.Accent : done ? Theme.Green : Theme.Border, 1.5f))
                g.DrawEllipse(p, circle);
            TextRenderer.DrawText(g, done ? "✓" : (i + 1).ToString(), Theme.Bold, circle, current || done ? Color.White : Theme.Gray,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (i < Steps.Length - 1)
            {
                using var line = new Pen(done ? Theme.Green : Theme.Border, 2);
                g.DrawLine(line, 33, y + 28, 33, y + 44);
            }
            TextRenderer.DrawText(g, Steps[i], current ? Theme.Bold : Theme.Body, new Point(56, y + 3), current ? Theme.Accent : done ? Theme.Text : Theme.Gray, TextFormatFlags.NoPrefix);
        }
    }

    // ---------------------------------------------------------------- building blocks

    private static Label Title(string text) => Theme.Label(text, Theme.H1);

    private static WrapLabel Para(string text, Color? color = null) => Theme.Wrap(text, Theme.Body, color ?? Theme.Text);

    private static WrapLabel Small(string text) => Theme.Wrap(text, Theme.Small, Theme.Gray);

    private static Banner Tip(CheckStatus status, string text)
    {
        var b = new Banner();
        b.Set(status, text);
        return b;
    }

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var r = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0, 6, 0, 6), Tag = "natural" };
        r.Controls.AddRange(controls);
        return r;
    }

    /// <summary>A row of boxes joined by arrows — the pictures that explain how traffic flows.</summary>
    private static Panel Diagram(int height, params (string Icon, string Title, string Sub, Color Color)[] boxes)
    {
        var p = new Panel { Height = height, Margin = new Padding(0, 10, 0, 14) };
        p.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int n = boxes.Length, gap = 34;
            int w = (p.Width - gap * (n - 1) - 4) / n;
            for (int i = 0; i < n; i++)
            {
                var (icon, title, sub, color) = boxes[i];
                var r = new Rectangle(2 + i * (w + gap), 2, w, height - 6);
                using (var path = Theme.Rounded(r, 12))
                using (var fill = new SolidBrush(Color.FromArgb(28, color)))
                using (var edge = new Pen(Color.FromArgb(120, color), 1.5f))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(edge, path);
                }
                TextRenderer.DrawText(g, icon, new Font("Segoe UI Emoji", 20f), new Rectangle(r.X, r.Y + 8, r.Width, 40), color,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, title, Theme.Bold, new Rectangle(r.X + 6, r.Y + 52, r.Width - 12, 22), Theme.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, sub, Theme.Small, new Rectangle(r.X + 6, r.Y + 74, r.Width - 12, height - 84), Theme.Gray,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                if (i < n - 1)
                {
                    int ax = r.Right + 6, ay = r.Y + r.Height / 2;
                    using var pen = new Pen(Theme.Gray, 2.5f) { EndCap = LineCap.ArrowAnchor };
                    g.DrawLine(pen, ax, ay, ax + gap - 12, ay);
                }
            }
        };
        p.Resize += (_, _) => p.Invalidate();
        return p;
    }

    private static Button CopyButton(string text) =>
        Theme.Secondary("📋  Copy", (s, _) =>
        {
            try
            {
                Clipboard.SetText(text);
                ((Button)s!).Text = "✔  Copied";
            }
            catch
            {
            }
        });

    // ---------------------------------------------------------------- 1. welcome

    private void Welcome(StackPanel page)
    {
        page.Controls.Add(Title("Welcome to AnyPortProxy 👋"));
        page.Controls.Add(Para("AnyPortProxy lets people on the internet reach things running on your computers at home — " +
                               "your NAS, a website, a game server, a dev project — using your own domain name."));
        page.Controls.Add(Diagram(150,
            ("🌍", "People online", "Friends, your phone, anyone with the address", Theme.Accent),
            ("📶", "Your router", "Sends incoming traffic to this PC", Theme.Amber),
            ("💻", "This PC", "AnyPortProxy decides where each connection goes", Theme.Green),
            ("🗄", "Your computers", "NAS, other PCs, games, apps", Color.FromArgb(124, 58, 237))));
        page.Controls.Add(Para("This short tour (about 2 minutes) explains how it works and helps you set up the important parts. " +
                               "You can do each step right here, or skip and come back later — the tour is always under Settings → Help."));
        page.Controls.Add(Tip(CheckStatus.Info, _main.State == ServiceState.Running
            ? "AnyPortProxy is installed and running in the background. It starts automatically with Windows."
            : "AnyPortProxy isn't running right now. That's fine for the tour — press Start at the top of the main window afterwards."));
    }

    // ---------------------------------------------------------------- 2. how it works

    private void HowItWorks(StackPanel page)
    {
        page.Controls.Add(Title("Two ways in"));
        page.Controls.Add(Para("Every connection arrives with an address and a port number. AnyPortProxy uses them like this:"));

        page.Controls.Add(Theme.Label("🌐  Websites — the address decides", Theme.H2));
        page.Controls.Add(Small("For normal web addresses (ports 80 and 443). Each address can go to a different computer."));
        page.Controls.Add(Diagram(120,
            ("🔗", $"nas.{Domain}", "typed in a browser", Theme.Accent),
            ("💻", "AnyPortProxy", "reads the address", Theme.Green),
            ("🗄", "Your NAS", "e.g. 192.168.1.20", Color.FromArgb(124, 58, 237))));

        page.Controls.Add(Theme.Label("🎮  Games & apps — the port decides", Theme.H2));
        page.Controls.Add(Small("For everything else. Anything you run on this PC is reachable at your domain plus its port number — no setup needed."));
        page.Controls.Add(Diagram(120,
            ("🔗", $"{Domain}:25565", "typed in Minecraft", Theme.Accent),
            ("💻", "AnyPortProxy", "keeps the port number", Theme.Green),
            ("🎮", "Minecraft on this PC", "listening on port 25565", Color.FromArgb(124, 58, 237))));

        page.Controls.Add(Tip(CheckStatus.Info,
            "Good to know: the address only matters for websites. For games and apps, every name that points to you works the same way — " +
            $"{Domain}:25565 and anything.{Domain}:25565 reach the same game. Works for TCP and UDP."));
    }

    // ---------------------------------------------------------------- 3. domain

    private TextBox? _domainBox;
    private Label? _domainStatus;
    private Label? _wildStatus;

    private void SaveDomainIfEdited()
    {
        if (_domainBox is null || _domainBox.IsDisposed) return;
        var d = _domainBox.Text.Trim().TrimEnd('.').ToLowerInvariant();
        if (d.Contains("://")) d = new Uri(d).Host;
        var current = _main.Config.Domain ?? "";
        if (d == current) return;
        _main.Config.Domain = d.Length == 0 ? null : d;
        _main.SaveConfig();
    }

    private void DomainStep(StackPanel page)
    {
        page.Controls.Add(Title("Your domain"));
        page.Controls.Add(Para("Your domain is the name people type, like example.com. You buy it once from a domain provider " +
                               "(Cloudflare, Namecheap, GoDaddy…). AnyPortProxy uses it to show you the right addresses, and lets you type just \"nas\" instead of nas.example.com."));

        _domainBox = new TextBox { Width = 300, Text = _main.Config.Domain ?? "", PlaceholderText = "example.com", Font = Theme.H2, Tag = "natural" };
        var check = Theme.Primary("Check it", async (_, _) =>
        {
            SaveDomainIfEdited();
            await CheckDomainAsync();
        });
        page.Controls.Add(Row(_domainBox, check));
        _domainStatus = new Label { AutoSize = true, ForeColor = Theme.Gray, UseMnemonic = false, Text = "Don't have one? You can skip this — people can still use your internet address.", Margin = new Padding(0, 4, 0, 10) };
        page.Controls.Add(_domainStatus);
        _domainStatus.MaximumSize = new Size(640, 0);
        _domainStatus.Margin = new Padding(0, 4, 0, 2);
        _wildStatus = new Label { AutoSize = true, UseMnemonic = false, Visible = false, MaximumSize = new Size(640, 0), Margin = new Padding(0, 0, 0, 10) };
        page.Controls.Add(_wildStatus);

        page.Controls.Add(Theme.Label("What to set at your domain provider", Theme.H2));
        page.Controls.Add(Small("Two \"A records\" pointing to your internet address. The star means \"any name\", so every subdomain works without touching DNS again."));
        var ip = _main.PublicIp?.ToString() ?? "(your internet address)";
        var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 4, Tag = "natural", Margin = new Padding(0, 6, 0, 6), CellBorderStyle = TableLayoutPanelCellBorderStyle.Single, BackColor = Theme.Light };
        void Cell(string t, Font f, int col, int row) => table.Controls.Add(new Label { Text = t, Font = f, AutoSize = true, UseMnemonic = false, Margin = new Padding(10, 6, 10, 6) }, col, row);
        Cell("Type", Theme.Bold, 0, 0); Cell("Name", Theme.Bold, 1, 0); Cell("Points to", Theme.Bold, 2, 0); Cell("", Theme.Bold, 3, 0);
        Cell("A", Theme.Body, 0, 1); Cell("@  (the domain itself)", Theme.Body, 1, 1); Cell(ip, Theme.Mono, 2, 1);
        Cell("A", Theme.Body, 0, 2); Cell("*  (any subdomain)", Theme.Body, 1, 2); Cell(ip, Theme.Mono, 2, 2);
        table.Controls.Add(CopyButton(ip), 3, 1);
        page.Controls.Add(table);
        page.Controls.Add(Small("Using Cloudflare? Set these to \"DNS only\" (grey cloud) for games and apps — the orange-cloud proxy only carries websites."));
        if (!string.IsNullOrWhiteSpace(_main.Config.Domain)) _ = CheckDomainAsync();
    }

    private async Task CheckDomainAsync()
    {
        if (_domainStatus is null) return;
        var d = _main.Config.Domain;
        if (string.IsNullOrWhiteSpace(d))
        {
            _domainStatus.Text = "Type your domain first.";
            return;
        }
        _domainStatus.ForeColor = Theme.Gray;
        _domainStatus.Text = "Checking the internet…";
        var ip = _main.PublicIp ?? await NetInfo.GetPublicIpAsync();
        async Task<IPAddress[]> Resolve(string name)
        {
            try { return await Dns.GetHostAddressesAsync(name); } catch { return []; }
        }
        var root = await Resolve(d);
        var wild = await Resolve($"apx-tour-{Guid.NewGuid():N}"[..18] + "." + d);
        if (_domainStatus.IsDisposed) return;
        bool rootOk = ip is not null && root.Contains(ip), wildOk = ip is not null && wild.Contains(ip);
        _domainStatus.ForeColor = rootOk ? Theme.Green : Theme.Amber;
        _domainStatus.Text = rootOk ? $"✔  {d} points to your internet address ({ip})."
            : root.Length == 0 ? $"⚠  {d} doesn't point anywhere yet — add the @ record below."
            : $"⚠  {d} points to {root[0]}, but your internet address is {ip}. Update the @ record below.";
        if (_wildStatus is { IsDisposed: false })
        {
            _wildStatus.Visible = true;
            _wildStatus.ForeColor = wildOk ? Theme.Green : Theme.Amber;
            _wildStatus.Text = (wildOk ? "✔  Any subdomain works (wildcard is set up)." : "⚠  New subdomains won't work yet — add the * record below.") +
                               (rootOk && wildOk ? "" : "\n     Changes at your provider can take a few minutes to show up. You can continue meanwhile.");
        }
    }

    // ---------------------------------------------------------------- 4. router

    private void RouterStep(StackPanel page)
    {
        page.Controls.Add(Title("Your router"));
        page.Controls.Add(Para("Your router is the box from your internet provider. By default it blocks everything coming in from the internet. " +
                               "You need to tell it once: \"send everything to this PC\". After that, AnyPortProxy takes over."));

        var lan = NetInfo.GetLanAddress()?.ToString() ?? "unknown";
        var gw = NetInfo.GetGatewayAddress()?.ToString();
        var card = new Card { Height = 92, Margin = new Padding(0, 8, 0, 10) };
        card.Controls.Add(new Label { Text = "This PC's address on your home network", AutoSize = true, Font = Theme.Small, ForeColor = Theme.Gray, Location = new Point(16, 12), BackColor = Color.Transparent, UseMnemonic = false });
        card.Controls.Add(new Label { Text = lan, AutoSize = true, Font = Theme.H1, Location = new Point(14, 34), BackColor = Color.Transparent, UseMnemonic = false });
        var copy = CopyButton(lan);
        copy.Location = new Point(240, 40);
        card.Controls.Add(copy);
        page.Controls.Add(card);

        page.Controls.Add(Theme.Label("Do this once:", Theme.H2));
        page.Controls.Add(Para($"1.  Open your router's settings page" + (gw is null ? "" : $" (usually http://{gw})") + ". The login is often printed on a sticker on the router."));
        page.Controls.Add(Para("2.  Look for \"DMZ\" (sometimes under Advanced, NAT or Firewall)."));
        page.Controls.Add(Para($"3.  Set the DMZ host to {lan} and save. That sends every port — TCP and UDP — to this PC."));
        page.Controls.Add(Small("No DMZ option? Use \"Port forwarding\" instead: forward ports 1–65535, protocol TCP and UDP (or \"Both\"), to " + lan + "."));
        if (gw is not null)
            page.Controls.Add(Row(Theme.Primary("🌐  Open my router's settings", (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo($"http://{gw}") { UseShellExecute = true }); } catch { }
            })));
        page.Controls.Add(Tip(CheckStatus.Info,
            "Is that safe? With DMZ, AnyPortProxy becomes the front door: dangerous ports (Remote Desktop, file sharing…) stay blocked, and port scanners are ignored. " +
            "Tip: give this PC a fixed address in the router (\"DHCP reservation\") so it never changes."));
    }

    // ---------------------------------------------------------------- 5. websites

    private void WebsitesStep(StackPanel page)
    {
        page.Controls.Add(Title("Websites"));
        page.Controls.Add(Para($"A website rule says: \"when someone visits this address, send them to that computer\". For example nas.{Domain} → your NAS. " +
                               "Your certificates (the padlock) stay on that computer — AnyPortProxy just passes the connection through."));
        page.Controls.Add(Diagram(110,
            ("🔗", $"nas.{Domain}", "address people type", Theme.Accent),
            ("🗄", "192.168.1.20", "the computer it goes to", Color.FromArgb(124, 58, 237)),
            ("🔢", "ports 80 / 443", "the normal web ports there", Theme.Green)));
        var count = new Label { AutoSize = true, Font = Theme.Bold, UseMnemonic = false, Margin = new Padding(0, 4, 0, 4) };
        void Refresh()
        {
            var sites = Websites.List(_main.Config.Proxy);
            count.Text = sites.Count == 0 ? "You have no websites yet." : "Your websites: " + string.Join(",  ", sites.Select(s => s.Host));
        }
        Refresh();
        page.Controls.Add(count);
        page.Controls.Add(Row(Theme.Primary("+  Add a website now", (_, _) =>
        {
            using var dlg = new WebsiteDialog(_main, null);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            Websites.Upsert(_main.Config.Proxy, dlg.Result, null);
            _main.SaveConfig();
            Refresh();
        })));
        page.Controls.Add(Small("Not sure of a computer's address? Look in your router's list of connected devices, or run \"ipconfig\" on that computer. " +
                                "A website on this PC itself must use other ports (like 8080), because AnyPortProxy uses 80/443."));
        page.Controls.Add(Small("You can add or change websites anytime on the Websites page."));
    }

    // ---------------------------------------------------------------- 6. games & apps

    private void GamesStep(StackPanel page)
    {
        page.Controls.Add(Title("Games & apps"));
        page.Controls.Add(Para($"Here's the magic part: every port is already forwarded. Start a game server or app on this PC, and people can connect to {Domain}:PORT straight away."));
        page.Controls.Add(Para("Use \"Open a port\" when you also want:"));
        page.Controls.Add(Para("•  devices at home to connect (it adds a Windows Firewall rule),"));
        page.Controls.Add(Para("•  your router opened automatically, if you didn't set up DMZ (UPnP),"));
        page.Controls.Add(Para("•  the fastest path for that app — connections go straight to it, and it sees players' real addresses."));
        page.Controls.Add(Row(
            Theme.Primary("🎮  Open a port for a game or app", (_, _) =>
            {
                using var dlg = new OpenPortDialog(_main);
                dlg.ShowDialog(this);
            }),
            Theme.Secondary("📡  Send a port to another computer", (_, _) =>
            {
                using var dlg = new PortMapDialog(_main);
                dlg.ShowDialog(this);
            })));
        page.Controls.Add(Tip(CheckStatus.Info,
            "Server on another computer (like WireGuard on your NAS)? Use \"Send a port to another computer\". " +
            "Dangerous ports like Remote Desktop stay blocked unless you deliberately unblock them."));
    }

    // ---------------------------------------------------------------- 7. check

    private void CheckStep(StackPanel page)
    {
        page.Controls.Add(Title("Check everything"));
        page.Controls.Add(Para("The Health check looks at the service, your firewall, your router, your DNS and your websites — and fixes most problems with one click. Try it now:"));
        var results = new ResultList { Height = 290, Dock = DockStyle.None };
        var progress = new Label { AutoSize = true, ForeColor = Theme.Gray, UseMnemonic = false, Margin = new Padding(10, 12, 0, 0) };
        Button? run = null;
        run = Theme.Primary("▶  Run the check", async (_, _) =>
        {
            run!.Enabled = false;
            results.Clear();
            var cfg = _main.Config;
            try
            {
                await Task.Run(() => Diagnostics.RunAsync(cfg,
                    r => BeginInvoke(() => { if (!results.IsDisposed && r.Status != CheckStatus.Ok) results.Add(r); }),
                    s => BeginInvoke(() => { if (!progress.IsDisposed) progress.Text = s; })));
            }
            catch (Exception ex)
            {
                results.Add(CheckResult.Fail("The check itself failed", ex.Message));
            }
            await Task.Delay(100);
            if (progress.IsDisposed) return;
            progress.Text = results.Controls.Count == 0 ? "✔ No problems found!" : "Problems and tips are listed below (things that are fine are hidden).";
            run.Enabled = true;
        });
        page.Controls.Add(Row(run, progress));
        page.Controls.Add(results);
        page.Controls.Add(Small("Test from outside your home: use your phone on mobile data (not Wi-Fi) and visit one of your addresses."));
    }

    // ---------------------------------------------------------------- 8. done

    private void DoneStep(StackPanel page)
    {
        page.Controls.Add(Title("You're ready! 🎉"));
        page.Controls.Add(Para("Here's where everything lives in the main window:"));
        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Tag = "natural", Margin = new Padding(0, 6, 0, 10) };
        void Item(string name, string what)
        {
            int row = grid.RowCount++;
            grid.Controls.Add(new Label { Text = name, Font = Theme.Bold, AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 6, 18, 6) }, 0, row);
            grid.Controls.Add(new Label { Text = what, AutoSize = true, UseMnemonic = false, MaximumSize = new Size(520, 0), Margin = new Padding(0, 6, 0, 6) }, 1, row);
        }
        Item("🏠  Home", "Status at a glance. The coloured light at the top is green when all is well.");
        Item("🌐  Websites", "Which address goes to which computer.");
        Item("🎮  Ports", "Open ports for games and apps, send ports to other computers, forwarding settings.");
        Item("🩺  Health check", "Something not working? Start here — it explains the problem and fixes most of them.");
        Item("📜  Activity", "Every connection, live. Handy to see if anyone is reaching you.");
        Item("⚙  Settings", "Your domain, flood protection, this tour again, uninstall.");
        page.Controls.Add(grid);
        page.Controls.Add(Tip(CheckStatus.Ok,
            "Prefer typing? Open a terminal and type  apx  for the same features in a menu, or  apx tour  for this tour. " +
            "Every page also has a \"How does this work?\" link."));
    }
}
