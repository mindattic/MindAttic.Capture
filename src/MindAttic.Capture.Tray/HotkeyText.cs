namespace MindAttic.Capture.Tray;

/// <summary>Turns a key press into the hotkey text <see cref="Windows.GlobalHotkeys.TryParse"/> reads ("Ctrl+Alt+Shift+R").</summary>
internal static class HotkeyText
{
    /// <summary>The hotkey for <paramref name="keyData"/>, or null for a lone modifier or a key hotkeys can't use.</summary>
    public static string? From(Keys keyData)
    {
        var key = Name(keyData & Keys.KeyCode);
        if (key is null) return null;
        var parts = new List<string>();
        if (keyData.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (keyData.HasFlag(Keys.Alt)) parts.Add("Alt");
        if (keyData.HasFlag(Keys.Shift)) parts.Add("Shift");
        parts.Add(key);
        return string.Join("+", parts);
    }

    private static string? Name(Keys code) => code switch
    {
        >= Keys.A and <= Keys.Z => ((char)code).ToString(),
        >= Keys.D0 and <= Keys.D9 => ((char)code).ToString(),
        >= Keys.F1 and <= Keys.F24 => $"F{code - Keys.F1 + 1}",
        Keys.PrintScreen => "PrintScreen",
        Keys.Pause => "Pause",
        Keys.Scroll => "ScrollLock",
        Keys.Insert => "Insert",
        Keys.Home => "Home",
        Keys.End => "End",
        Keys.PageUp => "PageUp",
        Keys.PageDown => "PageDown",
        Keys.Space => "Space",
        _ => null,
    };
}
