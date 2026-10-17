namespace MindAttic.Capture.Tray;

/// <summary>What the Settings window edits; start-with-Windows lives in the registry, not tray.json.</summary>
internal sealed record TrayPreferences(string RecordHotkey, string ScreenshotHotkey, string? OutputRoot, VideoFormat Video, ImageFormat Image, bool StartWithWindows);

/// <summary>Settings: hotkeys (press the keys to bind them), output folder, default formats, start with Windows.</summary>
internal sealed class SettingsForm : Form
{
    private readonly TextBox _record;
    private readonly TextBox _screenshot;
    private readonly TextBox _output;
    private readonly ComboBox _video;
    private readonly ComboBox _image;
    private readonly CheckBox _startup;
    private readonly Func<TrayPreferences, string?> _apply;

    /// <param name="apply">Applies the choices; returns why they can't be used (e.g. a hotkey taken by another app), or null.</param>
    public SettingsForm(TrayPreferences current, string defaultOutput, Func<TrayPreferences, string?> apply)
    {
        _apply = apply;
        Text = "MindAttic Capture Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        TopMost = true;

        var grid = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
        Controls.Add(grid);

        _record = HotkeyBox(current.RecordHotkey);
        AddRow(grid, "Start / stop recording", _record, ClearButton(_record));
        _screenshot = HotkeyBox(current.ScreenshotHotkey);
        AddRow(grid, "Screenshot", _screenshot, ClearButton(_screenshot));
        grid.Controls.Add(new Label { Text = "Click a box and press the keys (Ctrl, Alt and/or Shift plus a key).", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 10) }, 1, grid.RowCount++);

        _output = new TextBox { Text = current.OutputRoot ?? "", PlaceholderText = defaultOutput, Width = 320 };
        var browse = new Button { Text = "Browse...", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = _output.Text.Length > 0 ? _output.Text : defaultOutput, UseDescriptionForTitle = true, Description = "Where captures are saved" };
            if (dialog.ShowDialog(this) == DialogResult.OK) _output.Text = dialog.SelectedPath;
        };
        AddRow(grid, "Save captures to", _output, browse);

        _video = Combo(TrayFormats.Videos.Select(TrayFormats.Name), TrayFormats.Videos.ToList().IndexOf(current.Video));
        AddRow(grid, "Video files", _video, null);
        _image = Combo(TrayFormats.Images.Select(TrayFormats.Name), TrayFormats.Images.ToList().IndexOf(current.Image));
        AddRow(grid, "Screenshot format", _image, null);

        _startup = new CheckBox { Text = "Start with Windows", Checked = current.StartWithWindows, AutoSize = true, Margin = new Padding(3, 10, 3, 3) };
        grid.Controls.Add(_startup, 1, grid.RowCount++);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var ok = new Button { Text = "Save", AutoSize = true };
        ok.Click += (_, _) => Save();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        grid.Controls.Add(buttons, 0, grid.RowCount);
        grid.SetColumnSpan(buttons, 3);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void Save()
    {
        var record = _record.Text.Trim();
        var screenshot = _screenshot.Text.Trim();
        if (record.Length > 0 && string.Equals(record, screenshot, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Recording and screenshot need different hotkeys.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var output = _output.Text.Trim();
        var prefs = new TrayPreferences(record, screenshot, output.Length > 0 ? output : null,
            TrayFormats.Videos[Math.Max(0, _video.SelectedIndex)], TrayFormats.Images[Math.Max(0, _image.SelectedIndex)], _startup.Checked);
        if (_apply(prefs) is { } problem)
        {
            MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    private static void AddRow(TableLayoutPanel grid, string caption, Control field, Control? extra)
    {
        var row = grid.RowCount++;
        grid.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 3) }, 0, row);
        grid.Controls.Add(field, 1, row);
        if (extra is not null) grid.Controls.Add(extra, 2, row);
    }

    /// <summary>A box that shows the keys pressed in it instead of typing them.</summary>
    private static TextBox HotkeyBox(string value)
    {
        var box = new TextBox { Text = value, Width = 320, ReadOnly = true, BackColor = SystemColors.Window, ShortcutsEnabled = false };
        box.KeyDown += (_, e) =>
        {
            e.SuppressKeyPress = e.Handled = true;
            if (e.KeyCode is Keys.Back or Keys.Delete && e.Modifiers == Keys.None) box.Text = "";
            else if (HotkeyText.From(e.KeyData) is { } key) box.Text = key;
        };
        return box;
    }

    private static Button ClearButton(TextBox box)
    {
        var b = new Button { Text = "Clear", AutoSize = true };
        b.Click += (_, _) => box.Text = "";
        return b;
    }

    private static ComboBox Combo(IEnumerable<string> items, int selected)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        combo.Items.AddRange(items.Cast<object>().ToArray());
        combo.SelectedIndex = Math.Max(0, selected);
        return combo;
    }
}
