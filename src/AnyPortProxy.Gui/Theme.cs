using System.Drawing.Drawing2D;
using AnyPortProxy.Core;

namespace AnyPortProxy.Gui;

internal static class Theme
{
    public static readonly Color Accent = Color.FromArgb(37, 99, 235);
    public static readonly Color AccentLight = Color.FromArgb(219, 234, 254);
    public static readonly Color Green = Color.FromArgb(22, 163, 74);
    public static readonly Color GreenLight = Color.FromArgb(220, 252, 231);
    public static readonly Color Amber = Color.FromArgb(217, 119, 6);
    public static readonly Color AmberLight = Color.FromArgb(254, 243, 199);
    public static readonly Color Red = Color.FromArgb(220, 38, 38);
    public static readonly Color RedLight = Color.FromArgb(254, 226, 226);
    public static readonly Color Gray = Color.FromArgb(100, 116, 139);
    public static readonly Color Light = Color.FromArgb(248, 250, 252);
    public static readonly Color Nav = Color.FromArgb(241, 245, 249);
    public static readonly Color Border = Color.FromArgb(226, 232, 240);
    public static readonly Color Text = Color.FromArgb(15, 23, 42);

    public static readonly Font Body = new("Segoe UI", 10f);
    public static readonly Font Bold = new("Segoe UI Semibold", 10f);
    public static readonly Font Small = new("Segoe UI", 9f);
    public static readonly Font H1 = new("Segoe UI Semibold", 18f);
    public static readonly Font H2 = new("Segoe UI Semibold", 12.5f);
    public static readonly Font StatusTitle = new("Segoe UI Semibold", 15f);
    public static readonly Font Glyph = new("Segoe UI Symbol", 14f);
    public static readonly Font Mono = new("Consolas", 9.75f);

    public static Icon AppIcon { get; } = MakeIcon();

    public static Button Primary(string text, EventHandler? click = null) => MakeButton(text, Accent, Color.White, click);
    public static Button Secondary(string text, EventHandler? click = null) => MakeButton(text, Color.White, Text, click, border: true);
    public static Button Danger(string text, EventHandler? click = null) => MakeButton(text, Red, Color.White, click);
    public static Button Success(string text, EventHandler? click = null) => MakeButton(text, Green, Color.White, click);

    private static Button MakeButton(string text, Color back, Color fore, EventHandler? click, bool border = false)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlatStyle = FlatStyle.Flat,
            BackColor = back,
            ForeColor = fore,
            Font = Bold,
            Padding = new Padding(12, 5, 12, 5),
            Margin = new Padding(0, 4, 8, 4),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            UseMnemonic = false,
        };
        b.FlatAppearance.BorderSize = border ? 1 : 0;
        b.FlatAppearance.BorderColor = Border;
        b.FlatAppearance.MouseOverBackColor = border ? Nav : ControlPaint.Dark(back, 0.05f);
        if (click is not null) b.Click += click;
        return b;
    }

    public static Label Label(string text, Font? font = null, Color? color = null) => new()
    {
        UseMnemonic = false,
        Text = text,
        AutoSize = true,
        Font = font ?? Body,
        ForeColor = color ?? Text,
        Margin = new Padding(0, 2, 0, 2),
    };

    public static WrapLabel Wrap(string text, Font? font = null, Color? color = null) => new()
    {
        UseMnemonic = false,
        Text = text,
        Font = font ?? Body,
        ForeColor = color ?? Text,
        Margin = new Padding(0, 2, 0, 6),
    };

    public static (string Glyph, Color Color, Color Back) For(CheckStatus s) => s switch
    {
        CheckStatus.Ok => ("✔", Green, GreenLight),
        CheckStatus.Info => ("ℹ", Accent, AccentLight),
        CheckStatus.Warn => ("⚠", Amber, AmberLight),
        _ => ("✖", Red, RedLight),
    };

    public static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static Icon MakeIcon()
    {
        var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var path = Rounded(new Rectangle(2, 2, 59, 59), 14);
            using var brush = new SolidBrush(Accent);
            g.FillPath(brush, path);
            using var f = new Font("Segoe UI Symbol", 34, FontStyle.Bold, GraphicsUnit.Pixel);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("⇄", f, Brushes.White, new RectangleF(0, 0, 64, 62), sf);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}

