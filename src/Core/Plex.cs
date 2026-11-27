using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace CrateDeck.Core;

public sealed class Plex : IDisposable
{
    private readonly HttpClient client;
    public Uri Origin { get; }

    public static string E(string value) => Uri.EscapeDataString(value);

    public Plex(Uri origin, string identifier, string? token = null)
    {
        if (origin.Scheme != "https" && !(origin.IsLoopback && origin.Scheme == "http"))
            throw new ArgumentException("Use a secure Plex connection.");
        Origin = origin;
        client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Add("X-Plex-Product", "CrateDeck");
        client.DefaultRequestHeaders.Add("X-Plex-Client-Identifier", identifier);
        if (token != null)
            client.DefaultRequestHeaders.Add("X-Plex-Token", token);
    }

    public Uri Resolve(string path)
    {
        var u = new Uri(Origin, path);
        if (
            u.GetLeftPart(UriPartial.Authority) != Origin.GetLeftPart(UriPartial.Authority)
            || u.UserInfo.Length > 0
        )
            throw new InvalidDataException("Plex returned a different origin.");
        return u;
    }

    public void ApplyMediaHeaders(Uri destination, Action<string, string> add)
    {
        Resolve(destination.AbsoluteUri);
        foreach (
            var header in client.DefaultRequestHeaders.Where(h =>
                h.Key.StartsWith("X-Plex-", StringComparison.OrdinalIgnoreCase)
            )
        )
            add(header.Key, header.Value.Single());
    }

    public async Task<byte[]> Thumbnail(string key, CancellationToken ct)
    {
        Resolve(key);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            Resolve("/photo/:/transcode?width=144&height=144&minSize=1&url=" + E(key))
        );
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct
        );
        response.EnsureSuccessStatusCode();
        const int max = 256 * 1024;
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        byte[] block = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(block, ct)) > 0)
        {
            if (buffer.Length + read > max)
                throw new IOException("Thumbnail exceeds size limit.");
            buffer.Write(block, 0, read);
        }
        return buffer.ToArray();
    }

    public async Task<JsonNode> Call(
        string path,
        HttpMethod? method = null,
        CancellationToken ct = default
    )
    {
        method ??= HttpMethod.Get;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(method, Resolve(path));
                if (method != HttpMethod.Get)
                {
                    // SocketsHttpHandler may replay a bodyless HTTP/1.1 request after an EOF.
                    // A sent, explicit zero-length body keeps ambiguous mutations single-submit.
                    req.Content = new ByteArrayContent([]);
                    req.Headers.ExpectContinue = false;
                    req.Version = HttpVersion.Version11;
                    req.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
                }
                using var res = await client.SendAsync(req, ct).ConfigureAwait(false);
                if (
                    method == HttpMethod.Get
                    && attempt < 2
                    && ((int)res.StatusCode == 429 || (int)res.StatusCode >= 500)
                )
                {
                    var delay = res.Headers.RetryAfter?.Delta
                        ?? (res.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)
                        ?? TimeSpan.FromMilliseconds(250 * (1 << attempt));
                    if (delay > TimeSpan.FromSeconds(5))
                        throw new HttpRequestException(
                            "Plex requested a later retry.",
                            null,
                            res.StatusCode
                        );
                    await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, ct);
                    continue;
                }
                if (!res.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        $"Plex returned HTTP {(int)res.StatusCode}.",
                        null,
                        res.StatusCode
                    );
                var body = await res.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrWhiteSpace(body) || body.Trim() == "OK")
                    return new JsonObject();
                if (body.TrimStart().StartsWith('<'))
                {
                    using var reader = System.Xml.XmlReader.Create(
                        new StringReader(body),
                        new System.Xml.XmlReaderSettings
                        {
                            DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                            XmlResolver = null,
                            MaxCharactersInDocument = 16000000,
                        }
                    );
                    var root = System.Xml.Linq.XElement.Load(reader);
                    return FromXml(root);
                }
                var n = JsonNode.Parse(body)
                    ?? throw new InvalidDataException("Empty Plex response.");
                return n is JsonObject && n["MediaContainer"] is { } mc ? mc : n;
            }
            catch (HttpRequestException ex)
                when (method == HttpMethod.Get && attempt < 2 && ex.StatusCode == null)
            {
                await Task.Delay(250 * (1 << attempt), ct);
            }
        }
    }

    static JsonObject FromXml(System.Xml.Linq.XElement element)
    {
        var node = new JsonObject();
        foreach (var a in element.Attributes())
            node[a.Name.LocalName] = a.Value;
        foreach (var group in element.Elements().GroupBy(child => child.Name.LocalName))
        {
            string name = group.Key is "Track" or "Playlist" or "Video" ? "Metadata" : group.Key;
            var children = node[name] as JsonArray ?? new JsonArray();
            foreach (var child in group)
                children.Add(FromXml(child));
            if (node[name] == null)
                node[name] = children;
        }
        return node;
    }

    public async Task<(byte[] Data, long Size)> Range(
        string key,
        long start,
        int count,
        long? size,
        CancellationToken ct
    )
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Resolve(key));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        ct = deadline.Token;
        req.Headers.Range = new RangeHeaderValue(start, checked(start + count - 1));
        using var res = await client
            .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        var range = res.Content.Headers.ContentRange;
        if (
            res.StatusCode != HttpStatusCode.PartialContent
            || range?.From != start
            || range.Length is not long total
            || total <= start
            || (size.HasValue && total != size)
            || range.To != Math.Min(start + count, total) - 1
        )
            throw new IOException(
                "This media part did not return a consistent, seekable HTTP range."
            );
        var buffer = new byte[checked((int)(range.To.Value - start + 1))];
        using var stream = await res.Content.ReadAsStreamAsync(ct);
        await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        return (buffer, total);
    }

    public async Task<Track> Fresh(string id, CancellationToken ct = default)
    {
        var response = await Call("/library/metadata/" + E(id), ct: ct);
        var t = Track.Read(response.Items("Metadata").Single());
        if (t.Id != id)
            throw new IOException("Plex returned a different track identity.");
        return t.LibraryId != "" ? t : t with { LibraryId = response.Str("librarySectionID") };
    }

    public async Task SetFields(string library, Track t, Dictionary<string, string> fields)
    {
        if (fields.Count == 0)
            return;
        if (fields.Keys.Any(k => k is not ("title" or "originalTitle" or "index")))
            throw new ArgumentException("Unsupported track field.");
        string meta = "/library/metadata/" + E(t.Id) + "?includeFields=1";
        var response = await Call(meta);
        var before = response.Items("Metadata").Single();
        var current = Track.Read(before);
        var libraryId =
            current.LibraryId != "" ? current.LibraryId : response.Str("librarySectionID");
        if (current.Id != t.Id || current.Identity != t.Identity || libraryId != library)
            throw new IOException(
                "Track identity or library changed. Sync and review the new item."
            );
        var locks = before
            .Items("Field")
            .Where(f => fields.ContainsKey(f.Str("name")) && f["locked"] != null)
            .ToDictionary(
                f => f.Str("name"),
                f => f.Str("locked").ToLowerInvariant() is "1" or "true"
            );
        var parameters = fields
            .Select(f => f.Key + ".value=" + E(f.Value))
            .Concat(locks.Select(l => l.Key + ".locked=" + (l.Value ? "1" : "0")));
        var path =
            $"/library/sections/{E(library)}/all?type=10&id={E(t.Id)}&"
            + string.Join("&", parameters);
        await Call(path, HttpMethod.Put);
        var after = (await Call(meta)).Items("Metadata").Single();
        foreach (var (k, wasLocked) in locks)
        {
            bool nowLocked = after
                .Items("Field")
                .Any(f =>
                    f.Str("name") == k && f.Str("locked").ToLowerInvariant() is "1" or "true"
                );
            if (wasLocked != nowLocked)
                throw new IOException(
                    "Plex changed the field lock for " + k + ". Review the server result."
                );
        }
        var fresh = Track.Read(after);
        foreach (var (k, v) in fields)
            if (
                (
                    k switch
                    {
                        "title" => fresh.Title,
                        "originalTitle" => fresh.Performer,
                        _ => v == "" && fresh.Number == 0 ? "" : fresh.Number.ToString(),
                    }
                ) != v
            )
                throw new IOException("Plex reread differs for " + k + ".");
    }

    public void Dispose() => client.Dispose();
}

