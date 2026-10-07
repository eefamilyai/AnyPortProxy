using AnyPortProxy.Core;

namespace AnyPortProxy.Gui;

/// <summary>Pick a game/app (or type a port), see smart hints, then open it everywhere in one click.</summary>
internal sealed class OpenPortDialog : Form
{
    private const string Custom = "✏  Something else (I'll type the port)";
    private readonly MainForm _main;
    private readonly TextBox _search = new() { Dock = DockStyle.Top, PlaceholderText = "🔎  Search (e.g. minecraft, plex, 7777)", Font = Theme.Body };
    private readonly ListBox _presets = new() { Dock = DockStyle.Fill, Font = Theme.Body, IntegralHeight = false, ItemHeight = 26, DrawMode = DrawMode.OwnerDrawFixed, BorderStyle = BorderStyle.FixedSingle };
    private readonly TextBox _name = new() { Width = 330, Font = Theme.Body };
    private readonly TextBox _port = new() { Width = 140, Font = Theme.Body, PlaceholderText = "25565 or 2456-2458" };
    private readonly RadioButton _tcp = new() { Text = "TCP", AutoSize = true, Checked = true };
    private readonly RadioButton _udp = new() { Text = "UDP", AutoSize = true };
    private readonly RadioButton _both = new() { Text = "Both", AutoSize = true };
    private readonly CheckBox _firewall = new() { Text = "Windows Firewall — let devices on my home network connect", AutoSize = true, Checked = true };
    private readonly CheckBox _router = new() { Text = "My router — ask it to forward this port automatically (UPnP)", AutoSize = true, Checked = true };
    private readonly FlowLayoutPanel _hints = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Dock = DockStyle.Fill };

    // "Give this server its own address" (several servers on one port, told apart by the address players type)
    private readonly CheckBox _byAddress = new() { Text = "🏷  Give this server its own address  (lets several servers share this port)", AutoSize = true, Font = Theme.Bold, Margin = new Padding(0, 10, 0, 2), Visible = false };
    private readonly TextBox _address = new() { Width = 150, Font = Theme.Body, PlaceholderText = "mc2" };
    private readonly Label _domainSuffix = new() { AutoSize = true, UseMnemonic = false, ForeColor = Theme.Gray, Margin = new Padding(2, 7, 0, 0) };
    private readonly TextBox _addrComputer = new() { Width = 140, Font = Theme.Body, PlaceholderText = "192.168.1.30" };
    private readonly TextBox _addrPort = new() { Width = 70, Font = Theme.Body };
    private readonly FlowLayoutPanel _addrRow1 = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(20, 2, 0, 0), Visible = false };
    private readonly FlowLayoutPanel _addrRow2 = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(20, 2, 0, 0), Visible = false };
    private readonly Button _open;
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 400 };
    private int _hintVersion;

    public OpenPortDialog(MainForm main)
    {
        _main = main;
        Text = "Open a port for a game or app";
        Icon = Theme.AppIcon;
        Font = Theme.Body;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(980, 700);
        MinimumSize = new Size(880, 620);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.White;
        Padding = new Padding(18);
        MinimizeBox = false;

        // Left: what are you running?
        var left = new Panel { Dock = DockStyle.Left, Width = 330, Padding = new Padding(0, 0, 16, 0) };
        var leftTitle = new Label { UseMnemonic = false, Text = "1.  What do you want to run?", Font = Theme.H2, Dock = DockStyle.Top, Height = 34 };
        left.Controls.Add(_presets);
        left.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6 });
        left.Controls.Add(_search);
        left.Controls.Add(leftTitle);
        _presets.DrawItem += DrawPreset;

        // Right: details + hints.
        var right = new Panel { Dock = DockStyle.Fill };
        var form = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var title2 = new Label { UseMnemonic = false, Text = "2.  Details", Font = Theme.H2, AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
        form.Controls.Add(title2, 0, 0);
        form.SetColumnSpan(title2, 2);
        form.Controls.Add(FieldLabel("Name"), 0, 1);
        form.Controls.Add(_name, 1, 1);
        form.Controls.Add(FieldLabel("Port"), 0, 2);
        form.Controls.Add(_port, 1, 2);
        form.Controls.Add(FieldLabel("Type"), 0, 3);
        var protoRow = new FlowLayoutPanel { AutoSizeMode = AutoSizeMode.GrowAndShrink, AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
        protoRow.Controls.AddRange([_tcp, _udp, _both, new Label { UseMnemonic = false, Text = "Not sure? Keep the suggestion.", AutoSize = true, ForeColor = Theme.Gray, Font = Theme.Small, Margin = new Padding(10, 5, 0, 0) }]);
        form.Controls.Add(protoRow, 1, 3);
        var title3 = new Label { UseMnemonic = false, Text = "3.  Also set up", Font = Theme.H2, AutoSize = true, Margin = new Padding(0, 14, 0, 4) };
        form.Controls.Add(title3, 0, 4);
        form.SetColumnSpan(title3, 2);
        form.Controls.Add(_firewall, 0, 5);
        form.SetColumnSpan(_firewall, 2);
        form.Controls.Add(_router, 0, 6);
        form.SetColumnSpan(_router, 2);
        var routerHint = new Label { UseMnemonic = false, Text = "      Skip this if you've already forwarded all ports to this PC in your router's settings.", AutoSize = true, ForeColor = Theme.Gray, Font = Theme.Small };
        form.Controls.Add(routerHint, 0, 7);
        form.SetColumnSpan(routerHint, 2);

        Label Small(string t, int left = 8) => new() { Text = t, AutoSize = true, UseMnemonic = false, Margin = new Padding(left, 7, 4, 0) };
        _domainSuffix.Text = "." + (string.IsNullOrWhiteSpace(main.Config.Domain) ? "yourdomain.com" : main.Config.Domain);
        _addrRow1.Controls.AddRange([Small("Address", 0), _address, _domainSuffix]);
        var thisPc = Theme.Secondary("This PC", (_, _) =>
        {
            _addrComputer.Text = "127.0.0.1";
            if (PortRanges.TryParseOne(_port.Text, out var p, out _) && (!int.TryParse(_addrPort.Text, out var cur) || cur == p))
                _addrPort.Text = (p + 1).ToString(); // AnyPortProxy answers on the shared port, so the server moves up one
        });
        thisPc.Margin = new Padding(4, 0, 0, 0);
        _addrRow2.Controls.AddRange([Small("Runs on", 0), _addrComputer, thisPc, Small("port"), _addrPort]);
        form.Controls.Add(_byAddress, 0, 8);
        form.SetColumnSpan(_byAddress, 2);
        form.Controls.Add(_addrRow1, 0, 9);
        form.SetColumnSpan(_addrRow1, 2);
        form.Controls.Add(_addrRow2, 0, 10);
        form.SetColumnSpan(_addrRow2, 2);
        _byAddress.CheckedChanged += (_, _) =>
        {
            _addrRow1.Visible = _addrRow2.Visible = _byAddress.Checked;
            _firewall.Enabled = !_byAddress.Checked; // AnyPortProxy itself answers on the shared port
            if (_byAddress.Checked && _addrPort.Text.Length == 0 && PortRanges.TryParseOne(_port.Text, out var p, out _)) _addrPort.Text = p.ToString();
            _open!.Text = _byAddress.Checked ? "Add server" : "Open port"; // handler only runs after construction
            Schedule();
        };
        foreach (var tb in new[] { _address, _addrComputer, _addrPort }) tb.TextChanged += (_, _) => Schedule();

        var hintsTitle = new Label { UseMnemonic = false, Text = "💡  What I noticed", Font = Theme.H2, Dock = DockStyle.Top, Height = 40, Padding = new Padding(0, 12, 0, 0) };
        var hintsCard = new Card { Dock = DockStyle.Fill, Fill = Theme.Light, Edge = Theme.Border, Padding = new Padding(12) };
        hintsCard.BackColor = Theme.Light;
        _hints.BackColor = Theme.Light;
        hintsCard.Controls.Add(_hints);
        right.Controls.Add(hintsCard);
        right.Controls.Add(hintsTitle);
        right.Controls.Add(form);

        _open = Theme.Primary("Open port", async (_, _) => await OpenAsync());
        _open.Font = Theme.H2;
        var cancel = Theme.Secondary("Cancel", (_, _) => Close());
        var bottom = new FlowLayoutPanel { AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, 12, 0, 0) };
        bottom.Controls.Add(cancel);
        bottom.Controls.Add(_open);
        CancelButton = cancel;

        Controls.Add(right);
        Controls.Add(left);
        Controls.Add(bottom);

        FillPresets("");
        _search.TextChanged += (_, _) => FillPresets(_search.Text);
        _presets.SelectedIndexChanged += (_, _) => PresetChosen();
        foreach (var c in new Control[] { _port, _name }) c.TextChanged += (_, _) => Schedule();
        foreach (var r in new[] { _tcp, _udp, _both }) r.CheckedChanged += (_, _) => Schedule();
        _router.CheckedChanged += (_, _) => Schedule();
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await UpdateHintsAsync(); };
        Shown += (_, _) => { _search.Focus(); Schedule(); };
    }

    private static Label FieldLabel(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 8, 12, 4), Font = Theme.Bold };

    private void FillPresets(string search)
    {
        _presets.BeginUpdate();
        _presets.Items.Clear();
        _presets.Items.Add(Custom);
        foreach (var p in Presets.Search(search)) _presets.Items.Add(p);
        _presets.EndUpdate();
    }

    private void DrawPreset(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var item = _presets.Items[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using var back = new SolidBrush(selected ? Theme.AccentLight : Color.White);
        e.Graphics.FillRectangle(back, e.Bounds);
        var nameRect = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 120, e.Bounds.Height);
        var portRect = new Rectangle(e.Bounds.Right - 116, e.Bounds.Y, 110, e.Bounds.Height);
        const TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        if (item is PortPreset p)
        {
            var color = p.Risk == Risk.High ? Theme.Red : Theme.Text;
            TextRenderer.DrawText(e.Graphics, p.Name, Theme.Body, nameRect, color, flags);
            TextRenderer.DrawText(e.Graphics, $"{p.Range} {p.ProtocolText}", Theme.Small, portRect, Theme.Gray, flags | TextFormatFlags.Right);
        }
        else
        {
            TextRenderer.DrawText(e.Graphics, item.ToString(), Theme.Bold, nameRect, Theme.Accent, flags);
        }
    }

    private void PresetChosen()
    {
        switch (_presets.SelectedItem)
        {
            case PortPreset p:
                _name.Text = p.Name;
                _port.Text = p.Range;
                (p.Protocol switch { PortProtocol.Udp => _udp, PortProtocol.Both => _both, _ => _tcp }).Checked = true;
                break;
            case string:
                _name.Text = "";
                _port.Text = "";
                _tcp.Checked = true;
                _port.Focus();
                break;
        }
        Schedule();
    }

    private PortProtocol Protocol => _udp.Checked ? PortProtocol.Udp : _both.Checked ? PortProtocol.Both : PortProtocol.Tcp;

    private void Schedule()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void AddHint(CheckStatus status, string text)
    {
        var (glyph, color, _) = Theme.For(status);
        var label = new Label
        {
            UseMnemonic = false,
            Text = $"{glyph}  {text}",
            AutoSize = true,
            ForeColor = status is CheckStatus.Ok or CheckStatus.Info ? Theme.Text : color,
            Font = status is CheckStatus.Fail or CheckStatus.Warn ? Theme.Bold : Theme.Body,
            MaximumSize = new Size(Math.Max(200, _hints.ClientSize.Width - 30), 0),
            Margin = new Padding(0, 0, 0, 8),
            BackColor = Theme.Light,
        };
        _hints.Controls.Add(label);
    }

    private async Task UpdateHintsAsync()
    {
        int version = ++_hintVersion;
        _hints.SuspendLayout();
        _hints.Controls.Clear();
        _open.Enabled = false;

        if (!PortRanges.TryParseOne(_port.Text, out var lo, out var hi))
        {
            AddHint(CheckStatus.Info, _port.Text.Trim().Length == 0
                ? "Pick a game or app on the left, or choose \"Something else\" and type a port number."
                : "Type a port number from 1 to 65535, or a range like 2456-2458.");
            _hints.ResumeLayout();
            return;
        }

        var c = _main.Config;
        var preset = _presets.SelectedItem as PortPreset;
        bool custom = _presets.SelectedItem is string || preset is null || preset.Port != lo;
        // Offer "its own address" only where it can work: games/apps that send the address (or a custom TCP port).
        bool canShare = lo == hi && Protocol == PortProtocol.Tcp && (custom || preset!.ByAddress) && lo is not (80 or 443);
        _byAddress.Visible = canShare;
        if (!canShare && _byAddress.Checked) _byAddress.Checked = false;

        if (_byAddress.Checked)
        {
            _hints.ResumeLayout();
            await UpdateAddressHintsAsync(lo, preset, custom, version);
            return;
        }

        if (PortHelper.Validate(c, lo, hi, Protocol) is { } error)
        {
            AddHint(CheckStatus.Fail, error);
            _hints.ResumeLayout();
            return;
        }
        _open.Enabled = true;

        if (preset is not null && preset.Port == lo) AddHint(CheckStatus.Info, preset.Description);
        else if (Presets.Describe(lo) is { } d) AddHint(CheckStatus.Info, d);

        if (preset is not null && preset.Port == lo)
        {
            if (preset.ByAddress)
                AddHint(CheckStatus.Info, $"Several {preset.Name} servers? Tick \"Give this server its own address\" — players type e.g. mc2{_domainSuffix.Text} and each goes to its own server, all on port {lo}.");
            else
                AddHint(CheckStatus.Info, GameAddresses.WhyNot(preset.Name, lo));
        }

        if (Presets.WarningForRange(lo, hi) is { } w)
            AddHint(w.Risk == Risk.High ? CheckStatus.Fail : CheckStatus.Warn, (w.Risk == Risk.High ? "Dangerous: " : "Careful: ") + w.Why);

        var ca = c.Proxy.CatchAll;
        if (Protocol != PortProtocol.Udp)
        {
            if (!ca.Enabled) AddHint(CheckStatus.Info, "All-ports forwarding is off, so your router must forward this port straight to this PC.");
            else if (ca.BlockedPorts.Any(b => b >= lo && b <= hi)) AddHint(CheckStatus.Warn, "This port is currently blocked. Opening it will unblock it.");
            else if (PortRanges.ContainsAll(ca.AllowedPorts, lo, hi)) AddHint(CheckStatus.Ok, "Internet visitors can already reach this TCP port through AnyPortProxy.");
            else AddHint(CheckStatus.Info, "I'll add it to the forwarded ports.");
        }
        if (Protocol != PortProtocol.Tcp)
            AddHint(CheckStatus.Info, "UDP: apps listening on all interfaces get it directly (the firewall rule matters for those); localhost-only apps are relayed. Your router must forward UDP too.");
        if (_router.Checked && hi - lo + 1 > PortHelper.MaxRouterPorts)
            AddHint(CheckStatus.Warn, $"That's more than {PortHelper.MaxRouterPorts} ports — the router step will be skipped; forward them by hand.");
        _hints.ResumeLayout();

        var protocol = Protocol;
        var listening = await Task.Run(() => PortHelper.ListeningChecks(lo, hi, protocol));
        if (version != _hintVersion || IsDisposed) return;
        foreach (var r in listening) AddHint(r.Status, r.Title + (r.Detail is null ? "" : $" — {r.Detail}"));
        AddHint(CheckStatus.Info, $"People will connect to:  {_main.ConnectAddress(lo)}");
    }

    private string AddressHost => Websites.Expand(_main.Config.Domain, _address.Text);

    private string AddressTarget(int port)
    {
        var computer = _addrComputer.Text.Trim();
        int sp = int.TryParse(_addrPort.Text.Trim(), out var v) ? v : port;
        return TargetParser.Format(computer, sp == port ? null : sp);
    }

    private async Task UpdateAddressHintsAsync(int port, PortPreset? preset, bool custom, int version)
    {
        var c = _main.Config;
        var host = AddressHost;
        if (_address.Text.Trim().Length == 0 || _addrComputer.Text.Trim().Length == 0)
        {
            AddHint(CheckStatus.Info, $"Type the address players will use (e.g. mc2 → mc2{_domainSuffix.Text}) and the computer the server runs on.");
            return;
        }
        if (!int.TryParse(_addrPort.Text.Trim(), out var serverPort) || serverPort is < 1 or > 65535)
        {
            AddHint(CheckStatus.Fail, "The server's port must be a number from 1 to 65535.");
            return;
        }
        var target = AddressTarget(port);
        if (GameAddresses.Validate(c.Proxy, host, port, target) is { } error)
        {
            AddHint(CheckStatus.Fail, error);
            return;
        }
        _open.Enabled = true;

        AddHint(CheckStatus.Ok, $"Players type:  {GameAddresses.ConnectAddress(host, port)}   →   {TargetParser.Format(_addrComputer.Text.Trim(), serverPort)}");
        if (custom)
            AddHint(CheckStatus.Warn, "This only works if the app speaks Minecraft Java, HTTPS or HTTP — those send the address. Other games can't share a port by address.");

        var others = GameAddresses.List(c.Proxy).Where(g => g.Port == port).ToList();
        if (others.Count == 0)
            AddHint(CheckStatus.Info, $"First server on port {port}: it also gets players who use your IP address or a name you haven't added.");
        else
            AddHint(CheckStatus.Info, $"Already on port {port}: " + string.Join(",  ", others.Select(g => (g.IsFallback ? "anyone else" : g.Host) + " → " + g.Target)));

        bool isThisPc = NetInfo.IsThisPc(_addrComputer.Text.Trim());
        bool minecraft = preset?.Name.StartsWith("Minecraft Java") == true || port == 25565;
        if (isThisPc)
            AddHint(CheckStatus.Info, $"Run the server on this PC on port {serverPort}" + (minecraft ? $" (in server.properties: server-port={serverPort})." : "."));
        if (!_router.Checked) AddHint(CheckStatus.Info, $"Your router must send port {port} to this PC (DMZ or a port forward).");
        AddHint(CheckStatus.Info, $"{host} must exist in DNS — one *{_domainSuffix.Text} record covers every server name.");

        // Is a server still sitting on the shared port on this PC? It has to move.
        var listeners = await Task.Run(NetInfo.GetListeners);
        if (version != _hintVersion || IsDisposed) return;
        var squatter = listeners.FirstOrDefault(l => l.Protocol == "TCP" && l.Port == port && l.Process != "AnyPortProxy");
        if (squatter is not null)
            AddHint(CheckStatus.Warn, $"{squatter.Process} is using port {port} on this PC right now. Move it to another port (like {port + 1})" +
                                      (minecraft ? $" — set server-port={port + 1} in server.properties —" : "") +
                                      $" and add it as an address that runs on This PC, port {port + 1}.");
        var target2 = TargetParser.TryParse(target, out var th, out var tp) ? (th, tp ?? port) : (target, port);
        bool answering = await NetInfo.CanConnectAsync(target2.Item1, target2.Item2, 1500);
        if (version != _hintVersion || IsDisposed) return;
        AddHint(answering ? CheckStatus.Ok : CheckStatus.Warn, answering
            ? $"The server at {TargetParser.Format(target2.Item1, target2.Item2)} is answering."
            : $"Nothing answers at {TargetParser.Format(target2.Item1, target2.Item2)} yet — start the server there (you can add it now anyway).");
    }

    private async Task OpenAsync()
    {
        if (!PortRanges.TryParseOne(_port.Text, out var lo, out var hi)) return;
        if (_byAddress.Checked)
        {
            _open.Enabled = false;
            _open.Text = "Working…";
            UseWaitCursor = true;
            var cfg = _main.Config;
            var name = _name.Text.Trim();
            var host = AddressHost;
            var target = AddressTarget(lo);
            bool router = _router.Checked;
            var res = await Task.Run(() => PortHelper.AddGameAddressAsync(cfg, name.Length > 0 ? name : host, host, lo, target, router));
            UseWaitCursor = false;
            ResultsDialog.Show(this, $"Added {host}", res);
            Close();
            return;
        }
        if (Presets.WarningForRange(lo, hi) is { Risk: Risk.High } w &&
            MessageBox.Show(this, $"{w.Why}\n\nAre you really sure you want to open this to the whole internet?", "Dangerous port",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        var req = new OpenPortRequest
        {
            Name = _name.Text.Trim(),
            Port = lo,
            EndPort = hi > lo ? hi : null,
            Protocol = Protocol,
            Firewall = _firewall.Checked,
            Router = _router.Checked,
        };
        _open.Enabled = false;
        _open.Text = "Working…";
        UseWaitCursor = true;
        var c = _main.Config;
        var results = await Task.Run(() => PortHelper.OpenAsync(c, req));
        UseWaitCursor = false;
        ResultsDialog.Show(this, $"Opened {req.Name}", results);
        Close();
    }
}
