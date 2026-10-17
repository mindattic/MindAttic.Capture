namespace MindAttic.Capture.Tray;

/// <summary>The area selector's controls, shown just outside the selection so they are never recorded.</summary>
internal sealed class RegionToolbar : Form
{
    private const int WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8;
    internal static readonly Color Back = Color.FromArgb(28, 28, 32);
    private static readonly Color ButtonBack = Color.FromArgb(48, 48, 54);

    private readonly Dictionary<AspectRatio, Button> _ratioButtons = [];
    private readonly FlowLayoutPanel _rows;

    private readonly ComboBox _video;
    private readonly ComboBox _image;

    public RegionToolbar(AspectRatio ratio, VideoFormat video, ImageFormat image)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9f);
        BackColor = Back;
        ForeColor = Color.White;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(6);
        KeyPreview = true;
        _rows = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
        Controls.Add(_rows);

        var ratios = Row();
        ratios.Controls.Add(Caption("Aspect"));
        foreach (var r in AspectRatio.All)
        {
            var button = MakeButton(r.Name, () => RatioPicked?.Invoke(r));
            _ratioButtons[r] = button;
            ratios.Controls.Add(button);
        }
        SetRatio(ratio);

        var output = Row();
        output.Controls.Add(Caption("Video"));
        _video = Combo(TrayFormats.Videos.Select(TrayFormats.Name), TrayFormats.Videos.ToList().IndexOf(video), 130);
        output.Controls.Add(_video);
        output.Controls.Add(Caption("Image"));
        _image = Combo(TrayFormats.Images.Select(TrayFormats.Name), Math.Max(0, TrayFormats.Images.ToList().IndexOf(image)), 70);
        output.Controls.Add(_image);
        var record = MakeButton("Record", () => ActionPicked?.Invoke(OverlayAction.Record));
        record.BackColor = Color.FromArgb(200, 40, 40);
        output.Controls.Add(record);
        output.Controls.Add(MakeButton("Screenshot", () => ActionPicked?.Invoke(OverlayAction.Screenshot)));
        output.Controls.Add(MakeButton("Cancel", () => ActionPicked?.Invoke(OverlayAction.Cancel)));

        var regions = Row();
        regions.Controls.Add(Caption("Saved"));
        _saved = Combo([], -1, 150);
        _saved.SelectionChangeCommitted += (_, _) =>
        {
            if (_saved.SelectedItem is string name && _regions.FirstOrDefault(r => r.Name == name) is { } region) RegionPicked?.Invoke(region);
        };
        regions.Controls.Add(_saved);
        regions.Controls.Add(Caption("Save as"));
        _name = new TextBox { Width = 150, BackColor = ButtonBack, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(2, 4, 2, 2) };
        regions.Controls.Add(_name);
        regions.Controls.Add(MakeButton("Save", SaveName));
    }

    private readonly ComboBox _saved;
    private readonly TextBox _name;
    private IReadOnlyList<SavedRegion> _regions = [];

    /// <summary>A saved region was picked from the list.</summary>
    public event Action<SavedRegion>? RegionPicked;

    /// <summary>Save the current area under this name.</summary>
    public event Action<string>? SaveRequested;

    public void SetRegions(IReadOnlyList<SavedRegion> regions, string? selected = null)
    {
        _regions = regions;
        _saved.Items.Clear();
        _saved.Items.AddRange(regions.Select(r => (object)r.Name).ToArray());
        _saved.SelectedItem = selected;
        _saved.Enabled = regions.Count > 0;
    }

    /// <summary>Shows the format choices of a picked region.</summary>
    public void SetFormats(VideoFormat video, ImageFormat image)
    {
        _video.SelectedIndex = TrayFormats.Videos.ToList().IndexOf(video);
        _image.SelectedIndex = Math.Max(0, TrayFormats.Images.ToList().IndexOf(image));
    }

    private void SaveName()
    {
        var name = _name.Text.Trim();
        if (name.Length == 0)
        {
            _name.Focus();
            return;
        }
        SaveRequested?.Invoke(name);
        _name.Clear();
    }

    public event Action<AspectRatio>? RatioPicked;

    public event Action<OverlayAction>? ActionPicked;

    public VideoFormat Video => TrayFormats.Videos[Math.Max(0, _video.SelectedIndex)];

    public ImageFormat Image => TrayFormats.Images[Math.Max(0, _image.SelectedIndex)];

    private static ComboBox Combo(IEnumerable<string> items, int selected, int width)
    {
        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = width, Margin = new Padding(2, 3, 8, 2),
            BackColor = ButtonBack, ForeColor = Color.White, TabStop = false,
        };
        combo.Items.AddRange(items.Cast<object>().ToArray());
        if (combo.Items.Count > 0) combo.SelectedIndex = Math.Clamp(selected, 0, combo.Items.Count - 1);
        return combo;
    }

    /// <summary>Keys pressed while the toolbar has focus (Esc, Enter, arrows) belong to the selector.</summary>
    public event Action<KeyEventArgs>? KeyForwarded;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
            return cp;
        }
    }

    public void SetRatio(AspectRatio ratio)
    {
        foreach (var (r, b) in _ratioButtons) b.BackColor = r == ratio ? RegionOverlay.Accent : ButtonBack;
        foreach (var (r, b) in _ratioButtons) b.ForeColor = r == ratio ? Color.Black : Color.White;
    }

    /// <summary>A new horizontal row of controls.</summary>
    internal FlowLayoutPanel Row()
    {
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        _rows.Controls.Add(row);
        return row;
    }

    internal static Label Caption(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Color.FromArgb(170, 170, 180), Margin = new Padding(2, 7, 6, 0),
    };

    internal static Button MakeButton(string text, Action click)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlatStyle = FlatStyle.Flat,
            BackColor = ButtonBack, ForeColor = Color.White, Margin = new Padding(2), Padding = new Padding(4, 1, 4, 1),
            TabStop = false, UseMnemonic = false,
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 78);
        b.Click += (_, _) => click();
        return b;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (ActiveControl == _name && e.KeyCode == Keys.Enter)
        {
            SaveName();
            e.Handled = e.SuppressKeyPress = true;
            return;
        }
        if (ActiveControl is TextBox or ComboBox && e.KeyCode is not (Keys.Escape or Keys.Enter)) return;
        KeyForwarded?.Invoke(e);
        e.Handled = true;
    }
}
