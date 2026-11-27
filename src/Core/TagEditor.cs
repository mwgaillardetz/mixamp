using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Mixamp.Core;

public static class TagEditor
{
    static readonly Dictionary<string, (string Id3, string Mp4)> Fields = new()
    {
        ["TITLE"] = ("TIT2", "©nam"),
        ["ARTIST"] = ("TPE1", "©ART"),
        ["ALBUMARTIST"] = ("TPE2", "aART"),
        ["ALBUM"] = ("TALB", "©alb"),
        ["GENRE"] = ("TCON", "©gen"),
        ["DATE"] = ("TDRC", "©day"),
        ["DISCNUMBER"] = ("TPOS", "disk"),
        ["TRACKNUMBER"] = ("TRCK", "trkn"),
    };

    public static bool Supported(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".flac" or ".mp3" or ".m4a";

    public static void EditCopy(string path, Dictionary<string, string> edits)
    {
        if (!Supported(path) || edits.Keys.Any(k => !Fields.ContainsKey(k)))
            throw new IOException("This file or field is read-only.");
        if (Path.GetExtension(path).Equals(".flac", StringComparison.OrdinalIgnoreCase))
        {
            FlacGuard.EditCopy(path, edits);
            return;
        }
        string Signature() =>
            Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase)
                ? Mp3(path, edits)
                : Mp4(path, edits);
        string before = Signature();
        uint trackTotal,
            discTotal;
        using (var file = TagLib.File.Create(path))
        {
            var tag = file.Tag;
            trackTotal = tag.TrackCount;
            discTotal = tag.DiscCount;
            foreach (var (k, v) in edits)
            {
                string[] list = v == "" ? [] : [v];
                uint Number() =>
                    v == "" ? 0
                    : uint.TryParse(v, out var n) ? n
                    : throw new IOException("Enter a whole number.");
                switch (k)
                {
                    case "TITLE":
                        tag.Title = v;
                        break;
                    case "ALBUM":
                        tag.Album = v;
                        break;
                    case "ARTIST":
                        tag.Performers = list;
                        break;
                    case "ALBUMARTIST":
                        tag.AlbumArtists = list;
                        break;
                    case "GENRE":
                        tag.Genres = list;
                        break;
                    case "DATE":
                        tag.Year = Number();
                        break;
                    case "DISCNUMBER":
                        tag.Disc = Number();
                        break;
                    case "TRACKNUMBER":
                        tag.Track = Number();
                        break;
                }
            }
            if ((file.TagTypesOnDisk & TagLib.TagTypes.Id3v1) == 0)
                file.RemoveTags(TagLib.TagTypes.Id3v1);
            file.Save();
        }
        if (before != Signature())
            throw new IOException(
                "This tag variant changed unrelated metadata or encoded audio. The original was retained."
            );
        using var check = TagLib.File.Create(path);
        var t = check.Tag;
        if (t.TrackCount != trackTotal || t.DiscCount != discTotal)
            throw new IOException("Track or disc total changed.");
        foreach (var (k, v) in edits)
        {
            string? actual = k switch
            {
                "TITLE" => t.Title,
                "ALBUM" => t.Album,
                "ARTIST" => string.Join("\0", t.Performers),
                "ALBUMARTIST" => string.Join("\0", t.AlbumArtists),
                "GENRE" => string.Join("\0", t.Genres),
                "DATE" => t.Year == 0 ? "" : t.Year.ToString(),
                "DISCNUMBER" => t.Disc == 0 ? "" : t.Disc.ToString(),
                _ => t.Track == 0 ? "" : t.Track.ToString(),
            };
            if ((actual ?? "") != v)
                throw new IOException("Tag verification failed for " + k);
        }
    }

    static int SyncSize(ReadOnlySpan<byte> bytes)
    {
        int n = 0;
        foreach (var b in bytes)
        {
            if (b > 127)
                throw new IOException("Invalid ID3 size.");
            n = (n << 7) | b;
        }
        return n;
    }

