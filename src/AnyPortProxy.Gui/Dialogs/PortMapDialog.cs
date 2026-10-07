using AnyPortProxy.Core;

namespace AnyPortProxy.Gui;

/// <summary>"Send a port to another computer" rules (TCP and/or UDP), with live hints.</summary>
internal sealed class PortMapDialog : Form
{
    private readonly MainForm _main;
    private readonly ListView _list;
    private readonly TextBox _port = new() { Width = 120, PlaceholderText = "51820 or 2456-2458" };
    private readonly RadioButton _tcp = new() { Text = "TCP", AutoSize = true, Checked = true };
    private readonly RadioButton _udp = new() { Text = "UDP", AutoSize = true };
    private readonly RadioButton _both = new() { Text = "Both", AutoSize = true };
    private readonly TextBox _computer = new() { Width = 180, PlaceholderText = "192.168.1.20" };
    private readonly TextBox _targetPort = new() { Width = 90, PlaceholderText = "same" };
    private readonly TextBox _name = new() { Width = 220, PlaceholderText = "e.g. WireGuard on the NAS" };
    private readonly Label _hint = new() { AutoSize = true, MaximumSize = new Size(640, 0), Font = Theme.Small, ForeColor = Theme.Gray, UseMnemonic = false, Margin = new Padding(0, 6, 0, 0) };
    private readonly Button _remove;

    public PortMapDialog(MainForm main)
    {
        _main = main;
        Text = "Send a port to another computer";
        Icon = Theme.AppIcon;
        Font = Theme.Body;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(720, 560);
        MinimumSize = new Size(640, 480);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.White;
        Padding = new Padding(18);
        MinimizeBox = false;

        var head = new Label
        {
            Text = "Connections to a port on this PC are passed on to another computer on your network — TCP, UDP or both. " +
                   "Handy for a VPN or game server on your NAS (e.g. UDP 51820 → NAS for WireGuard). Your router must forward the port to this PC.",
            Dock = DockStyle.Top,
            Height = 58,
            ForeColor = Theme.Gray,
            UseMnemonic = false,
        };

        _list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            SmallImageList = new ImageList { ImageSize = new Size(1, 28) },
        };
        _list.Columns.Add("Name", 200);
        _list.Columns.Add("Port on this PC", 130);
        _list.Columns.Add("Type", 90);
        _list.Columns.Add("Goes to", 220);
        _list.SelectedIndexChanged += (_, _) => _remove!.Enabled = _list.SelectedItems.Count > 0;

