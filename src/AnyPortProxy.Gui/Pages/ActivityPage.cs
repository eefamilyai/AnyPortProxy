using System.Text.RegularExpressions;
using AnyPortProxy.Core;

namespace AnyPortProxy.Gui.Pages;

internal sealed partial class ActivityPage : PageBase
{
    private const int MaxLines = 3000;
    private readonly RichTextBox _box = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        Font = Theme.Mono,
        BackColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle,
        WordWrap = true,
        DetectUrls = false,
    };
    private readonly TextBox _filter = new() { Width = 220, PlaceholderText = "🔎  Filter (e.g. 25565 or nas)" };
    private readonly CheckBox _problems = new() { Text = "Only problems", AutoSize = true, Margin = new Padding(10, 8, 10, 0) };
    private readonly CheckBox _pause = new() { Text = "Pause", AutoSize = true, Margin = new Padding(0, 8, 10, 0) };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly List<string> _all = new();
    private LogReader.Follower? _follower;

    public ActivityPage(MainForm main) : base(main)
    {
        var bar = new FlowLayoutPanel { AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 0, 0, 8) };
        bar.Controls.Add(_filter);
        bar.Controls.Add(_problems);
        bar.Controls.Add(_pause);
        bar.Controls.Add(Theme.Secondary("Clear view", (_, _) => { _all.Clear(); _box.Clear(); }));
        bar.Controls.Add(Theme.Secondary("📂  Open logs folder", (_, _) => MainForm.OpenFolder(AppPaths.LogDir)));
        _filter.TextChanged += (_, _) => Redraw();
        _problems.CheckedChanged += (_, _) => Redraw();
        _timer.Tick += (_, _) => Poll();

        Controls.Add(_box);
        Controls.Add(bar);
        Controls.Add(Header("Activity",
            "Every connection through AnyPortProxy shows up here live: who connected, which address or port, and where they were sent. " +
            "Yellow = warning, red = problem.",
            "Each line is one connection:\n\n" +
            "    [443] 203.0.113.7:51234 tls host=nas.example.com -> 192.168.1.20:443\n\n" +
            "means: someone at 203.0.113.7 connected to port 443, asked for nas.example.com, and was sent to 192.168.1.20.\n\n" +
            "•  \"forwarded\" lines are games/apps on other ports.\n" +
            "•  [UDP …] lines are UDP sessions (games, voice, VPN).\n" +
            "•  Yellow lines usually mean the computer behind an address isn't answering — is it on?\n" +
            "•  When it's very busy, lines are summarised (\"Busy: 1,234 more connections…\") so the log never floods.\n\n" +
            "Nothing shows up when you test from outside? Your router probably isn't sending traffic to this PC — see the Health check."));
        _box.BringToFront();
    }

    public override void OnShow(bool autoRun)
    {
        _all.Clear();
        _all.AddRange(LogReader.Tail(500));
        _follower = new LogReader.Follower();
        Redraw();
        _timer.Start();
    }

    public override void OnHide() => _timer.Stop();

    private void Poll()
    {
        if (_pause.Checked || _follower is null) return;
        var lines = _follower.ReadNew();
        if (lines.Count == 0) return;
        _all.AddRange(lines);
        if (_all.Count > MaxLines) _all.RemoveRange(0, _all.Count - MaxLines);
        foreach (var l in lines) Append(l);
    }

    private bool Matches(string line)
    {
        if (_problems.Checked && !(line.Contains(" WARN ") || line.Contains(" FAIL ") || line.Contains(" CRIT "))) return false;
        return _filter.Text.Length == 0 || line.Contains(_filter.Text, StringComparison.OrdinalIgnoreCase);
    }

    private void Redraw()
    {
        _box.SuspendLayout();
        _box.Clear();
        foreach (var l in _all) Append(l, scroll: false);
        _box.SelectionStart = _box.TextLength;
        _box.ScrollToCaret();
        _box.ResumeLayout();
        if (_all.Count == 0)
        {
            _box.SelectionColor = Theme.Gray;
            _box.AppendText(Main.State == ServiceState.NotInstalled
                ? "Nothing yet — install AnyPortProxy first (Home page)."
                : "Nothing yet. Connections will appear here as they happen.");
        }
    }

    private void Append(string line, bool scroll = true)
    {
        if (!Matches(line)) return;
        var m = LineRegex().Match(line);
        string text;
        Color color = Theme.Text;
        if (m.Success)
        {
            var level = m.Groups["level"].Value;
            color = level switch { "WARN" => Theme.Amber, "FAIL" or "CRIT" => Theme.Red, "DBUG" => Theme.Gray, _ => Theme.Text };
            text = $"{m.Groups["time"].Value}  {m.Groups["msg"].Value}";
        }
        else
        {
            text = line;
        }
        _box.SelectionStart = _box.TextLength;
        _box.SelectionColor = color;
        _box.AppendText(text + "\n");
        if (scroll)
        {
            _box.SelectionStart = _box.TextLength;
            _box.ScrollToCaret();
        }
    }

    [GeneratedRegex(@"^\S+ (?<time>\d\d:\d\d:\d\d)\.\d+ (?<level>\w{4}) [^:]+: (?<msg>.*)$")]
    private static partial Regex LineRegex();
}