    static string Mp3(string path, Dictionary<string, string> edits)
    {
        using var f = File.OpenRead(path);
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] header = new byte[10];
        f.ReadExactly(header);
        if (
            !header.AsSpan(0, 3).SequenceEqual("ID3"u8)
            || header[3] is not (3 or 4)
            || header[5] != 0
        )
            throw new IOException("Only ordinary ID3v2.3 and v2.4 headers are writable.");
        h.AppendData(header.AsSpan(0, 6));
        int version = header[3];
        long end = 10L + SyncSize(header.AsSpan(6));
        if (end > f.Length)
            throw new IOException("Truncated ID3 tag.");
        var omit = edits.Keys.Select(k => Fields[k].Id3).ToHashSet();
        if (omit.Contains("TDRC"))
            omit.Add("TYER");
        while (f.Position + 10 <= end)
        {
            f.ReadExactly(header);
            if (header[0] == 0)
                break;
            int size =
                version == 4
                    ? SyncSize(header.AsSpan(4, 4))
                    : BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));
            if (size < 0 || size > end - f.Position || header[8] != 0 || header[9] != 0)
                throw new IOException("Read-only ID3 frame variant.");
            if (omit.Contains(Encoding.ASCII.GetString(header, 0, 4)))
                f.Position += size;
            else if (header[0] == 'T' && size is > 0 and <= 1048576)
            {
                byte[] text = new byte[size];
                f.ReadExactly(text);
                int length = size;
                if (text[0] is 0 or 3 && size > 1 && text[^1] == 0)
                    length--;
                h.AppendData(header.AsSpan(0, 4));
                h.AppendData(BitConverter.GetBytes(length));
                h.AppendData(text.AsSpan(0, length));
            }
            else
            {
                h.AppendData(header);
                HashBytes(f, h, size);
            }
        }
        f.Position = end;
        HashBytes(f, h, f.Length - end);
        return Convert.ToHexString(h.GetHashAndReset());
    }

    static void HashBytes(Stream s, IncrementalHash hash, long size)
    {
        var buffer = new byte[(int)Math.Min(size, 65536)];
        while (size > 0)
        {
            int n = (int)Math.Min(buffer.Length, size);
            s.ReadExactly(buffer, 0, n);
            hash.AppendData(buffer, 0, n);
            size -= n;
        }
    }

    static string Mp4(string path, Dictionary<string, string> edits)
    {
        using var f = File.OpenRead(path);
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var omit = edits.Keys.Select(k => Fields[k].Mp4).ToHashSet();
        bool metadata = false,
            audio = false;
        void Walk(long stop, string parent, int depth)
        {
            if (depth > 12)
                throw new IOException("Unsupported MP4 nesting.");
            byte[] head = new byte[8];
            while (f.Position < stop)
            {
                long begin = f.Position;
                f.ReadExactly(head);
                long size = BinaryPrimitives.ReadUInt32BigEndian(head);
                string type = Encoding.Latin1.GetString(head, 4, 4);
                if (size == 1)
                {
                    f.ReadExactly(head);
                    size = BinaryPrimitives.ReadInt64BigEndian(head);
                }
                if (size == 0)
                    size = stop - begin;
                long end = checked(begin + size);
                if (end < f.Position || end > stop)
                    throw new IOException("Invalid MP4 atom size.");
                if (type is "free" or "skip" || parent == "ilst" && omit.Contains(type))
                {
                    f.Position = end;
                    continue;
                }
                h.AppendData(Encoding.Latin1.GetBytes(type));
                if (parent == "meta" && type == "hdlr")
                {
                    if (end - f.Position is < 24 or > 4096)
                        throw new IOException("Unknown metadata handler.");
                    byte[] handler = new byte[end - f.Position];
                    f.ReadExactly(handler);
                    if (!handler.AsSpan(8, 4).SequenceEqual("mdir"u8))
                        throw new IOException("Unknown metadata handler.");
                    h.AppendData(handler.AsSpan(0, 12));
                    continue;
                }
                if (
                    type
                    is "moov"
                        or "udta"
                        or "meta"
                        or "ilst"
                        or "trak"
                        or "mdia"
                        or "minf"
                        or "stbl"
                )
                {
                    if (type == "meta")
                        HashBytes(f, h, 4);
                    if (type == "ilst")
                        metadata = true;
                    Walk(end, type, depth + 1);
                }
                else
                {
                    audio |= type == "mdat";
                    h.AppendData(BitConverter.GetBytes(end - f.Position));
                    HashBytes(f, h, end - f.Position);
                }
            }
        }
        Walk(f.Length, "", 0);
        if (!metadata || !audio)
            throw new IOException("Unsupported MP4 metadata layout.");
        return Convert.ToHexString(h.GetHashAndReset());
    }
}
