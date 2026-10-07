using System.Diagnostics;
using AnyPortProxy.Core;

namespace AnyPortProxy.Gui.Pages;

internal sealed class SettingsPage : PageBase
{
    private readonly StackPanel _stack = new();
    private readonly TextBox _domain = new() { Width = 300, Tag = "natural", PlaceholderText = "example.com" };
    private readonly RadioButton _normal = new() { Text = "Normal — one line per connection", AutoSize = true, Tag = "natural" };
    private readonly RadioButton _detailed = new() { Text = "Detailed — also byte counts and technical info (for troubleshooting)", AutoSize = true, Tag = "natural" };

    public SettingsPage(MainForm main) : base(main)
    {
        Controls.Add(_stack);
    }

    public override void OnShow(bool autoRun)
    {
        Main.ReloadConfig();
        _stack.SuspendLayout();
        _stack.Controls.Clear();

        _stack.Controls.Add(Theme.Label("Settings", Theme.H1));

        Section("Your domain");
        _stack.Controls.Add(Theme.Wrap("The domain you own. Used to show addresses like example.com:25565, and lets you type just \"nas\" instead of nas.example.com.", Theme.Small, Theme.Gray));
        _domain.Text = Main.Config.Domain ?? "";
        _stack.Controls.Add(Row(_domain, Theme.Primary("Save", (_, _) =>
        {
            var d = _domain.Text.Trim().TrimEnd('.').ToLowerInvariant();
            if (d.Contains("://")) d = new Uri(d).Host;
            Main.Config.Domain = d.Length == 0 ? null : d;
            if (Main.SaveConfig()) MessageBox.Show(Main, "Saved.", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
        })));

        Section("Activity log detail");
        bool detailed = ConfigStore.LogLevel(Main.Config).Equals("Debug", StringComparison.OrdinalIgnoreCase);
        _normal.Checked = !detailed;
        _detailed.Checked = detailed;
        _stack.Controls.Add(_normal);
        _stack.Controls.Add(_detailed);
        _stack.Controls.Add(Row(Theme.Secondary("Save", (_, _) =>
        {
            ConfigStore.SetLogLevel(Main.Config, _detailed.Checked ? "Debug" : "Information");
            if (Main.SaveConfig()) MessageBox.Show(Main, "Saved.", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
        })));

        Section("Flood protection");
        _stack.Controls.Add(Theme.Wrap("Stops one visitor (or an attack) from using up the whole server. Devices on your home network are never limited. " +
                                       "The defaults suit almost everyone.", Theme.Small, Theme.Gray));
        var limits = Main.Config.Proxy.Limits;
        var total = new NumericUpDown { Minimum = 100, Maximum = 1_000_000, Increment = 1000, Width = 110, Value = Math.Clamp(limits.MaxConnections, 100, 1_000_000) };
        var perIp = new NumericUpDown { Minimum = 10, Maximum = 1_000_000, Increment = 50, Width = 110, Value = Math.Clamp(limits.MaxConnectionsPerIp, 10, 1_000_000) };
        _stack.Controls.Add(Row(
            new Label { Text = "Max open connections:", AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 8, 6, 0) }, total,
            new Label { Text = "per internet address:", AutoSize = true, UseMnemonic = false, Margin = new Padding(16, 8, 6, 0) }, perIp,
            Theme.Secondary("Save", (_, _) =>
            {
                Main.Config.Proxy.Limits.MaxConnections = (int)total.Value;
                Main.Config.Proxy.Limits.MaxConnectionsPerIp = (int)Math.Min(perIp.Value, total.Value);
                if (Main.SaveConfig()) MessageBox.Show(Main, "Saved — applied immediately.", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }),
            Theme.Secondary("Defaults", (_, _) =>
            {
                total.Value = 20_000;
                perIp.Value = 300;
            })));

        Section("Terminal");
        _stack.Controls.Add(Theme.Wrap("Everything in this app also works from a terminal. Type  apx  for a menu, or  apx help  for all commands " +
                                       "(e.g.  apx port open \"minecraft java\",  apx check --fix,  apx logs -f).", Theme.Body));
        _stack.Controls.Add(Row(Theme.Secondary("⌨  Open terminal menu", (_, _) => OpenTerminal())));

        Section("Advanced");
        _stack.Controls.Add(Row(
            Theme.Secondary("📝  Edit settings file", (_, _) => Process.Start(new ProcessStartInfo("notepad.exe", $"\"{AppPaths.ConfigFile}\"") { UseShellExecute = true })),
            Theme.Secondary("📂  Settings & logs folder", (_, _) => MainForm.OpenFolder(AppPaths.DataDir)),
            Theme.Secondary("📂  Program folder", (_, _) => MainForm.OpenFolder(AppPaths.ServiceDir()))));

        Section("Install");
        _stack.Controls.Add(Row(
            Theme.Secondary("📦  Reinstall / update program files", async (_, _) => await Main.InstallAsync()),
            Theme.Danger("🗑  Uninstall AnyPortProxy…", async (_, _) => await UninstallAsync())));

        Section("About");
        _stack.Controls.Add(Theme.Wrap(
            $"AnyPortProxy {AppPaths.Version}\n" +
            $"Settings: {AppPaths.ConfigFile}\n" +
            $"Program: {AppPaths.ServiceDir()}\n" +
            $"Service: {Main.State}", Theme.Small, Theme.Gray));
        _stack.ResumeLayout();
    }

    private void Section(string title)
    {
        var l = Theme.Label(title, Theme.H2);
        l.Margin = new Padding(0, 18, 0, 2);
        _stack.Controls.Add(l);
    }

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSizeMode = AutoSizeMode.GrowAndShrink, AutoSize = true, WrapContents = true, Margin = new Padding(0, 4, 0, 4) };
        row.Controls.AddRange(controls);
        return row;
    }

    private static void OpenTerminal()
    {
        var exe = Path.Combine(AppPaths.ServiceDir(), AppPaths.CliExe);
        if (!File.Exists(exe)) exe = Path.Combine(AppPaths.AppDir, AppPaths.CliExe);
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }

    private async Task UninstallAsync()
    {
        var answer = MessageBox.Show(Main,
            "Uninstall AnyPortProxy?\n\nThis stops it, removes the service, firewall rules, router forwards and shortcuts.\n\n" +
            "Also delete your settings (websites and ports)?\n\nYes = uninstall and delete settings\nNo = uninstall but keep settings",
            "Uninstall", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button3);
        if (answer == DialogResult.Cancel) return;
        bool purge = answer == DialogResult.Yes;
        ProgressDialog.Run(Main, "Uninstalling", log => Installer.UninstallAsync(purge, log));
        await Task.Yield();
        Application.Exit();
    }
}