        // Add form
        var form = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Padding = new Padding(0, 10, 0, 0) };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Label L(string t) => new() { Text = t, AutoSize = true, Font = Theme.Bold, UseMnemonic = false, Margin = new Padding(0, 8, 10, 0) };
        FlowLayoutPanel Row(params Control[] cs)
        {
            var r = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
            r.Controls.AddRange(cs);
            return r;
        }
        var title = new Label { Text = "Add a rule", Font = Theme.H2, AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 0, 0, 4) };
        form.Controls.Add(title, 0, 0);
        form.SetColumnSpan(title, 2);
        form.Controls.Add(L("Port on this PC"), 0, 1);
        form.Controls.Add(Row(_port, new Label { Width = 16 }, _tcp, _udp, _both), 1, 1);
        form.Controls.Add(L("Send it to"), 0, 2);
        form.Controls.Add(Row(_computer, new Label { Text = "port", AutoSize = true, UseMnemonic = false, Margin = new Padding(10, 7, 4, 0) }, _targetPort), 1, 2);
        form.Controls.Add(L("Name"), 0, 3);
        form.Controls.Add(_name, 1, 3);
        form.Controls.Add(_hint, 0, 4);
        form.SetColumnSpan(_hint, 2);
        var add = Theme.Primary("+  Add rule", (_, _) => AddRule());
        _remove = Theme.Secondary("🗑  Remove selected", (_, _) => RemoveRule());
        _remove.Enabled = false;
        var close = Theme.Secondary("Close", (_, _) => Close());
        form.Controls.Add(Row(add, _remove, close), 1, 5);
        CancelButton = close;

        Controls.Add(_list);
        Controls.Add(form);
        Controls.Add(head);
        _list.BringToFront();

        foreach (var c in new Control[] { _port, _computer, _targetPort }) c.TextChanged += (_, _) => UpdateHint();
        foreach (var r in new[] { _tcp, _udp, _both }) r.CheckedChanged += (_, _) => UpdateHint();
        _port.Leave += (_, _) => SuggestFromPreset();
        Fill();
        UpdateHint();
    }

    private PortProtocol Protocol => _udp.Checked ? PortProtocol.Udp : _both.Checked ? PortProtocol.Both : PortProtocol.Tcp;

    private void Fill()
    {
        _list.Items.Clear();
        foreach (var f in _main.Config.Proxy.Forwards)
        {
            var problem = PortForwards.Validate(_main.Config.Proxy, f);
            var item = new ListViewItem([f.Name, f.Range, f.ProtocolText, problem is null ? f.Target : $"{f.Target}  ⚠ ignored: {problem}"]) { Tag = f };
            if (problem is not null) item.ForeColor = Theme.Amber;
            _list.Items.Add(item);
        }
    }

    private void SuggestFromPreset()
    {
        if (!PortRanges.TryParseOne(_port.Text, out var lo, out _)) return;
        var p = Presets.ForPort(lo);
        if (p is null || p.Port != lo) return;
        if (_name.Text.Length == 0) _name.Text = p.Name;
        (p.Protocol switch { PortProtocol.Udp => _udp, PortProtocol.Both => _both, _ => _tcp }).Checked = true;
    }

    private PortForward? Build(out string? error)
    {
        error = null;
        if (!PortRanges.TryParseOne(_port.Text, out var lo, out var hi))
        {
            error = "Type the port on this PC (a number from 1 to 65535, or a range like 2456-2458).";
            return null;
        }
        var computer = _computer.Text.Trim();
        if (computer.Equals("this pc", StringComparison.OrdinalIgnoreCase)) computer = "127.0.0.1";
        if (computer.Length == 0)
        {
            error = "Type the IP address of the computer to send it to (like 192.168.1.20).";
            return null;
        }
        var target = computer;
        if (_targetPort.Text.Trim().Length > 0)
        {
            if (!int.TryParse(_targetPort.Text.Trim(), out var tp) || tp is < 1 or > 65535)
            {
                error = "The destination port must be a number from 1 to 65535 (or leave it empty for the same port).";
                return null;
            }
            target = TargetParser.Format(computer, tp);
        }
        var f = new PortForward
        {
            Name = _name.Text.Trim(),
            Port = lo,
            EndPort = hi > lo ? hi : null,
            Protocol = Protocol,
            Target = target,
        };
        error = PortForwards.Validate(_main.Config.Proxy, f);
        return error is null ? f : null;
    }

    private void UpdateHint()
    {
        var f = Build(out var error);
        if (f is null)
        {
            _hint.ForeColor = _port.Text.Length == 0 || _computer.Text.Length == 0 ? Theme.Gray : Theme.Amber;
            _hint.Text = _port.Text.Length == 0 || _computer.Text.Length == 0 ? "Fill in the port and the computer." : "⚠  " + error;
            return;
        }
        var notes = new List<string> { $"✔  {f.ProtocolText} {f.Range} on this PC → {f.Target}" };
        if (Presets.Describe(f.Port) is { } d) notes.Add(d);
        if (Presets.WarningForRange(f.Port, f.Last) is { } w) notes.Add("⚠  " + w.Why);
        notes.Add("Your router must forward " + (f.HasUdp && f.HasTcp ? "TCP and UDP" : f.ProtocolText) + $" {f.Range} to this PC.");
        _hint.ForeColor = Theme.Text;
        _hint.Text = string.Join("\n", notes);
    }

    private void AddRule()
    {
        var f = Build(out var error);
        if (f is null)
        {
            MessageBox.Show(this, error, "Almost there", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (Presets.WarningForRange(f.Port, f.Last) is { Risk: Risk.High } w &&
            MessageBox.Show(this, $"{w.Why}\n\nSend it anyway?", "Careful", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        _main.Config.Proxy.Forwards.Add(f);
        if (!_main.SaveConfig())
        {
            _main.Config.Proxy.Forwards.Remove(f);
            return;
        }
        _port.Text = _computer.Text = _targetPort.Text = _name.Text = "";
        Fill();
        UpdateHint();
    }

    private void RemoveRule()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not PortForward f) return;
        if (MessageBox.Show(this, $"Remove the rule for {f.ProtocolText} {f.Range} → {f.Target}?", "Remove rule",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _main.Config.Proxy.Forwards.Remove(f);
        _main.SaveConfig();
        Fill();
    }
}
