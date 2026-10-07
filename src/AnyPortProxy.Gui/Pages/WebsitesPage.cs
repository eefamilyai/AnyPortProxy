using AnyPortProxy.Core;

namespace AnyPortProxy.Gui.Pages;

internal sealed class WebsitesPage : PageBase
{
    private readonly ListView _list = MakeList(("Address people type", 280), ("http (port 80) goes to", 210), ("https (port 443) goes to", 210), ("Test", 190));
    private readonly Label _empty;
    private readonly TextBox _default = new() { Width = 220, Font = Theme.Body };
    private readonly Button _edit, _remove;

    public WebsitesPage(MainForm main) : base(main)
    {
        _empty = new Label
        {
            UseMnemonic = false,
            Text = "No websites yet.\n\nClick \"+ Add website\" to send an address like nas.yourdomain.com\nto one of your computers.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Theme.Gray,
            Font = Theme.H2,
            BackColor = Theme.Light,
        };

        var add = Theme.Primary("+  Add website", (_, _) => AddOrEdit(null));
        _edit = Theme.Secondary("✎  Change", (_, _) => AddOrEdit(Selected()));
        _remove = Theme.Secondary("🗑  Remove", (_, _) => Remove());
        var test = Theme.Secondary("🔍  Test all", async (_, _) => await TestAllAsync());
        var bar = ButtonBar(add, _edit, _remove, test);

        var defaultRow = new FlowLayoutPanel { AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 14, 0, 0), WrapContents = true };
        defaultRow.Controls.Add(new Label { UseMnemonic = false, Text = "Addresses that aren't in the list go to:", AutoSize = true, Margin = new Padding(0, 9, 8, 0) });
        defaultRow.Controls.Add(_default);
        var thisPc = Theme.Secondary("This PC", (_, _) => _default.Text = "127.0.0.1");
        defaultRow.Controls.Add(thisPc);
        defaultRow.Controls.Add(Theme.Primary("Save", (_, _) => SaveDefault()));
        defaultRow.Controls.Add(Theme.Wrap("Usually leave this alone. Visitors to an unknown address are sent here.", Theme.Small, Theme.Gray));

        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.DoubleClick += (_, _) => AddOrEdit(Selected());

        var listHost = new Panel { Dock = DockStyle.Fill };
        listHost.Controls.Add(_list);
        listHost.Controls.Add(_empty);

        Controls.Add(listHost);
        Controls.Add(bar);
        Controls.Add(defaultRow);
        Controls.Add(Header("Websites",
            "When someone types one of these addresses in their browser, AnyPortProxy sends them to the computer you chose. " +
            "Works for both http and https — your certificates stay on that computer."));
        listHost.BringToFront();
    }

    public override void OnShow(bool autoRun)
    {
        Main.ReloadConfig();
        Fill();
        _default.Text = Main.Config.Proxy.DefaultTarget;
    }

    private void Fill()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var w in Websites.List(Main.Config.Proxy))
        {
            var item = new ListViewItem([w.Host, w.HttpText, w.HttpsText, ""]) { Tag = w };
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        _empty.Visible = _list.Items.Count == 0;
        _list.Visible = !_empty.Visible;
        UpdateButtons();
    }

    private Website? Selected() => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as Website : null;

    private void UpdateButtons() => _edit.Enabled = _remove.Enabled = Selected() is not null;

    private void AddOrEdit(Website? existing)
    {
        using var dlg = new WebsiteDialog(Main, existing);
        if (dlg.ShowDialog(Main) != DialogResult.OK) return;
        Websites.Upsert(Main.Config.Proxy, dlg.Result, existing?.Host);
        if (Main.SaveConfig()) Fill();
    }

    private void Remove()
    {
        if (Selected() is not { } w) return;
        if (MessageBox.Show(Main, $"Remove {w.Host}?\n\nVisitors to that address will go to the default computer instead.", "Remove website",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        Websites.Remove(Main.Config.Proxy, w.Host);
        if (Main.SaveConfig()) Fill();
    }

    private void SaveDefault()
    {
        var t = _default.Text.Trim();
        if (!TargetParser.TryParse(t, out _, out _))
        {
            MessageBox.Show(Main, "Type an IP address like 192.168.1.20, or click \"This PC\".", "Not valid", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Main.Config.Proxy.DefaultTarget = t;
        if (Main.SaveConfig()) MessageBox.Show(Main, "Saved.", "Websites", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task TestAllAsync()
    {
        foreach (ListViewItem item in _list.Items)
        {
            var w = (Website)item.Tag!;
            item.SubItems[3].Text = "testing…";
            var parts = new List<string>();
            foreach (var (label, port) in new[] { ("http", w.HttpPort), ("https", w.HttpsPort) })
            {
                if (port is not int p) continue;
                parts.Add((await NetInfo.CanConnectAsync(w.Computer, p) ? "✔ " : "✖ ") + label);
            }
            item.SubItems[3].Text = string.Join("   ", parts);
            item.ForeColor = parts.Any(x => x.StartsWith('✖')) ? Theme.Red : Theme.Green;
        }
        if (_list.Items.Cast<ListViewItem>().Any(i => i.ForeColor == Theme.Red))
            MessageBox.Show(Main, "Some computers didn't answer (✖).\n\nCheck that the computer is turned on and the website is running on that port.",
                "Test results", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
