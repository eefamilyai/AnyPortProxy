using System.Net;
using AnyPortProxy.Core;

namespace AnyPortProxy.Gui;

/// <summary>Add/change a website, with live hints (address expansion, DNS check, connection test).</summary>
internal sealed class WebsiteDialog : Form
{
    private const string ThisPc = "127.0.0.1";
    private readonly MainForm _main;
    private readonly TextBox _host = new() { Font = Theme.Body, Dock = DockStyle.Fill };
    private readonly Label _hostHint = Hint("");
    private readonly TextBox _computer = new() { Font = Theme.Body, Width = 220 };
    private readonly Label _computerHint = Hint("The IP address of the computer the website runs on. Find it in your router's device list, or run \"ipconfig\" on that computer.");
    private readonly CheckBox _http = new() { Text = "http visitors (port 80) go to port", AutoSize = true, Checked = true, Margin = new Padding(0, 6, 6, 0) };
    private readonly CheckBox _https = new() { Text = "https visitors (port 443) go to port", AutoSize = true, Checked = true, Margin = new Padding(0, 6, 6, 0) };
    private readonly NumericUpDown _httpPort = new() { Minimum = 1, Maximum = 65535, Value = 80, Width = 90 };
    private readonly NumericUpDown _httpsPort = new() { Minimum = 1, Maximum = 65535, Value = 443, Width = 90 };
    private readonly Label _portHint = Hint("Most NAS boxes and servers use the normal ports 80 and 443.");
    private readonly Label _testResult = Hint("");
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 700 };
    private readonly List<RouteRule> _other;

    public Website Result { get; private set; } = new();

    public WebsiteDialog(MainForm main, Website? existing)
    {
        _main = main;
        _other = existing?.Other ?? new();
        Text = existing is null ? "Add a website" : $"Change {existing.Host}";
        Icon = Theme.AppIcon;
        Font = Theme.Body;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(660, 580);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.White;
        Padding = new Padding(22, 18, 22, 14);

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoScroll = true };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Add(Control c) => grid.Controls.Add(c);

        Add(Theme.Label("1.  The address people will type", Theme.H2));
        _host.PlaceholderText = main.Config.Domain is { Length: > 0 } d ? $"nas.{d}   (or just: nas)" : "nas.example.com";
        Add(_host);
        Add(_hostHint);

        Add(Spacer());
        Add(Theme.Label("2.  Which computer is it on?", Theme.H2));
        var compRow = new FlowLayoutPanel { AutoSizeMode = AutoSizeMode.GrowAndShrink, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _computer.PlaceholderText = "192.168.1.20";
        compRow.Controls.Add(_computer);
        compRow.Controls.Add(Theme.Secondary($"This PC ({NetInfo.GetLanAddress()?.ToString() ?? "localhost"})", (_, _) => UseThisPc()));
        Add(compRow);
        Add(_computerHint);

        Add(Spacer());
        Add(Theme.Label("3.  Which ports does the website use on that computer?", Theme.H2));
        var httpRow = new FlowLayoutPanel { AutoSizeMode = AutoSizeMode.GrowAndShrink, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        httpRow.Controls.Add(_http);
        httpRow.Controls.Add(_httpPort);
        Add(httpRow);
        var httpsRow = new FlowLayoutPanel { AutoSizeMode = AutoSizeMode.GrowAndShrink, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        httpsRow.Controls.Add(_https);
        httpsRow.Controls.Add(_httpsPort);
        Add(httpsRow);
        Add(_portHint);

        Add(Spacer());
        var testRow = new FlowLayoutPanel { AutoSizeMode = AutoSizeMode.GrowAndShrink, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        testRow.Controls.Add(Theme.Secondary("🔍  Test connection", async (_, _) => await TestAsync()));
        Add(testRow);
        Add(_testResult);

        var save = Theme.Primary(existing is null ? "Add website" : "Save changes", (_, _) => TrySave());
        var cancel = Theme.Secondary("Cancel", (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        var bottom = new FlowLayoutPanel { AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, 10, 0, 0) };
        bottom.Controls.Add(cancel);
        bottom.Controls.Add(save);
        AcceptButton = save;
        CancelButton = cancel;

        Controls.Add(grid);
        Controls.Add(bottom);

        if (existing is not null)
        {
            _host.Text = existing.Host;
            _computer.Text = existing.Computer;
            _http.Checked = existing.HttpPort is not null;
            _https.Checked = existing.HttpsPort is not null;
            _httpPort.Value = existing.HttpPort ?? 80;
            _httpsPort.Value = existing.HttpsPort ?? 443;
        }

        foreach (var label in new[] { _hostHint, _computerHint, _portHint, _testResult }) label.MaximumSize = new Size(570, 0);
        _host.TextChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); ShowHostHint(); };
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await CheckDnsAsync(); };
        _computer.TextChanged += (_, _) => ShowPortHint();
        _http.CheckedChanged += (_, _) => { _httpPort.Enabled = _http.Checked; ShowPortHint(); };
        _https.CheckedChanged += (_, _) => { _httpsPort.Enabled = _https.Checked; ShowPortHint(); };
        _httpPort.ValueChanged += (_, _) => ShowPortHint();
        _httpsPort.ValueChanged += (_, _) => ShowPortHint();
        ShowHostHint();
        ShowPortHint();
    }

    private static Label Hint(string text) => new() { UseMnemonic = false, Text = text, AutoSize = true, Font = Theme.Small, ForeColor = Theme.Gray, Margin = new Padding(0, 2, 0, 4) };

    private static Control Spacer() => new Panel { Height = 8, Margin = Padding.Empty };

    private string ExpandedHost()
    {
        var h = _host.Text.Trim().TrimEnd('.').ToLowerInvariant();
        foreach (var prefix in new[] { "http://", "https://" })
            if (h.StartsWith(prefix)) h = h[prefix.Length..];
        h = h.TrimEnd('/');
        if (h.Length > 0 && !h.Contains('.') && h != "*" && _main.Config.Domain is { Length: > 0 } d) h = $"{h}.{d}";
        return h;
    }

    private void SetHint(Label l, CheckStatus status, string text)
    {
        var (glyph, color, _) = Theme.For(status);
        l.Text = $"{glyph}  {text}";
        l.ForeColor = status == CheckStatus.Info ? Theme.Gray : color;
    }

    private void ShowHostHint()
    {
        var h = ExpandedHost();
        if (h.Length == 0)
        {
            SetHint(_hostHint, CheckStatus.Info, "For example nas.example.com. Use *.example.com to catch every address under your domain.");
            return;
        }
        if (Websites.ValidateHost(h) is { } err)
        {
            SetHint(_hostHint, CheckStatus.Fail, err);
            return;
        }
        SetHint(_hostHint, CheckStatus.Info, h != _host.Text.Trim().ToLowerInvariant() ? $"Will be saved as {h}  ·  checking the internet…" : "Checking the internet…");
    }

    private async Task CheckDnsAsync()
    {
        var h = ExpandedHost();
        if (h.Length == 0 || Websites.ValidateHost(h) is not null) return;
        if (h.Contains('*'))
        {
            SetHint(_hostHint, CheckStatus.Info, $"Catches every address ending in {h[1..]} that isn't listed separately.");
            return;
        }
        IPAddress[] addrs;
        try
        {
            addrs = await Dns.GetHostAddressesAsync(h);
        }
        catch
        {
            addrs = [];
        }
        if (h != ExpandedHost()) return; // user kept typing
        var ip = _main.PublicIp;
        if (addrs.Length == 0)
            SetHint(_hostHint, CheckStatus.Warn, $"{h} doesn't exist on the internet yet. At your domain provider, add an A record for it pointing to {ip?.ToString() ?? "your internet address"}. (You can still save it now.)");
        else if (ip is not null && addrs.Contains(ip))
            SetHint(_hostHint, CheckStatus.Ok, $"{h} points to your internet address ({ip}). 👍");
        else if (ip is not null)
            SetHint(_hostHint, CheckStatus.Warn, $"{h} points to {addrs[0]}, but your internet address is {ip}. Update the A record at your domain provider (fine if you use Cloudflare's proxy).");
        else
            SetHint(_hostHint, CheckStatus.Ok, $"{h} exists ({addrs[0]}).");
    }

    private void UseThisPc()
    {
        _computer.Text = ThisPc;
        if (_httpPort.Value == 80) _httpPort.Value = 8080;
        if (_httpsPort.Value == 443) _httpsPort.Value = 8443;
        ShowPortHint();
    }

    private bool IsLocal() => NetInfo.IsThisPc(_computer.Text.Trim());

    private void ShowPortHint()
    {
        if (IsLocal())
        {
            bool loop = (_http.Checked && _httpPort.Value == 80) || (_https.Checked && _httpsPort.Value == 443);
            SetHint(_portHint, loop ? CheckStatus.Fail : CheckStatus.Info,
                loop
                    ? "On this PC, ports 80/443 are used by AnyPortProxy itself. Set your website to use other ports (like 8080 and 8443) and enter them here."
                    : "Make sure your website on this PC is running on these ports. Untick https if it only does http.");
        }
        else
        {
            SetHint(_portHint, CheckStatus.Info, "Most NAS boxes and servers use the normal ports 80 and 443. Synology DSM uses 5000 / 5001.");
        }
    }

    private Website? Build(out string? error)
    {
        error = null;
        var host = ExpandedHost();
        var computer = _computer.Text.Trim();
        if (computer.ToLowerInvariant() is "this pc" or "localhost") computer = ThisPc;
        error = Websites.ValidateHost(host) ?? Websites.ValidateComputer(computer);
        if (error is not null) return null;
        if (!_http.Checked && !_https.Checked)
        {
            error = "Tick http, https or both.";
            return null;
        }
        if (IsLocal() && ((_http.Checked && _httpPort.Value == 80) || (_https.Checked && _httpsPort.Value == 443)))
        {
            error = "On this PC, ports 80/443 are used by AnyPortProxy itself. Use the ports your website really runs on (like 8080 / 8443).";
            return null;
        }
        return new Website
        {
            Host = host,
            Computer = computer,
            HttpPort = _http.Checked ? (int)_httpPort.Value : null,
            HttpsPort = _https.Checked ? (int)_httpsPort.Value : null,
            Other = _other,
        };
    }

    private async Task TestAsync()
    {
        var w = Build(out var error);
        if (w is null)
        {
            SetHint(_testResult, CheckStatus.Fail, error!);
            return;
        }
        SetHint(_testResult, CheckStatus.Info, "Testing…");
        var parts = new List<string>();
        bool allOk = true;
        foreach (var (label, port) in new[] { ("http", w.HttpPort), ("https", w.HttpsPort) })
        {
            if (port is not int p) continue;
            bool ok = await NetInfo.CanConnectAsync(w.Computer, p);
            allOk &= ok;
            parts.Add($"{label} → {TargetParser.Format(w.Computer, p)}: {(ok ? "answering" : "NOT answering")}");
        }
        SetHint(_testResult, allOk ? CheckStatus.Ok : CheckStatus.Warn,
            string.Join("   ·   ", parts) + (allOk ? "" : "\nIs the computer turned on, and is the website running on that port?"));
    }

    private void TrySave()
    {
        var w = Build(out var error);
        if (w is null)
        {
            MessageBox.Show(this, error, "Almost there", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Result = w;
        DialogResult = DialogResult.OK;
        Close();
    }
}
