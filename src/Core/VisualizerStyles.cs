namespace Mixamp.Core;

public static class VisualizerStyles
{
    public const string Off = "Off", Pixels = "Pixel spectrum", Mirror = "Mirrored peaks", Dots = "Dot matrix";
    public static IReadOnlyList<string> All { get; } = [Off, Pixels, Mirror, Dots];
    public static string Resolve(string? saved) => saved != null && All.Contains(saved) ? saved : Off;
}
