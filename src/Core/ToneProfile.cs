namespace Mixamp.Core;

public record ToneProfile(string Name, double Low, double Mid, double High)
{
    public static readonly ToneProfile Flat = new("Flat", 0, 0, 0);
    public static readonly ToneProfile Warm = new("Warm", 0, -1, -2);
    public static readonly ToneProfile Clear = new("Clarity", -2, 0, -1);
    public static readonly ToneProfile Punch = new("Punch", 0, -2, -1);
    public static readonly string[] Modes = ["Auto", "Flat", "Warm", "Clarity", "Punch"];
    static readonly Dictionary<string,ToneProfile> Genres = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Jazz"] = Warm, ["Blues"] = Warm, ["Soul"] = Warm, ["Folk"] = Clear,
        ["Acoustic"] = Clear, ["Vocal"] = Clear, ["Singer-Songwriter"] = Clear,
        ["Electronic"] = Punch, ["Dance"] = Punch, ["House"] = Punch, ["Hip Hop"] = Punch,
        ["Hip-Hop"] = Punch, ["Techno"] = Punch
    };

    public static string Custom(double low,double mid,double high) =>
        FormattableString.Invariant($"Custom|{Math.Clamp(low,-6,6):0.0}|{Math.Clamp(mid,-6,6):0.0}|{Math.Clamp(high,-6,6):0.0}");
    public double Headroom => Math.Pow(10, -new[] {Low,Mid,High}.Sum(g => Math.Max(0,g)) / 20);

    public static ToneProfile Choose(string mode, string genre)
    {
        if (mode.StartsWith("Custom|",StringComparison.Ordinal))
        {
            var values = mode.Split('|').Skip(1).Select(s =>
                double.TryParse(s,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var v)
                    && double.IsFinite(v) && Math.Abs(v)<=6 ? v : double.NaN).ToArray();
            return values.Length==3 && values.All(double.IsFinite) ? new("Custom",values[0],values[1],values[2]) : Flat;
        }
        if (mode != "Auto") return mode switch { "Warm" => Warm, "Clarity" => Clear, "Punch" => Punch, _ => Flat };
        ToneProfile? selected = null;
        foreach (var tag in (genre ?? "").Split([',',';','/'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Genres.TryGetValue(tag, out var profile) || selected != null && profile != selected) return Flat;
            selected = profile;
        }
        return selected ?? Flat;
    }
}
