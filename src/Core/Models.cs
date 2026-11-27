using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mixamp.Core;

public record Scope(string Account, string Server, string Library)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string Key => JsonSerializer.Serialize(this);
}

public record Part(string Id, string Key, string File, long Size, string Container);

public record Track(
    string Id,
    string Title,
    string Performer,
    string Artist,
    string Album,
    string AlbumId,
    string ArtistId,
    int Disc,
    int Number,
    int Year,
    long Duration,
    string Genre,
    string Format,
    int Bitrate,
    string Guid,
    Part[] Parts,
    string QueueItem = "",
    string PlaylistItem = "",
    string Thumb = "",
    string LibraryId = ""
)
{
    public string ArtistDisplay => Performer.Length == 0 ? Artist : Performer;
    public string Length => Cue.Format(Duration);

    [System.Text.Json.Serialization.JsonIgnore]
    public string Identity =>
        Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(
                        new
                        {
                            Guid,
                            Duration,
                            Parts = Parts.Select(x => new { x.File, x.Size }),
                        }
                    )
                )
            )
        );

    static string CleanKey(string key)
    {
        if (
            key.Contains('?')
            || key.Contains('#')
            || key.Contains('\\')
            || !key.StartsWith('/')
            || key.StartsWith("//")
        )
            return "";
        return key;
    }

    public static Track Read(JsonNode n)
    {
        var media = n.Items("Media").ToArray();
        var parts = media
            .SelectMany(m =>
                m.Items("Part")
                    .Select(p => new Part(
                        p.Str("id"),
                        CleanKey(p.Str("key")),
                        p.Str("file"),
                        p.Num("size"),
                        m.Str("container")
                    ))
            )
            .ToArray();
        return new(
            n.Str("ratingKey"),
            n.Str("title"),
            n.Str("originalTitle"),
            n.Str("grandparentTitle"),
            n.Str("parentTitle"),
            n.Str("parentRatingKey"),
            n.Str("grandparentRatingKey"),
            (int)n.Num("parentIndex"),
            (int)n.Num("index"),
            (int)n.Num("year"),
            n.Num("duration"),
            string.Join(", ", n.Items("Genre").Select(g => g.Str("tag"))),
            media.FirstOrDefault()?.Str("audioCodec") ?? "",
            (int)(media.FirstOrDefault()?.Num("bitrate") ?? 0),
            n.Str("guid"),
            parts,
            n.Str("playQueueItemID"),
            n.Str("playlistItemID"),
            CleanKey(n.Str("thumb")),
            n.Str("librarySectionID")
        );
    }
}

public static class Nodes
{
    public static string Str(this JsonNode n, string k) => n[k]?.ToString() ?? "";

    public static long Num(this JsonNode n, string k) => long.TryParse(n.Str(k), out var x) ? x : 0;

    public static IEnumerable<JsonNode> Items(this JsonNode n, string k) =>
        (n[k] as JsonArray ?? []).OfType<JsonNode>();
}

public record Cue(long? Start = null, long? End = null)
{
    public void Validate(long duration)
    {
        if ((Start ?? 0) < 0 || (End ?? duration) <= (Start ?? 0) || End > duration)
            throw new ArgumentException(
                "Start must precede stop and both must fit the original duration."
            );
    }

    public static long Parse(string s)
    {
        var m = Regex.Match(s.Trim(), @"^(?:(\d+):)?(\d+):([0-5]\d)(?:\.(\d{1,3}))?$");
        if (
            !m.Success
            || (
                m.Groups[1].Success
                && (m.Groups[2].Length != 2 || int.Parse(m.Groups[2].Value) > 59)
            )
        )
            throw new FormatException("Enter m:ss, m:ss.fff or h:mm:ss.fff.");
        long Value(int i) =>
            m.Groups[i].Success ? long.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
        return checked(
            ((Value(1) * 60 + Value(2)) * 60 + Value(3)) * 1000
            + int.Parse(m.Groups[4].Value.PadRight(3, '0'))
        );
    }

    public static string Format(long ms) =>
        $"{ms / 60000}:{ms / 1000 % 60:00}" + (ms % 1000 == 0 ? "" : $".{ms % 1000:000}");
}
