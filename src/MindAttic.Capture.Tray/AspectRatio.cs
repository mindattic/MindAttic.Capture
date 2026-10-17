namespace MindAttic.Capture.Tray;

/// <summary>A selector shape: "16:9" is width 16 to height 9; Free has no value.</summary>
internal sealed record AspectRatio(string Name, double? Value)
{
    public static readonly AspectRatio Free = new("Free", null);

    public static IReadOnlyList<AspectRatio> All { get; } =
    [
        Free,
        Of(16, 9), Of(9, 16), Of(1, 1), Of(4, 3), Of(3, 4), Of(4, 5), Of(21, 9), Of(1, 2),
    ];

    private static AspectRatio Of(int w, int h) => new($"{w}:{h}", (double)w / h);

    /// <summary>The ratio called <paramref name="name"/>, or Free.</summary>
    public static AspectRatio Find(string? name) => All.FirstOrDefault(r => r.Name == name) ?? Free;
}
