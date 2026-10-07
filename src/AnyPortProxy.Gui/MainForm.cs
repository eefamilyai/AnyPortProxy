using System.Diagnostics;
using System.Net;
using AnyPortProxy.Core;
using AnyPortProxy.Gui.Pages;

namespace AnyPortProxy.Gui;

internal sealed class MainForm : Form
{
    private readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = Color.White };
    private readonly Label _dot = new() { Text = "●", Font = new Font("Segoe UI", 26f), AutoSize = true, ForeColor = Theme.Gray, Margin = new Padding(0, 0, 10, 0) };
    private readonly Label _title = Theme.Label("Checking…", Theme.StatusTitle);
    private readonly Label _subtitle = Theme.Label("", Theme.Body, Theme.Gray);
    private readonly Button _primary = Theme.Primary("…");
    private readonly Button _secondary = Theme.Secondary("Restart");
    private readonly Dictionary<string, Button> _nav = new();
    private readonly Dictionary<string, PageBase> _pages = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private PageBase? _current;
    private bool _busy;

    public AppConfig Config { get; private set; } = ConfigStore.CreateDefault();
    public ServiceState State { get; private set; } = ServiceState.NotInstalled;
    public ServiceStatus? Status { get; private set; }
    public IPAddress? PublicIp { get; private set; }

    public event EventHandler? StatusChanged;

    public MainForm()
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "AnyPortProxy";
        Icon = Theme.AppIcon;
        Font = Theme.Body;
        BackColor = Color.White;
        Size = new Size(1140, 860);
        MinimumSize = new Size(920, 620);
        StartPosition = FormStartPosition.CenterScreen;

        // Header: status light, text, and the one button that does "the next sensible thing".
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 88,
            ColumnCount = 3,
            BackColor = Theme.Light,
            Padding = new Padding(20, 12, 20, 12),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var texts = new FlowLayoutPanel { AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
        texts.Controls.Add(_title);
        texts.Controls.Add(_subtitle);
        var buttons = new FlowLayoutPanel { AutoSizeMode = AutoSizeMode.GrowAndShrink, AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 10, 0, 0) };
        _secondary.Click += async (_, _) => await ServiceActionAsync(ServiceManager.Restart, "Restarting…");
        _primary.Click += async (_, _) => await PrimaryClickAsync();
        buttons.Controls.Add(_secondary);
        buttons.Controls.Add(_primary);
        header.Controls.Add(_dot, 0, 0);
        header.Controls.Add(texts, 1, 0);
        header.Controls.Add(buttons, 2, 0);
        var headerLine = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Border };

        // Left navigation.
        var nav = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            Width = 236,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Theme.Nav,
            Padding = new Padding(10, 16, 10, 10),
        };
        AddPage(nav, "home", "🏠   Home", new HomePage(this));
        AddPage(nav, "websites", "🌐   Websites", new WebsitesPage(this));
        AddPage(nav, "ports", "🎮   Ports (games & apps)", new PortsPage(this));
        AddPage(nav, "health", "🩺   Health check", new HealthPage(this));
        AddPage(nav, "activity", "📜   Activity", new ActivityPage(this));
        AddPage(nav, "settings", "⚙   Settings", new SettingsPage(this));

        Controls.Add(_content);
        Controls.Add(nav);
        Controls.Add(headerLine);
        Controls.Add(header);

        if (!Elevation.IsAdmin)
        {
            var warn = new Label
            {
                UseMnemonic = false,
                Dock = DockStyle.Top,
                Height = 30,
                Text = "  ⚠  Not running as Administrator — you can look around, but changes won't work. Right-click the app and choose \"Run as administrator\".",
                BackColor = Theme.AmberLight,
                ForeColor = Theme.Text,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            Controls.Add(warn);
        }
        Controls.Add(BuildUpdateBar());

        Load += async (_, _) =>
        {
            ReloadConfig();
            RefreshStatus();
            ShowPage("home");
            // First time after installing: walk the user through it.
            if (State != ServiceState.NotInstalled && !Config.Onboarded) BeginInvoke(ShowTour);
            _timer.Tick += (_, _) => RefreshStatus();
            _timer.Start();
            PublicIp = await NetInfo.GetPublicIpAsync();
            StatusChanged?.Invoke(this, EventArgs.Empty);
            await CheckForUpdatesAsync(userAsked: false);
            _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(userAsked: false);
            _updateTimer.Start();
        };
    }

    // ---------------------------------------------------------------- updates

    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 12 * 60 * 60 * 1000 };
    private readonly Panel _updateBar = new() { Dock = DockStyle.Top, Height = 54, BackColor = Theme.AccentLight, Visible = false, Padding = new Padding(16, 6, 16, 6) };
    private readonly Label _updateText = new() { AutoSize = true, UseMnemonic = false, Font = Theme.Bold, ForeColor = Theme.Text, Margin = new Padding(0, 8, 12, 0) };

    public UpdateInfo? AvailableUpdate { get; private set; }

    public event EventHandler? UpdateChecked;

    private Control BuildUpdateBar()
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Color.Transparent };
        var notes = Theme.Secondary("What's new", (_, _) => ShowReleaseNotes());
        var now = Theme.Primary("Update now", async (_, _) => await InstallUpdateAsync());
        var later = Theme.Secondary("Later", (_, _) => _updateBar.Visible = false);
        foreach (var b in new[] { notes, now, later }) b.Margin = new Padding(0, 0, 8, 0);
        row.Controls.AddRange([new Label { Text = "⬆", Font = Theme.Glyph, AutoSize = true, ForeColor = Theme.Accent, Margin = new Padding(0, 4, 8, 0) }, _updateText, notes, now, later]);
        _updateBar.Controls.Add(row);
        return _updateBar;
    }

    /// <summary>Checks the GitHub releases. Quiet in the background; explains problems when the user asked.</summary>
    public async Task CheckForUpdatesAsync(bool userAsked)
    {
        var repo = Updater.Repo(Config);
        if (repo is null)
        {
            if (userAsked)
                MessageBox.Show(this, "This copy of AnyPortProxy wasn't built with an update source (a GitHub repository), so it can't check for updates.",
                    "Updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            AvailableUpdate = await Updater.CheckAsync(repo);
        }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException or TaskCanceledException)
        {
            if (userAsked) MessageBox.Show(this, ex.Message, "Couldn't check for updates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            UpdateChecked?.Invoke(this, EventArgs.Empty);
            return;
        }
        UpdateChecked?.Invoke(this, EventArgs.Empty);
        if (AvailableUpdate is { } u)
        {
            bool auto = Config.Updates.AutoInstall && State == ServiceState.Running;
            _updateText.Text = $"AnyPortProxy {u.Version} is available (you have {AppPaths.Version})." +
                               (auto ? " It will install by itself when nobody is connected." : "");
            _updateBar.Visible = true;
        }
        else
        {
            _updateBar.Visible = false;
            if (userAsked) MessageBox.Show(this, $"You're up to date (version {AppPaths.Version}).", "Updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    public void ShowReleaseNotes()
    {
        if (AvailableUpdate is not { } u) return;
        var text = string.IsNullOrWhiteSpace(u.Notes) ? "(No release notes.)" : u.Notes.Length > 3000 ? u.Notes[..3000] + "…" : u.Notes;
        if (MessageBox.Show(this, text + "\n\nOpen the release page on GitHub?", $"What's new in {u.Version}",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.Page.ToString()) { UseShellExecute = true }); } catch { }
        }
    }

    /// <summary>Downloads + verifies the new installer, runs it silently, and closes; the installer reopens the app.</summary>
    public async Task InstallUpdateAsync()
    {
        if (AvailableUpdate is not { } u) return;
        if (MessageBox.Show(this, $"Update to AnyPortProxy {u.Version} now?\n\nConnections drop for a few seconds while it restarts. Your websites, ports and settings are kept, and this window reopens when it's done.",
                "Update", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

        string? path = null;
        bool ok = ProgressDialog.Run(this, $"Updating to {u.Version}", async log =>
        {
            log($"Downloading {u.AssetName} ({u.Size / 1_048_576.0:0} MB) from GitHub…");
            int lastPct = -1;
            path = await Updater.DownloadAsync(u, new Progress<double>(v =>
            {
                int pct = (int)(v * 100);
                if (pct / 10 != lastPct / 10) log($"{pct}%");
                lastPct = pct;
            }));
            log(u.Sha256 is null ? "Downloaded and checked (size, product and version)." : "Downloaded and verified (GitHub checksum matches).");
            log("Starting the installer — this window will close and reopen in a moment.");
        });
        if (!ok || path is null) return;
        try
        {
            Updater.RunInstaller(path, reopenApp: true);
            Application.Exit();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Couldn't start the installer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void AddPage(FlowLayoutPanel nav, string key, string label, PageBase page)
    {
        var b = new Button
        {
            Text = label,
            TextAlign = ContentAlignment.MiddleLeft,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.Body,
            Width = 214,
            Height = 42,
            Margin = new Padding(0, 0, 0, 4),
            Cursor = Cursors.Hand,
            BackColor = Theme.Nav,
            ForeColor = Theme.Text,
            Padding = new Padding(8, 0, 0, 0),
            UseMnemonic = false,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Theme.Border;
        b.Click += (_, _) => ShowPage(key);
        nav.Controls.Add(b);
        _nav[key] = b;
        page.Dock = DockStyle.Fill;
        _pages[key] = page;
    }

    public void ShowPage(string key, bool autoRun = false)
    {
        foreach (var (k, b) in _nav)
        {
            b.BackColor = k == key ? Color.White : Theme.Nav;
            b.Font = k == key ? Theme.Bold : Theme.Body;
            b.ForeColor = k == key ? Theme.Accent : Theme.Text;
        }
        _current?.OnHide();
        _content.Controls.Clear();
        _current = _pages[key];
        _content.Controls.Add(_current);
        _current.OnShow(autoRun);
    }

    public void ReloadConfig()
    {
        try
        {
            Config = ConfigStore.Load();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Settings problem", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Saves settings; the running proxy picks them up automatically.</summary>
    public bool SaveConfig()
    {
        try
        {
            ConfigStore.Save(Config);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Couldn't save settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    public void RefreshStatus()
    {
        State = ServiceManager.GetState();
        Status = ServiceStatus.Read();
        if (!_busy) UpdateHeader();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateHeader()
    {
        var problems = Status is { IsFresh: true } ? Status.Problems().ToList() : new List<string>();
        _primary.Visible = true;
        _secondary.Visible = State == ServiceState.Running;
        _primary.Enabled = _secondary.Enabled = State is not (ServiceState.Starting or ServiceState.Stopping);

        switch (State)
        {
            case ServiceState.NotInstalled:
                SetHeader(Theme.Gray, "Not installed yet", "Click \"Install & start\" — it only takes a few seconds.");
                StylePrimary("Install & start", Theme.Accent);
                break;
            case ServiceState.Stopped:
                SetHeader(Theme.Red, "Stopped", "Nobody can reach your websites or ports right now.");
                StylePrimary("▶  Start", Theme.Green);
                break;
            case ServiceState.Starting:
                SetHeader(Theme.Amber, "Starting…", "");
                break;
            case ServiceState.Stopping:
                SetHeader(Theme.Amber, "Stopping…", "");
                break;
            default:
                if (Status is not { IsFresh: true })
                {
                    SetHeader(Theme.Amber, "Running (starting up…)", "Waiting for the proxy to report in.");
                    StylePrimary("Stop", Theme.Red);
                }
                else if (problems.Count > 0)
                {
                    SetHeader(Theme.Amber, "Running, but something needs attention", problems[0]);
                    StylePrimary("Fix problems…", Theme.Amber);
                }
                else
                {
                    var parts = new List<string>();
                    if (Status.SniffPorts.Count > 0) parts.Add($"Websites on {string.Join(", ", Status.SniffPorts.Select(p => p.Port))}");
                    parts.Add(Status.CatchAll.State == "Running" ? "All other ports forwarded" : "All-ports forwarding off");
                    parts.Add($"{Status.ActiveConnections} active connection{(Status.ActiveConnections == 1 ? "" : "s")}");
                    if (Status.BytesPerSec >= 1) parts.Add(ServiceStatus.FormatRate(Status.BytesPerSec));
                    if (Status.IgnoredProbes > 0) parts.Add($"{Status.IgnoredProbes} scanner probes ignored");
                    SetHeader(Theme.Green, "Running — everything looks good", string.Join("  ·  ", parts));
                    StylePrimary("Stop", Theme.Red);
                }
                break;
        }
    }

    private void SetHeader(Color color, string title, string subtitle)
    {
        _dot.ForeColor = color;
        _title.Text = title;
        _subtitle.Text = subtitle;
    }

    private void StylePrimary(string text, Color color)
    {
        _primary.Text = text;
        _primary.BackColor = color;
    }

    private async Task PrimaryClickAsync()
    {
        switch (State)
        {
            case ServiceState.NotInstalled:
                await InstallAsync();
                break;
            case ServiceState.Stopped:
                await ServiceActionAsync(ServiceManager.Start, "Starting…");
                break;
            case ServiceState.Running when Status is { IsFresh: true } && Status.Problems().Any():
                ShowPage("health", autoRun: true);
                break;
            case ServiceState.Running:
                if (MessageBox.Show(this, "While AnyPortProxy is stopped, nobody can reach your websites or ports.\n\nStop it?",
                        "Stop AnyPortProxy?", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                    await ServiceActionAsync(ServiceManager.Stop, "Stopping…");
                break;
        }
    }

    public async Task ServiceActionAsync(Action action, string busyText)
    {
        _busy = true;
        _primary.Enabled = _secondary.Enabled = false;
        SetHeader(Theme.Amber, busyText, "");
        try
        {
            await Task.Run(action);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message + "\n\nTip: the Health check page can usually tell you why.", "That didn't work",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        _busy = false;
        await Task.Delay(1500); // give the service a moment to write its first status
        RefreshStatus();
    }

    public Task<bool> InstallAsync()
    {
        _timer.Stop();
        var ok = ProgressDialog.Run(this, "Installing AnyPortProxy", log => Task.Run(() => Installer.Install(log)));
        ReloadConfig();
        _timer.Start();
        RefreshStatus();
        return Task.FromResult(ok);
    }

    /// <summary>The getting-started tour.</summary>
    public void ShowTour()
    {
        using (var tour = new OnboardingForm(this)) tour.ShowDialog(this);
        ReloadConfig();
        RefreshStatus();
        ShowPage("home");
    }

    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    public string ConnectAddress(int port) => PortHelper.ConnectAddress(Config, port, PublicIp);
}
