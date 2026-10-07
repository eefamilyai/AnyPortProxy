using AnyPortProxy.Core;

namespace AnyPortProxy.Gui.Pages;

internal sealed class HealthPage : PageBase
{
    private readonly ResultList _results = new();
    private readonly Button _run, _fixAll;
    private readonly Label _progress = Theme.Label("", Theme.Body, Theme.Gray);
    private readonly Banner _summary = new() { Dock = DockStyle.Top, Visible = false };
    private bool _running;

    public HealthPage(MainForm main) : base(main)
    {
        _run = Theme.Primary("▶  Run health check", async (_, _) => await RunAsync());
        _run.Font = Theme.H2;
        _fixAll = Theme.Success("🛠  Fix everything I can");
        _fixAll.Click += async (_, _) =>
        {
            _fixAll.Enabled = false;
            await _results.FixAllAsync();
            Main.RefreshStatus();
        };
        _fixAll.Font = Theme.H2;
        _fixAll.Visible = false;
        _results.FixApplied += (_, _) => _fixAll.Visible = _results.Fixable.Any();

        var bar = new FlowLayoutPanel { AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, 8) };
        _progress.Margin = new Padding(8, 14, 0, 0);
        bar.Controls.AddRange([_run, _fixAll, _progress]);

        Controls.Add(_results);
        Controls.Add(_summary);
        Controls.Add(bar);
        Controls.Add(Header("Health check",
            "Looks for common problems — the service, Windows Firewall, your router, DNS, programs blocking ports 80/443 — " +
            "and fixes most of them with one click."));
        _results.BringToFront();
    }

    public override void OnShow(bool autoRun)
    {
        if (autoRun && !_running) BeginInvoke(async () => await RunAsync());
    }

    private async Task RunAsync()
    {
        if (_running) return;
        _running = true;
        _run.Enabled = false;
        _fixAll.Visible = false;
        _summary.Visible = false;
        _results.Clear();
        Main.ReloadConfig();
        var cfg = Main.Config;
        var all = new List<CheckResult>();
        try
        {
            await Task.Run(() => Diagnostics.RunAsync(cfg,
                r => BeginInvoke(() =>
                {
                    all.Add(r);
                    _results.Add(r);
                }),
                s => BeginInvoke(() => _progress.Text = s)));
        }
        catch (Exception ex)
        {
            _results.Add(CheckResult.Fail("The health check itself failed", ex.Message));
        }
        await Task.Delay(100); // let queued results land
        _progress.Text = $"Checked at {DateTime.Now:t}";
        int fails = all.Count(r => r.Status == CheckStatus.Fail), warns = all.Count(r => r.Status == CheckStatus.Warn);
        _summary.Visible = true;
        if (fails == 0 && warns == 0) _summary.Set(CheckStatus.Ok, "Everything looks good! 🎉");
        else _summary.Set(fails > 0 ? CheckStatus.Fail : CheckStatus.Warn,
            $"Found {fails} problem(s) and {warns} warning(s). Problems are listed first in red; click a blue button to fix one.");
        _fixAll.Visible = _results.Fixable.Any();
        _fixAll.Enabled = true;
        _run.Enabled = true;
        _running = false;
    }
}
