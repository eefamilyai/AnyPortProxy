namespace AnyPortProxy.Gui.Pages;

internal abstract class PageBase : UserControl
{
    protected PageBase(MainForm main)
    {
        Main = main;
        BackColor = Color.White;
        Padding = new Padding(28, 22, 28, 18);
        Font = Theme.Body;
    }

    protected MainForm Main { get; }

    public virtual void OnShow(bool autoRun) { }

    public virtual void OnHide() { }

    /// <summary>Title + description block that docks to the top of a page.</summary>
    protected static Control Header(string title, string description) => new PageHeader(title, description);

    /// <summary>Page title + wrapped description; height follows the text (only recomputed when the width changes).</summary>
    private sealed class PageHeader : Panel
    {
        private readonly Label _title;
        private readonly Label _desc;
        private int _lastWidth = -1;

        public PageHeader(string title, string description)
        {
            Dock = DockStyle.Top;
            _title = Theme.Label(title, Theme.H1);
            _title.Location = Point.Empty;
            _desc = new Label { Text = description, AutoSize = true, Font = Theme.Body, ForeColor = Theme.Gray, UseMnemonic = false };
            Controls.Add(_title);
            Controls.Add(_desc);
            Height = 80;
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (Width == _lastWidth || Width < 50) return;
            _lastWidth = Width;
            int w = Math.Max(200, Width - 8);
            _desc.MaximumSize = new Size(w, 0);
            _desc.Location = new Point(1, _title.PreferredHeight + 4);
            Height = _desc.Top + _desc.GetPreferredSize(new Size(w, 0)).Height + 14;
        }
    }

    protected static ListView MakeList(params (string Name, int Width)[] columns)
    {
        var lv = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            Dock = DockStyle.Fill,
            Font = Theme.Body,
            BorderStyle = BorderStyle.FixedSingle,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        foreach (var (name, width) in columns) lv.Columns.Add(name, width);
        // Taller rows are easier to read and click.
        lv.SmallImageList = new ImageList { ImageSize = new Size(1, 30) };
        return lv;
    }

    protected static FlowLayoutPanel ButtonBar(params Control[] controls)
    {
        var bar = new FlowLayoutPanel { AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true, Padding = new Padding(0, 10, 0, 0) };
        bar.Controls.AddRange(controls);
        return bar;
    }
}
