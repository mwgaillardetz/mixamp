namespace Mixamp.Core;

public sealed record ThemePalette(string Canvas, string Side, string Paper, string Stripe,
    string Ink, string Muted, string Accent, string Tint, string Line, string DeckTop, string DeckBottom);

public sealed record PlayerTheme(string Name, string Description, ThemePalette Palette);

public static class ThemeCatalog
{
    public static IReadOnlyList<PlayerTheme> All { get; } =
    [
        new("Silver Halide", "Brushed silver with cool teal detailing", new(
            "#F4F5F3", "#E7EAE8", "#FAFBFA", "#EFF2F1", "#162A30", "#52646A",
            "#16696B", "#CEDDD9", "#CAD1CE", "#F8F9F8", "#CDD3D0")),
        new("Ochre Rewind", "Gruvbox Dark · toasted browns, cream and amber", new(
            "#282828", "#32302F", "#1D2021", "#282828", "#EBDBB2", "#BDAE93",
            "#FABD2F", "#504638", "#665C54", "#504945", "#3C3836")),
        new("Bitstream 98", "Classic Winamp · graphite metal and a green LED display", new(
            "#292A3A", "#343546", "#080909", "#080909", "#E2E2EA", "#C7C6CE",
            "#20FF36", "#242965", "#595A70", "#525367", "#282939")),
        new("Velvet Mocha", "Catppuccin Mocha · mauve on layered midnight blue", new(
            "#1E1E2E", "#181825", "#1E1E2E", "#242438", "#CDD6F4", "#BAC2DE",
            "#CBA6F7", "#45405E", "#45475A", "#313244", "#181825")),
        new("Phosphor FM", "Green phosphor on a dark terminal", new(
            "#0C1510", "#0F2015", "#101B13", "#132218", "#C4F4CD", "#8FBE99",
            "#8AFFA3", "#22452C", "#345F40", "#172E1E", "#122318")),
        new("Cyanotype", "Prussian blue ink on pale blue paper", new(
            "#DFEDF4", "#CEDFEA", "#F2F8FB", "#E5F0F6", "#123955", "#416074",
            "#005C91", "#B6D7E9", "#91B2C4", "#E2F0F6", "#A9CADC")),
    ];

    public static PlayerTheme Resolve(string? saved)
    {
        string? name = saved switch
        {
            "Silver Library" => "Silver Halide",
            "Terminal Green" => "Phosphor FM",
            "Midnight Amp" => "Bitstream 98",
            "Studio Dark" => "Ochre Rewind",
            "Fluent Light" => "Cyanotype",
            _ => saved
        };
        return All.FirstOrDefault(t => t.Name == name) ?? All[0];
    }
}
