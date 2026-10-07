using AnyPortProxy.Core;

namespace AnyPortProxy.Gui;

/// <summary>Scrollable list of check results, each with an optional one-click "Fix" button.</summary>
internal sealed class ResultList : FlowLayoutPanel
{
    private readonly List<(CheckResult Result, Button? Fix)> _items = new();

    public ResultList()
    {
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoScroll = true;
        Dock = DockStyle.Fill;
        BackColor = Color.White;
        Padding = new Padding(0, 4, 0, 4);
    }

    public IEnumerable<CheckResult> Fixable => _items.Where(i => i.Fix is { Enabled: true }).Select(i => i.Result);

    public event EventHandler? FixApplied;

    public void Clear()
    {
        _items.Clear();
        Controls.Clear();
    }

    public void AddRange(IEnumerable<CheckResult> results)
    {
        SuspendLayout();
        foreach (var r in results) Add(r);
        ResumeLayout();
    }

    public void Add(CheckResult r)
    {
        var (glyph, color, back) = Theme.For(r.Status);
        var row = new Card
        {
            Fill = r.Status is CheckStatus.Fail or CheckStatus.Warn ? back : Color.White,
            Edge = r.Status is CheckStatus.Fail or CheckStatus.Warn ? back : Theme.Border,
            Padding = new Padding(12, 8, 12, 8),
            Margin = new Padding(0, 0, 0, 6),
        };
        row.BackColor = row.Fill;

        var icon = new Label { UseMnemonic = false, Text = glyph, ForeColor = color, Font = Theme.Glyph, AutoSize = true, BackColor = Color.Transparent };
        var title = new Label
        {
            UseMnemonic = false,
            Text = r.Title,
            Font = r.Status is CheckStatus.Fail or CheckStatus.Warn ? Theme.Bold : Theme.Body,
            ForeColor = Theme.Text,
            AutoSize = true,
            BackColor = Color.Transparent,
        };
        var detail = new Label
        {
            UseMnemonic = false,
            Text = r.Detail ?? "",
            Font = Theme.Small,
            ForeColor = Theme.Gray,
            AutoSize = true,
            BackColor = Color.Transparent,
            Visible = !string.IsNullOrWhiteSpace(r.Detail),
        };
        row.Controls.Add(icon);
        row.Controls.Add(title);
        row.Controls.Add(detail);

        Button? fix = null;
        if (r.Fix is not null)
        {
            fix = Theme.Primary(r.Fix.Label);
            fix.Margin = Padding.Empty;
            fix.Click += async (_, _) =>
            {
                fix.Enabled = false;
                fix.Text = "Fixing…";
                try
                {
                    var msg = await Task.Run(r.Fix.Run);
                    fix.Text = "✔ " + msg;
                    fix.BackColor = Theme.Green;
                }
                catch (Exception ex)
                {
                    fix.Text = "Failed";
                    fix.BackColor = Theme.Red;
                    MessageBox.Show(FindForm(), ex.Message, "Couldn't fix it", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                LayoutRow(row, icon, title, detail, fix);
                FixApplied?.Invoke(this, EventArgs.Empty);
            };
            row.Controls.Add(fix);
        }

        _items.Add((r, fix));
        Controls.Add(row);
        LayoutRow(row, icon, title, detail, fix);
    }

    public async Task FixAllAsync()
    {
        foreach (var (_, fix) in _items.ToList())
        {
            if (fix is not { Enabled: true }) continue;
            fix.PerformClick();
            while (fix.Text == "Fixing…") await Task.Delay(200);
        }
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        SuspendLayout();
        foreach (Control c in Controls)
        {
            if (c is not Card row) continue;
            var labels = row.Controls.OfType<Label>().ToList();
            LayoutRow(row, labels[0], labels[1], labels[2], row.Controls.OfType<Button>().FirstOrDefault());
        }
        ResumeLayout();
    }

    private void LayoutRow(Card row, Label icon, Label title, Label detail, Button? fix)
    {
        int width = Math.Max(300, ClientSize.Width - Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 4);
        row.Width = width;
        int fixWidth = fix is null ? 0 : fix.PreferredSize.Width + 12;
        int textLeft = row.Padding.Left + 30;
        int textWidth = Math.Max(120, width - textLeft - row.Padding.Right - fixWidth);

        icon.Location = new Point(row.Padding.Left, row.Padding.Top - 2);
        title.MaximumSize = new Size(textWidth, 0);
        detail.MaximumSize = new Size(textWidth, 0);
        title.Location = new Point(textLeft, row.Padding.Top + 1);
        int y = title.Bottom + 2;
        if (detail.Visible)
        {
            detail.Location = new Point(textLeft, y);
            y = detail.Bottom;
        }
        int height = Math.Max(y, icon.Bottom) + row.Padding.Bottom;
        if (fix is not null)
        {
            fix.Location = new Point(width - row.Padding.Right - fix.PreferredSize.Width, row.Padding.Top);
            height = Math.Max(height, fix.Bottom + row.Padding.Bottom);
        }
        row.Height = height;
    }
}

/// <summary>Runs a long task with a live log, then lets the user close the window.</summary>
internal sealed class ProgressDialog : Form
{
    private readonly TextBox _log = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        Font = Theme.Body,
        BackColor = Color.White,
        BorderStyle = BorderStyle.None,
    };

    private readonly ProgressBar _bar = new() { Dock = DockStyle.Top, Style = ProgressBarStyle.Marquee, Height = 6 };
    private readonly Label _status = Theme.Label("Working… please wait.", Theme.H2);
    private readonly Button _close;
    private readonly Func<Action<string>, Task> _work;

    public bool Succeeded { get; private set; }

    private ProgressDialog(string title, Func<Action<string>, Task> work)
    {
        _work = work;
        Text = title;
        Icon = Theme.AppIcon;
        Font = Theme.Body;
        Size = new Size(620, 400);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        BackColor = Color.White;
        Padding = new Padding(18);
        ControlBox = false;

        _close = Theme.Primary("Close", (_, _) => Close());
        _close.Enabled = false;
        var bottom = new FlowLayoutPanel { AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        bottom.Controls.Add(_close);
        var top = new Panel { Dock = DockStyle.Top, Height = 44 };
        _status.Location = new Point(0, 4);
        top.Controls.Add(_status);

        Controls.Add(_log);
        Controls.Add(bottom);
        Controls.Add(_bar);
        Controls.Add(top);
        Shown += async (_, _) => await RunAsync();
    }

    private void Append(string line)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Append(line));
            return;
        }
        _log.AppendText((line.StartsWith("WARNING") ? "⚠ " : "• ") + line + Environment.NewLine);
    }

    private async Task RunAsync()
    {
        try
        {
            await _work(Append);
            Succeeded = true;
            _status.Text = "✔ Done!";
            _status.ForeColor = Theme.Green;
        }
        catch (Exception ex)
        {
            Append("ERROR: " + ex.Message);
            _status.Text = "✖ That didn't work";
            _status.ForeColor = Theme.Red;
        }
        _bar.Style = ProgressBarStyle.Continuous;
        _bar.Value = 100;
        _close.Enabled = true;
        ControlBox = true;
        _close.Focus();
    }

    public static bool Run(IWin32Window owner, string title, Func<Action<string>, Task> work)
    {
        using var d = new ProgressDialog(title, work);
        d.ShowDialog(owner);
        return d.Succeeded;
    }
}

