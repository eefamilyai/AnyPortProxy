using AnyPortProxy.Core;

namespace AnyPortProxy.Gui.Pages;

internal sealed class HomePage : PageBase
{
    private readonly StackPanel _stack = new();
    private ServiceState? _shownFor;

    public HomePage(MainForm main) : base(main)
    {
        Controls.Add(_stack);
        main.StatusChanged += (_, _) =>
        {
            if (Parent is not null && _shownFor != Main.State && (_shownFor == ServiceState.NotInstalled || Main.State == ServiceState.NotInstalled))
                Build();
        };
    }

    public override void OnShow(bool autoRun) => Build();

    private void Build()
    {
        _shownFor = Main.State;
        _stack.SuspendLayout();
        _stack.Controls.Clear();
        if (Main.State == ServiceState.NotInstalled) BuildWelcome();
        else BuildDashboard();
        _stack.ResumeLayout();
    }

    private void BuildWelcome()
    {
        _stack.Controls.Add(Theme.Label("Welcome to AnyPortProxy 👋", Theme.H1));
        _stack.Controls.Add(Theme.Wrap("AnyPortProxy lets people on the internet reach things running on your computers.", Theme.Body, Theme.Gray));

        var what = new Card { Height = 150 };
        var inner = new StackPanel { AutoScroll = false, BackColor = Color.Transparent };
        inner.Controls.Add(Theme.Wrap("🌐   Websites — send nas.yourdomain.com to your NAS, and theo.yourdomain.com to another computer.", Theme.Body));
        inner.Controls.Add(Theme.Wrap("🎮   Games & apps — start anything on this PC (like a Minecraft server) and friends can join at yourdomain.com:PORT.", Theme.Body));
        inner.Controls.Add(Theme.Wrap("🔒   Safe defaults — dangerous ports (Remote Desktop, file sharing…) stay blocked.", Theme.Body));
        what.Controls.Add(inner);
        _stack.Controls.Add(what);

        _stack.Controls.Add(Theme.Label("Your domain name (optional)", Theme.H2));
        _stack.Controls.Add(Theme.Wrap("The domain you own, like example.com. You can add it later in Settings.", Theme.Small, Theme.Gray));
        var domain = new TextBox { Text = Main.Config.Domain ?? "", PlaceholderText = "example.com", Font = Theme.Body, Tag = "natural", Width = 320 };
        _stack.Controls.Add(domain);

        var install = Theme.Primary("Install & start AnyPortProxy");
        install.Font = Theme.H2;
        install.Padding = new Padding(18, 8, 18, 8);
        install.Margin = new Padding(0, 16, 0, 4);
        install.Tag = "natural";
        install.Click += async (_, _) =>
        {
            if (await Main.InstallAsync())
            {
                if (!string.IsNullOrWhiteSpace(domain.Text))
                {
                    Main.Config.Domain = domain.Text.Trim().TrimEnd('.').ToLowerInvariant();
                    Main.SaveConfig();
                }
                MessageBox.Show(Main, "AnyPortProxy is running! 🎉\n\nNext: add your websites (like nas.yourdomain.com), or open a port for a game.",
                    "Installed", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Main.ShowPage("websites");
            }
        };
        _stack.Controls.Add(install);
        _stack.Controls.Add(Theme.Wrap("Windows will run it in the background and start it automatically when the PC turns on.", Theme.Small, Theme.Gray));
    }

    private void BuildDashboard()
    {
        _stack.Controls.Add(Theme.Label("Home", Theme.H1));
        _stack.Controls.Add(Theme.Wrap("Here's what's set up. Click a button to change anything.", Theme.Body, Theme.Gray));

        // Width comes from the page; height follows how many rows the cards wrap into.
        var cards = new FlowLayoutPanel { WrapContents = true, Margin = new Padding(0, 8, 0, 0), Height = 200 };
        int lastWidth = -1;
        cards.SizeChanged += (_, _) =>
        {
            if (cards.Width == lastWidth) return;
            lastWidth = cards.Width;
            cards.Height = cards.GetPreferredSize(new Size(cards.Width, 0)).Height;
        };
        var c = Main.Config;
        int sites = Websites.List(c.Proxy).Count;
        cards.Controls.Add(MakeCard("🌐  Websites",
            sites == 0 ? "No websites yet. Send an address like nas.yourdomain.com to one of your computers." : $"{sites} address{(sites == 1 ? "" : "es")} set up.",
            sites == 0 ? "Add your first website" : "Manage websites", () => Main.ShowPage("websites")));
        cards.Controls.Add(MakeCard("🎮  Games & apps",
            (c.Proxy.CatchAll.Enabled ? "Every other port on this PC is reachable from the internet." : "All-ports forwarding is off.") +
            (c.Ports.Count > 0 ? $" {c.Ports.Count} port(s) opened with the helper." : ""),
            "Open a port for a game or app", () => Main.ShowPage("ports", autoRun: true)));
        cards.Controls.Add(MakeCard("🩺  Health check",
            "Finds common problems (router, firewall, DNS, busy ports) and fixes most of them with one click.",
            "Run health check", () => Main.ShowPage("health", autoRun: true)));

        var addressText = c.Domain is { Length: > 0 }
            ? $"Your domain: {c.Domain}\nFriends connect to {c.Domain}:PORT"
            : Main.PublicIp is not null ? $"Your internet address: {Main.PublicIp}\nTip: set your domain in Settings." : "Looking up your internet address…";
        cards.Controls.Add(MakeCard("🔗  Your address", addressText, "Copy address", () =>
        {
            var text = c.Domain ?? Main.PublicIp?.ToString();
            if (text is null) return;
            Clipboard.SetText(text);
            MessageBox.Show(Main, $"Copied \"{text}\" to the clipboard.", "Copied", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }));
        _stack.Controls.Add(cards);

        _stack.Controls.Add(Theme.Label("Tips", Theme.H2));
        _stack.Controls.Add(Theme.Wrap("•  Sharing a game server? Start it on this PC, then go to Ports → \"Open a port\".", Theme.Body));
        _stack.Controls.Add(Theme.Wrap("•  Website on another computer? Websites → \"Add website\" and type that computer's IP address.", Theme.Body));
        _stack.Controls.Add(Theme.Wrap("•  Something not working? The Health check finds most problems and can fix them for you.", Theme.Body));
        _stack.Controls.Add(Theme.Wrap("•  Prefer typing? Open a terminal and type  apx  — everything here works there too.", Theme.Body));
    }

    private static Card MakeCard(string title, string text, string button, Action click)
    {
        var card = new Card { Width = 330, Height = 178, Tag = "natural" };
        card.Controls.Add(new Label { UseMnemonic = false, Text = title, Font = Theme.H2, AutoSize = true, Location = new Point(16, 14), BackColor = Color.Transparent });
        card.Controls.Add(new Label
        {
            UseMnemonic = false,
            Text = text,
            Font = Theme.Body,
            ForeColor = Theme.Gray,
            Location = new Point(16, 48),
            Size = new Size(298, 72),
            BackColor = Color.Transparent,
        });
        var b = Theme.Primary(button, (_, _) => click());
        b.Location = new Point(16, 128);
        card.Controls.Add(b);
        return card;
    }
}
