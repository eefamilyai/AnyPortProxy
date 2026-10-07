using AnyPortProxy.Core;

namespace AnyPortProxy.Gui.Pages;

internal sealed class PortsPage : PageBase
{
    private readonly ListView _list = MakeList(("Name", 240), ("Port", 100), ("Type", 90), ("Windows Firewall", 130), ("Router", 90), ("Running now", 200));
    private readonly Banner _banner = new();
    private readonly Label _empty;
    private readonly Button _close;
    private readonly CheckBox _enabled = new() { Text = "Forward every other port to:", AutoSize = true, Font = Theme.Bold, Margin = new Padding(0, 8, 6, 0) };
    private readonly TextBox _target = new() { Width = 160 };
    private readonly TextBox _allowed = new() { Width = 200 };
    private readonly TextBox _blocked = new() { Width = 380 };
    private readonly CheckBox _lan = new() { Text = "Also redirect devices on my home network (usually leave off)", AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    private readonly CheckBox _smart = new() { Text = "Smart routing (recommended) — ignore port scanners and deliver to apps the fastest way", AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    private readonly CheckBox _udpBox = new() { Text = "Also forward UDP (games, voice chat, VPNs)", AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    private int _fillVersion;

    public PortsPage(MainForm main) : base(main)
    {
        _empty = new Label
        {
            UseMnemonic = false,
            Text = "No ports opened with the helper yet.\n\nClick \"+ Open a port for a game or app\" — pick Minecraft, Plex, a web app…\nand AnyPortProxy sets up the firewall and your router for you.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Theme.Gray,
            Font = Theme.Body,
            BackColor = Theme.Light,
        };
        var open = Theme.Primary("+  Open a port for a game or app", (_, _) => OpenPort());
        var check = Theme.Secondary("🔍  Check a port…", async (_, _) => await CheckPortAsync());
        _close = Theme.Secondary("🔒  Close selected", async (_, _) => await ClosePortAsync());
        var refresh = Theme.Secondary("↻  Refresh", (_, _) => OnShow(false));
        var portMap = Theme.Secondary("📡  Send a port to another computer…", (_, _) =>
        {
            using var dlg = new PortMapDialog(Main);
            dlg.ShowDialog(Main);
            OnShow(false);
        });
        var bar = ButtonBar(open, check, _close, portMap, refresh);
        _list.SelectedIndexChanged += (_, _) => _close.Enabled = _list.SelectedItems.Count > 0;

        // All-ports forwarding settings.
        var settings = new GroupBox { Text = "All-ports forwarding", Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Font = Theme.Bold, Padding = new Padding(12, 8, 12, 8) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Font = Theme.Body };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var targetRow = new FlowLayoutPanel { AutoSizeMode = AutoSizeMode.GrowAndShrink, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        targetRow.Controls.Add(_target);
        targetRow.Controls.Add(Theme.Secondary("This PC", (_, _) => _target.Text = "127.0.0.1"));
        grid.Controls.Add(_enabled, 0, 0);
        grid.Controls.Add(targetRow, 1, 0);
        grid.Controls.Add(new Label { UseMnemonic = false, Text = "Forwarded ports:", AutoSize = true, Margin = new Padding(20, 8, 6, 0) }, 0, 1);
        var allowedRow = new FlowLayoutPanel { AutoSizeMode = AutoSizeMode.GrowAndShrink, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        allowedRow.Controls.Add(_allowed);
        allowedRow.Controls.Add(new Label { UseMnemonic = false, Text = "e.g. 1-49151  or  3000-3999, 25565", AutoSize = true, ForeColor = Theme.Gray, Font = Theme.Small, Margin = new Padding(8, 8, 0, 0) });
        grid.Controls.Add(allowedRow, 1, 1);
        grid.Controls.Add(new Label { UseMnemonic = false, Text = "Blocked ports:", AutoSize = true, Margin = new Padding(20, 8, 6, 0) }, 0, 2);
        var blockedRow = new FlowLayoutPanel { AutoSizeMode = AutoSizeMode.GrowAndShrink, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        blockedRow.Controls.Add(_blocked);
        blockedRow.Controls.Add(Theme.Secondary("Safe defaults", (_, _) => _blocked.Text = string.Join(", ", CatchAllOptions.DefaultBlocked)));
        grid.Controls.Add(blockedRow, 1, 2);
        grid.Controls.Add(_lan, 1, 3);
        grid.Controls.Add(_smart, 1, 4);
        grid.Controls.Add(_udpBox, 1, 5);
        var save = Theme.Primary("Save forwarding settings", (_, _) => SaveSettings());
        grid.Controls.Add(save, 1, 6);
        settings.Controls.Add(grid);

        var listHost = new Panel { Dock = DockStyle.Fill };
        listHost.Controls.Add(_list);
        listHost.Controls.Add(_empty);

        _banner.Dock = DockStyle.Top;
        Controls.Add(listHost);
        Controls.Add(bar);
        Controls.Add(settings);
        Controls.Add(_banner);
        Controls.Add(Header("Ports (games & apps)",
            "Run a game server or app on this PC and let people connect. \"Open a port\" sets up everything: " +
            "AnyPortProxy, Windows Firewall and (if you want) your router.",
            "Every port is already forwarded to this PC (that's \"all-ports forwarding\"). So usually you just start your game server or app, " +
            "and people connect to yourdomain.com:PORT.\n\n" +
            "\"Open a port\" adds the extras:\n" +
            "•  a Windows Firewall rule, so devices at home can connect too,\n" +
            "•  your router opened automatically (UPnP) if you didn't set up DMZ,\n" +
            "•  the fastest path: connections go straight to the app, which sees players' real addresses.\n\n" +
            "\"Send a port to another computer\" is for servers that run elsewhere — e.g. UDP 51820 → your NAS for WireGuard.\n\n" +
            "Blocked ports (Remote Desktop, file sharing…) are never reachable from the internet unless you remove them from the list. " +
            "UDP (games, voice, VPN) is handled too."));
        listHost.BringToFront();
    }

    public override void OnShow(bool autoRun)
    {
        Main.ReloadConfig();
        var c = Main.Config;
        var ca = c.Proxy.CatchAll;
        if (ca.Enabled)
            _banner.Set(CheckStatus.Ok, $"All-ports forwarding is ON: anything you run on this PC on ports {ca.AllowedPorts} ({(ca.Udp ? "TCP and UDP" : "TCP")}) can already be reached from the internet at {c.Domain ?? Main.PublicIp?.ToString() ?? "your internet address"}:PORT — except blocked ports. Opening a port below also sets up Windows Firewall, your router and UDP.");
        else
            _banner.Set(CheckStatus.Warn, "All-ports forwarding is OFF: only the ports your router forwards straight to a computer are reachable. Turn it on below.");

        _enabled.Checked = ca.Enabled;
        _target.Text = ca.Target ?? c.CatchAllHost;
        _allowed.Text = ca.AllowedPorts;
        _blocked.Text = string.Join(", ", ca.BlockedPorts);
        _lan.Checked = ca.InterceptLan;
        _smart.Checked = ca.SmartRouting;
        _udpBox.Checked = ca.Udp;
        _ = FillAsync();
        if (autoRun) BeginInvoke(OpenPort);
    }

    private async Task FillAsync()
    {
        int version = ++_fillVersion;
        List<Listener> listeners;
        try { listeners = await Task.Run(NetInfo.GetListeners); } catch { listeners = new(); }
        if (version != _fillVersion || IsDisposed) return; // a newer refresh started
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var r in Main.Config.Ports)
        {
            var running = listeners.Where(l => l.Port >= r.Port && l.Port <= r.Last && r.Protocols().Contains(l.Protocol))
                .Select(l => l.Process).Distinct().ToList();
            var item = new ListViewItem([
                r.Name, r.Range, r.ProtocolText,
                r.Firewall ? "✔ allowed" : "–",
                r.Router ? "✔ forwarded" : "–",
                running.Count > 0 ? "✔ " + string.Join(", ", running) : "⚠ nothing yet",
            ]) { Tag = r, UseItemStyleForSubItems = false };
            item.SubItems[5].ForeColor = running.Count > 0 ? Theme.Green : Theme.Amber;
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        _empty.Visible = _list.Items.Count == 0;
        _list.Visible = !_empty.Visible;
        _close.Enabled = _list.SelectedItems.Count > 0;
    }

    private void OpenPort()
    {
        using var dlg = new OpenPortDialog(Main);
        dlg.ShowDialog(Main);
        OnShow(false);
    }

    private async Task CheckPortAsync()
    {
        var text = Ask.Text(Main, "Check a port", "Which port should I check? (for example 25565)");
        if (text is null) return;
        if (!int.TryParse(text, out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show(Main, "Type a number from 1 to 65535.", "Check a port", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var proto = Presets.ForPort(port)?.Protocol ?? PortProtocol.Tcp;
        Cursor = Cursors.WaitCursor;
        var c = Main.Config;
        var results = await Task.Run(() => PortHelper.CheckAsync(c, port, proto, checkRouter: true));
        Cursor = Cursors.Default;
        ResultsDialog.Show(Main, $"Port {port}", results);
        OnShow(false);
    }

    private async Task ClosePortAsync()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not PortRule rule) return;
        var c = Main.Config;
        bool canBlock = rule.Protocol != PortProtocol.Udp && c.Proxy.CatchAll.Enabled;
        var answer = MessageBox.Show(Main,
            $"Close {rule.Name} ({rule.Range} {rule.ProtocolText})?\n\nThis removes the Windows Firewall rule" + (rule.Router ? " and the router forward" : "") + "." +
            (canBlock ? "\n\nAlso BLOCK it completely from the internet?\n(Otherwise all-ports forwarding still lets TCP through.)\n\nYes = close and block   ·   No = just close" : ""),
            "Close port", canBlock ? MessageBoxButtons.YesNoCancel : MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (answer is DialogResult.Cancel) return;
        bool block = answer == DialogResult.Yes;
        Cursor = Cursors.WaitCursor;
        var results = await Task.Run(() => PortHelper.CloseAsync(c, rule, block));
        Cursor = Cursors.Default;
        ResultsDialog.Show(Main, "Port closed", results);
        OnShow(false);
    }

    private void SaveSettings()
    {
        var ca = Main.Config.Proxy.CatchAll;
        if (!PortRanges.TryParse(_allowed.Text, out _, out var err))
        {
            MessageBox.Show(Main, $"Forwarded ports: {err}", "Not quite", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var blocked = new List<int>();
        foreach (var part in _blocked.Text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, out var p) || p is < 1 or > 65535)
            {
                MessageBox.Show(Main, $"Blocked ports: \"{part}\" isn't a port number.", "Not quite", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            blocked.Add(p);
        }
        var target = _target.Text.Trim();
        if (Websites.ValidateComputer(target) is { } terr)
        {
            MessageBox.Show(Main, terr, "Not quite", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var unblockedDanger = ca.BlockedPorts.Except(blocked).Where(p => Presets.Warning(p) is { Risk: Risk.High }).ToList();
        if (unblockedDanger.Count > 0 && MessageBox.Show(Main,
                "You're unblocking dangerous ports:\n\n" + string.Join("\n", unblockedDanger.Select(p => $"• {p}: {Presets.Warning(p)!.Value.Why}")) + "\n\nAre you sure?",
                "Careful!", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        ca.Enabled = _enabled.Checked;
        ca.AllowedPorts = _allowed.Text.Trim();
        ca.BlockedPorts = blocked.Distinct().Order().ToList();
        ca.Target = target == Main.Config.CatchAllHost && ca.Target is null ? null : target;
        ca.InterceptLan = _lan.Checked;
        ca.SmartRouting = _smart.Checked;
        ca.Udp = _udpBox.Checked;
        if (Main.SaveConfig())
        {
            MessageBox.Show(Main, "Saved — AnyPortProxy applies it right away.", "All-ports forwarding", MessageBoxButtons.OK, MessageBoxIcon.Information);
            OnShow(false);
        }
    }
}
