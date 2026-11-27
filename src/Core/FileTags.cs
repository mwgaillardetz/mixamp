using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mixamp.Core;

public record Mapping(string ServerRoot, string Share);

public static class MappedFiles
{
    public static string Resolve(string serverPath, IEnumerable<Mapping> mappings)
    {
        if (!serverPath.StartsWith('/') || serverPath.Contains('\\'))
            throw new IOException("Expected an absolute Linux part path.");
        var components = serverPath[1..].Split('/');
        if (
            components.Any(s =>
                s is "" or "." or ".."
                || s.EndsWith(' ')
                || s.EndsWith('.')
                || s.Any(c => c < 32 || "<>:\"|?*".Contains(c))
                || System.Text.RegularExpressions.Regex.IsMatch(
                    s,
                    @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(\.|$)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                )
            )
        )
            throw new IOException("Unsafe or ambiguous path component.");
        var m = mappings
            .OrderByDescending(x => x.ServerRoot.TrimEnd('/').Length)
            .FirstOrDefault(x =>
                serverPath.StartsWith(x.ServerRoot.TrimEnd('/') + "/", StringComparison.Ordinal)
            )
            ?? throw new IOException("No matching Plex root mapping.");
        if (
            !OperatingSystem.IsWindows()
            || !m.Share.StartsWith(@"\\")
            || m.Share.StartsWith(@"\\?")
            || m.Share.StartsWith(@"\\.")
            || m.Share.Contains('/')
            || m.Share[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries).Length < 2
        )
            throw new IOException("Choose a Windows UNC share.");
        var root = Path.GetFullPath(m.Share).TrimEnd('\\');
        var path = Path.GetFullPath(
            Path.Combine(
                root,
                serverPath[(m.ServerRoot.TrimEnd('/').Length + 1)..].Replace('/', '\\')
            )
        );
        if (!path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Path leaves the configured root.");
        for (string? p = path; p != null; p = Path.GetDirectoryName(p))
            if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Links and junctions are read-only.");
        return path;
    }
}

// Compare native FLAC blocks and encoded frames independently of TagLib's tag model.
public static class FlacGuard
{
    public static string Signature(string path, IEnumerable<string> changed)
    {
        using var s = File.OpenRead(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var omit = changed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        byte[] h = new byte[4];
        s.ReadExactly(h);
        if (!h.AsSpan().SequenceEqual("fLaC"u8))
            throw new IOException("Only native FLAC is writable.");
        int comments = 0;
        while (true)
        {
            s.ReadExactly(h);
            int kind = h[0] & 127,
                len = (h[1] << 16) | (h[2] << 8) | h[3];
            var data = new byte[len];
            s.ReadExactly(data);
            if (kind == 4)
            {
                comments++;
                using var r = new BinaryReader(
                    new MemoryStream(data),
                    new UTF8Encoding(false, true)
                );
                string Field()
                {
                    int n = r.ReadInt32();
                    if (n < 0 || n > r.BaseStream.Length - r.BaseStream.Position)
                        throw new InvalidDataException("Malformed Vorbis comment.");
                    return new UTF8Encoding(false, true).GetString(r.ReadBytes(n));
                }
                Field();
                int n = r.ReadInt32();
                List<string> keep = [];
                for (int i = 0; i < n; i++)
                {
                    var f = Field();
                    var parts = f.Split('=', 2);
                    if (parts.Length != 2)
                        throw new IOException("Invalid Vorbis field.");
                    if (!omit.Contains(parts[0]))
                        keep.Add(parts[0].ToUpperInvariant() + "=" + parts[1]);
                }
                if (r.BaseStream.Position != len)
                    throw new IOException("Unknown FLAC comment variant.");
                foreach (var f in keep.Order(StringComparer.Ordinal))
                {
                    var bytes = Encoding.UTF8.GetBytes(f);
                    hash.AppendData(BitConverter.GetBytes(bytes.Length));
                    hash.AppendData(bytes);
                }
            }
            else if (kind != 1)
            {
                hash.AppendData([(byte)kind]);
                hash.AppendData(BitConverter.GetBytes(len));
                hash.AppendData(data);
            }
            if ((h[0] & 128) != 0)
                break;
        }
        if (comments != 1)
            throw new IOException("Only a single Vorbis comment block is writable.");
        byte[] buf = new byte[65536];
        int count;
        while ((count = s.Read(buf)) > 0)
            hash.AppendData(buf, 0, count);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static void EditCopy(string path, Dictionary<string, string> changes)
    {
        var before = Signature(path, changes.Keys);
        using (var file = TagLib.File.Create(path))
        {
            var tag =
                file.GetTag(TagLib.TagTypes.Xiph) as TagLib.Ogg.XiphComment
                ?? throw new IOException("Unsupported FLAC tags.");
            foreach (var (field, v) in changes)
            {
                if (
                    field
                    is not (
                        "TITLE"
                        or "ARTIST"
                        or "ALBUM"
                        or "ALBUMARTIST"
                        or "GENRE"
                        or "DATE"
                        or "DISCNUMBER"
                        or "TRACKNUMBER"
                    )
                )
                    throw new ArgumentException("Unsupported tag.");
                if (v == "")
                    tag.RemoveField(field);
                else
                    tag.SetField(field, v);
            }
            file.Save();
        }
        using (var file = TagLib.File.Create(path))
        {
            var tag = (TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph);
            foreach (var (k, v) in changes)
                if (!tag.GetField(k).SequenceEqual(v == "" ? Array.Empty<string>() : [v]))
                    throw new IOException("FLAC verification failed for " + k);
        }
        if (Signature(path, changes.Keys) != before)
            throw new IOException(
                "Encoded frames or unrelated tags changed. The original was retained."
            );
    }
}