/// <summary>A label that wraps to its parent's width.</summary>
internal sealed class WrapLabel : Label
{
    public WrapLabel() => AutoSize = true;

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        if (Parent is null or StackPanel) return; // StackPanel sizes its WrapLabels itself
        Parent.Resize += (_, _) => Fit();
        Fit();
    }

    public void Fit()
    {
        if (Parent is null) return;
        int w = Parent.Width - Parent.Padding.Horizontal - Margin.Horizontal - SystemInformation.VerticalScrollBarWidth - 4;
        if (w > 60 && MaximumSize.Width != w) MaximumSize = new Size(w, 0);
    }
}

/// <summary>Vertical stack that stretches its children to full width; scrolls when needed.</summary>
internal sealed class StackPanel : FlowLayoutPanel
{
    public StackPanel()
    {
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoScroll = true;
        Dock = DockStyle.Fill;
    }

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        Stretch(e.Control!);
    }

    private int _lastWidth = -1;

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        if (Width == _lastWidth) return; // height changes don't affect child widths
        _lastWidth = Width;
        foreach (Control c in Controls) Stretch(c);
    }

    private void Stretch(Control c)
    {
        // Always reserve room for the scrollbar so its appearing/disappearing can't cause a resize loop.
        int w = Width - Padding.Horizontal - c.Margin.Horizontal - SystemInformation.VerticalScrollBarWidth - 4;
        if (w < 60) return;
        if (c is WrapLabel wl)
        {
            if (wl.MaximumSize.Width != w) wl.MaximumSize = new Size(w, 0);
        }
        else if (c.Tag as string != "natural" && c.Width != w)
        {
            c.Width = w;
        }
    }
}

/// <summary>White rounded box with a soft border.</summary>
internal class Card : Panel
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color Fill { get; set; } = Color.White;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color Edge { get; set; } = Theme.Border;

    public Card()
    {
        DoubleBuffered = true;
        Padding = new Padding(16);
        Margin = new Padding(0, 0, 12, 12);
        BackColor = Color.White;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Color.White);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 10);
        using var b = new SolidBrush(Fill);
        using var p = new Pen(Edge);
        e.Graphics.FillPath(b, path);
        e.Graphics.DrawPath(p, path);
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        Invalidate();
    }
}

/// <summary>Coloured message box used for tips, warnings and status banners.</summary>
internal sealed class Banner : Card
{
    private const int GlyphWidth = 32;
    private readonly Label _glyph = new() { UseMnemonic = false, AutoSize = true, Font = Theme.Glyph, BackColor = Color.Transparent };
    private readonly Label _text = new() { UseMnemonic = false, AutoSize = true, Font = Theme.Body, BackColor = Color.Transparent };

    public Banner()
    {
        Padding = new Padding(14, 10, 14, 10);
        Margin = new Padding(0, 4, 0, 10);
        Height = 48;
        Controls.Add(_glyph);
        Controls.Add(_text);
    }

    public void Set(CheckStatus status, string text)
    {
        var (glyph, color, back) = Theme.For(status);
        _glyph.Text = glyph;
        _glyph.ForeColor = color;
        _text.Text = text;
        Fill = back;
        Edge = back;
        BackColor = back;
        Relayout();
        Invalidate();
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        Relayout();
    }

    private void Relayout()
    {
        int textWidth = Math.Max(80, Width - Padding.Horizontal - GlyphWidth);
        _text.MaximumSize = new Size(textWidth, 0);
        _glyph.Location = new Point(Padding.Left, Padding.Top - 2);
        _text.Location = new Point(Padding.Left + GlyphWidth, Padding.Top + 2);
        int h = Math.Max(_glyph.PreferredHeight, _text.GetPreferredSize(new Size(textWidth, 0)).Height) + Padding.Vertical + 4;
        if (Height != h) Height = h;
    }
}