/// <summary>Shows what happened (e.g. after opening a port), with fix buttons.</summary>
internal sealed class ResultsDialog : Form
{
    private ResultsDialog(string title, string heading, IEnumerable<CheckResult> results)
    {
        Text = title;
        Icon = Theme.AppIcon;
        Font = Theme.Body;
        Size = new Size(720, 520);
        MinimumSize = new Size(560, 360);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.White;
        Padding = new Padding(18);
        MinimizeBox = false;

        var list = new ResultList();
        var head = new Panel { Dock = DockStyle.Top, Height = 46 };
        head.Controls.Add(new Label { UseMnemonic = false, Text = heading, Font = Theme.H2, AutoSize = true, Location = new Point(0, 6) });
        var bottom = new FlowLayoutPanel { AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        var close = Theme.Primary("Close", (_, _) => Close());
        bottom.Controls.Add(close);
        AcceptButton = close;

        Controls.Add(list);
        Controls.Add(bottom);
        Controls.Add(head);
        Shown += (_, _) => list.AddRange(results);
    }

    public static void Show(IWin32Window owner, string title, IEnumerable<CheckResult> results)
    {
        var list = results.ToList();
        var heading = list.Any(r => r.Status == CheckStatus.Fail) ? "✖ Some things didn't work — see below"
            : list.Any(r => r.Status == CheckStatus.Warn) ? "⚠ Done, with a few things to look at"
            : "✔ All done!";
        using var d = new ResultsDialog(title, heading, list);
        d.ShowDialog(owner);
    }
}

internal static class Ask
{
    /// <summary>Simple one-line input box.</summary>
    public static string? Text(IWin32Window owner, string title, string question, string initial = "")
    {
        using var f = new Form
        {
            Text = title,
            Icon = Theme.AppIcon,
            Font = Theme.Body,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ClientSize = new Size(420, 150),
            BackColor = Color.White,
        };
        var label = new Label { UseMnemonic = false, Text = question, AutoSize = true, Location = new Point(18, 18), MaximumSize = new Size(384, 0) };
        var box = new TextBox { Text = initial, Location = new Point(18, 50), Width = 384 };
        var ok = Theme.Primary("OK");
        ok.DialogResult = DialogResult.OK;
        ok.Location = new Point(250, 96);
        var cancel = Theme.Secondary("Cancel");
        cancel.DialogResult = DialogResult.Cancel;
        cancel.Location = new Point(320, 96);
        f.Controls.AddRange([label, box, ok, cancel]);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        return f.ShowDialog(owner) == DialogResult.OK ? box.Text.Trim() : null;
    }
}
