namespace MindAttic.Capture.Tray;

/// <summary>The area selector's controls, shown just outside the selection so they are never recorded.</summary>
internal sealed class RegionToolbar : Form
{
    private const int WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8;
    internal static readonly Color Back = Color.FromArgb(28, 28, 32);
    private static readonly Color ButtonBack = Color.FromArgb(48, 48, 54);

    private readonly Dictionary<AspectRatio, Button> _ratioButtons = [];
    private readonly FlowLayoutPanel _rows;

    public RegionToolbar(AspectRatio ratio)
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
    }

    public event Action<AspectRatio>? RatioPicked;

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
        if (ActiveControl is TextBox or ComboBox && e.KeyCode is not (Keys.Escape or Keys.Enter)) return;
        KeyForwarded?.Invoke(e);
        e.Handled = true;
    }
}