public sealed class RangeStream(Plex plex, string key, long size) : Stream
{
    const int Block = 256 * 1024;
    long position,
        baseOffset = -1;
    byte[] cache = [];

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken ct = default
    )
    {
        if (position >= size || buffer.IsEmpty)
            return 0;
        if (position < baseOffset || position >= baseOffset + cache.Length)
        {
            cache = (await plex.Range(key, position, Block, size, ct).ConfigureAwait(false)).Data;
            baseOffset = position;
        }
        int offset = checked((int)(position - baseOffset)),
            n = Math.Min(buffer.Length, cache.Length - offset);
        cache.AsMemory(offset, n).CopyTo(buffer);
        position += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] b, int o, int c, CancellationToken ct) =>
        ReadAsync(b.AsMemory(o, c), ct).AsTask();

    public override int Read(byte[] b, int o, int c) =>
        ReadAsync(b.AsMemory(o, c)).AsTask().GetAwaiter().GetResult();

    public override long Seek(long off, SeekOrigin origin) =>
        Position = checked(
            off
            + (
                origin switch
                {
                    SeekOrigin.Begin => 0,
                    SeekOrigin.Current => position,
                    SeekOrigin.End => size,
                    _ => throw new ArgumentException(),
                }
            )
        );

    public override long Position
    {
        get => position;
        set => position = value >= 0 ? value : throw new IOException("Negative seek.");
    }
    public override long Length => size;
    public override bool CanRead => true;
    public override bool CanWrite => false;
    public override bool CanSeek => true;

    public override void Flush() { }

    public override void SetLength(long v) => throw new NotSupportedException();

    public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
}
