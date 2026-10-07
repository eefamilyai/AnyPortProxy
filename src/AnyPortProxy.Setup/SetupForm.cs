using System.Diagnostics;
using System.Drawing.Drawing2D;
using AnyPortProxy.Core;

namespace AnyPortProxy.Setup;

/// <summary>Three-step wizard: Welcome → Installing → Done.</summary>
internal sealed class SetupForm : Form
{
    private static readonly Color Accent = Color.FromArgb(37, 99, 235);
    private static readonly Color Green = Color.FromArgb(22, 163, 74);
    private static readonly Color Red = Color.FromArgb(220, 38, 38);
    private static readonly Color Gray = Color.FromArgb(100, 116, 139);
    private static readonly Color Ink = Color.FromArgb(15, 23, 42);
    private static readonly Font Body = new("Segoe UI", 10f);
    private static readonly Font Bold = new("Segoe UI Semibold", 10f);
    private static readonly Font H1 = new("Segoe UI Semibold", 16f);
    private static readonly Font H2 = new("Segoe UI Semibold", 12.5f);

    private readonly Panel _body = new() { Dock = DockStyle.Fill, Padding = new Padding(28, 20, 28, 10), BackColor = Color.White };
    private readonly FlowLayoutPanel _buttons = new()
    {
        Dock = DockStyle.Bottom,
        FlowDirection = FlowDirection.RightToLeft,
        Height = 62,
        Padding = new Padding(16, 12, 16, 12),
        BackColor = Color.FromArgb(248, 250, 252),
    };
    private readonly string? _installed = Installer.InstalledVersion();
    private bool _busy;

