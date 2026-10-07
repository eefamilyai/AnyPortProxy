namespace AnyPortProxy.Gui;

/// <summary>Short "How does this work?" explanation for a page, with a way into the full tour.</summary>
internal sealed class HelpDialog : Form
{
    private HelpDialog(string title, string text, MainForm? main)
    {
        Text = $"How does this work? — {title}";
        Icon = Theme.AppIcon;
        Font = Theme.Body;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.White;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = Padding.Empty;

        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(24, 20, 24, 18) };
        stack.Controls.Add(new Label { Text = title, Font = Theme.H1, AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 0, 0, 10) });
        stack.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(560, 0), UseMnemonic = false, Margin = new Padding(0, 0, 0, 16) });
        var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
        var ok = Theme.Primary("Got it", (_, _) => Close());
        buttons.Controls.Add(ok);
        if (main is not null)
            buttons.Controls.Add(Theme.Secondary("📖  Take the full tour", (_, _) =>
            {
                Close();
                main.ShowTour();
            }));
        stack.Controls.Add(buttons);
        Controls.Add(stack);
        AcceptButton = ok;
        CancelButton = ok;
    }

    public static void Show(Form? owner, string title, string text)
    {
        using var d = new HelpDialog(title, text, owner as MainForm);
        d.ShowDialog(owner);
    }
}