    public SetupForm()
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "AnyPortProxy Setup";
        Font = Body;
        ClientSize = new Size(660, 480);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        Controls.Add(_body);
        Controls.Add(_buttons);
        Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Color.FromArgb(226, 232, 240) });
        Controls.Add(MakeBanner());

        FormClosing += (_, e) =>
        {
            if (_busy)
            {
                e.Cancel = true;
                MessageBox.Show(this, "Please wait — setup is still working.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };

        ShowWelcome();
    }

    private Control MakeBanner()
    {
        var banner = new Panel { Dock = DockStyle.Top, Height = 96 };
        banner.Paint += (_, e) =>
        {
            using var brush = new LinearGradientBrush(banner.ClientRectangle, Color.FromArgb(59, 130, 246), Color.FromArgb(29, 78, 216), 0f);
            e.Graphics.FillRectangle(brush, banner.ClientRectangle);
            if (Icon is not null) e.Graphics.DrawIcon(new Icon(Icon, 48, 48), 26, 24);
            TextRenderer.DrawText(e.Graphics, "AnyPortProxy", H1, new Point(88, 22), Color.White);
            TextRenderer.DrawText(e.Graphics, $"Setup  ·  version {AppPaths.Version}", Body, new Point(90, 56), Color.FromArgb(219, 234, 254));
        };
        return banner;
    }

    private static Button MakeButton(string text, bool primary, EventHandler click)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(110, 36),
            FlatStyle = FlatStyle.Flat,
            Font = Bold,
            BackColor = primary ? Accent : Color.White,
            ForeColor = primary ? Color.White : Ink,
            Margin = new Padding(8, 0, 0, 0),
            Padding = new Padding(10, 2, 10, 2),
            Cursor = Cursors.Hand,
            UseMnemonic = false,
        };
        b.FlatAppearance.BorderSize = primary ? 0 : 1;
        b.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
        b.Click += click;
        return b;
    }

    private static Label MakeLabel(string text, Font font, Color color, int top, int width = 600) => new()
    {
        Text = text,
        Font = font,
        ForeColor = color,
        AutoSize = true,
        MaximumSize = new Size(width, 0),
        Location = new Point(28, top),
        UseMnemonic = false,
    };

    private void SetButtons(params Button[] buttons)
    {
        _buttons.Controls.Clear();
        foreach (var b in buttons) _buttons.Controls.Add(b); // right-to-left: first = rightmost
    }

    // ---------------------------------------------------------------- step 1

    private void ShowWelcome()
    {
        _body.Controls.Clear();
        if (!Payload.IsPresent)
        {
            _body.Controls.Add(MakeLabel("This setup file is incomplete", H2, Red, 20));
            _body.Controls.Add(MakeLabel("It doesn't contain the program files. Rebuild it with build.ps1, or download the setup again.", Body, Ink, 56));
            SetButtons(MakeButton("Close", true, (_, _) => Close()));
            return;
        }

        bool update = _installed is not null;
        _body.Controls.Add(MakeLabel(
            update
                ? (_installed == AppPaths.Version ? $"AnyPortProxy {_installed} is already installed." : $"Update AnyPortProxy {_installed} → {AppPaths.Version}")
                : "Welcome! This will install AnyPortProxy on this PC.", H2, Ink, 20));
        _body.Controls.Add(MakeLabel(
            update
                ? "Setup will replace the program files and restart the background service. Your websites, ports and settings are kept."
                : "AnyPortProxy lets people on the internet reach things running on your computers — websites by address, and games or apps on any port.",
            Body, Gray, 54));

        var list = string.Join("\n", new[]
        {
            "✔   The AnyPortProxy app  (Start menu + desktop shortcut)",
            "✔   The background service  (starts automatically with Windows)",
            "✔   The network driver for all-ports forwarding  (WinDivert)",
            "✔   A Windows Firewall rule for AnyPortProxy",
            "✔   The \"apx\" terminal command",
            "✔   An entry in Settings → Apps, so you can uninstall it normally",
        });
        _body.Controls.Add(MakeLabel(update ? "Setup will update:" : "Setup will install:", Bold, Ink, 112));
        _body.Controls.Add(MakeLabel(list, Body, Ink, 138));
        _body.Controls.Add(MakeLabel($"Installs to {AppPaths.InstallDir}. Nothing else is needed — everything is included.", new Font("Segoe UI", 9f), Gray, 300));

        var buttons = new List<Button>
        {
            MakeButton("Cancel", false, (_, _) => Close()),
            MakeButton(update ? (_installed == AppPaths.Version ? "Reinstall" : "Update") : "Install", true, async (_, _) => await RunAsync(install: true)),
        };
        if (update) buttons.Add(MakeButton("Uninstall…", false, async (_, _) => await RunAsync(install: false)));
        SetButtons(buttons.ToArray());
        AcceptButton = buttons[1];
    }

    // ---------------------------------------------------------------- step 2

    private async Task RunAsync(bool install)
    {
        bool purge = false;
        if (!install)
        {
            var answer = MessageBox.Show(this,
                "Uninstall AnyPortProxy?\n\nAlso delete your settings (websites and ports)?\n\nYes = uninstall and delete settings\nNo = uninstall but keep settings",
                "Uninstall", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button3);
            if (answer == DialogResult.Cancel) return;
            purge = answer == DialogResult.Yes;
        }

        // Open AnyPortProxy windows would lock the files.
        var running = Payload.RunningApps();
        if (running.Count > 0)
        {
            if (MessageBox.Show(this, "AnyPortProxy is open. Setup needs to close it first.\n\nClose it now?", Text,
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
            Payload.CloseApps(running);
        }

        _busy = true;
        _body.Controls.Clear();
        _buttons.Controls.Clear();
        ControlBox = false;

        var title = MakeLabel(install ? "Installing…" : "Uninstalling…", H2, Ink, 20);
        var bar = new ProgressBar { Style = ProgressBarStyle.Marquee, Location = new Point(30, 58), Size = new Size(600, 8) };
        var log = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
            Font = Body,
            Location = new Point(30, 80),
            Size = new Size(600, 210),
        };
        _body.Controls.AddRange([title, bar, log]);

        var lines = new List<string>();
        void Log(string line)
        {
            lines.Add(line);
            BeginInvoke(() => log.AppendText((line.StartsWith("WARNING") ? "⚠  " : "•  ") + line + Environment.NewLine));
        }

        Exception? error = null;
        try
        {
            if (install) await Task.Run(() => Payload.Install(Log));
            else await Installer.UninstallAsync(purge, Log);
        }
        catch (Exception ex)
        {
            error = ex;
            Log("ERROR: " + ex.Message);
        }

        try
        {
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "AnyPortProxySetup.log"), lines);
        }
        catch
        {
        }

        _busy = false;
        ControlBox = true;
        ShowDone(install, error);
    }

    // ---------------------------------------------------------------- step 3

    private void ShowDone(bool install, Exception? error)
    {
        _body.Controls.Clear();
        if (error is not null)
        {
            _body.Controls.Add(MakeLabel("✖  Setup couldn't finish", H2, Red, 20));
            _body.Controls.Add(MakeLabel(error.Message, Body, Ink, 58));
            _body.Controls.Add(MakeLabel(
                "Tips: close any AnyPortProxy windows and try again. If your antivirus blocked the network driver (WinDivert64.sys), allow it and run setup again.\n\n" +
                $"A log was saved to {Path.Combine(Path.GetTempPath(), "AnyPortProxySetup.log")}",
                Body, Gray, 130));
            SetButtons(MakeButton("Close", false, (_, _) => Close()), MakeButton("Try again", true, (_, _) => ShowWelcome()));
            return;
        }

        if (!install)
        {
            _body.Controls.Add(MakeLabel("✔  AnyPortProxy has been uninstalled", H2, Green, 20));
            _body.Controls.Add(MakeLabel("Thanks for using it!", Body, Gray, 58));
            SetButtons(MakeButton("Finish", true, (_, _) => Close()));
            return;
        }

        _body.Controls.Add(MakeLabel("✔  AnyPortProxy is installed and running", H2, Green, 20));
        _body.Controls.Add(MakeLabel(
            "It runs in the background and starts automatically with Windows.\n\n" +
            "Next steps in the app:\n" +
            "   1.  Websites → add your addresses (like nas.yourdomain.com)\n" +
            "   2.  Ports → open a port for a game or app\n" +
            "   3.  Health check → finds and fixes problems\n\n" +
            "Prefer the terminal? Open a new one and type  apx",
            Body, Ink, 58));
        var open = new CheckBox
        {
            Text = "Open AnyPortProxy now",
            Checked = true,
            AutoSize = true,
            Font = Bold,
            Location = new Point(30, 290),
            UseMnemonic = false,
        };
        _body.Controls.Add(open);
        var finish = MakeButton("Finish", true, (_, _) =>
        {
            if (open.Checked)
            {
                var gui = Path.Combine(AppPaths.InstallDir, AppPaths.GuiExe);
                try { Process.Start(new ProcessStartInfo(gui) { UseShellExecute = true }); } catch { }
            }
            Close();
        });
        SetButtons(finish);
        AcceptButton = finish;
    }
}
