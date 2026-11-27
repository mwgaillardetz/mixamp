using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mixamp.Core;

public record MetadataField(string Key, string Label, string? Value, bool Tags);

public sealed class TrackMetadata
{
    record Snapshot(Track Track, Dictionary<string, string> Values, HashSet<string> Locked);
    readonly Plex api;
    readonly Scope scope;
    readonly List<Snapshot> snapshots;
    public IReadOnlyList<MetadataField> Fields { get; }
    public int Count => snapshots.Count;
    public IEnumerable<Track> Tracks => snapshots.Select(s => s.Track);
    static readonly Dictionary<string, string> Labels = new()
    {
        ["title"] = "Title",
        ["originalTitle"] = "Track artist",
        ["index"] = "Track number",
        ["parentIndex"] = "Disc number",
        ["rating"] = "Critic rating (0–10)",
        ["audienceRating"] = "Audience rating (0–10)",
        ["userRating"] = "My rating (0–10)",
        ["addedAt"] = "Date added (Unix timestamp)",
        ["genre"] = "Genres",
        ["mood"] = "Moods",
        ["collection"] = "Collections",
        ["label"] = "Labels"
    };
    static readonly Dictionary<string, string> Tags = new() { ["genre"] = "Genre", ["mood"] = "Mood", ["collection"] = "Collection", ["label"] = "Label" };
    static string CanonicalTags(IEnumerable<string> values) => string.Join("\n", values.Select(x => x.Trim()).Where(x => x.Length > 0)
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    static async Task<Snapshot> Read(Plex api, Scope scope, string id)
    {
        var result = await api.Call("/library/metadata/" + Plex.E(id) + "?includeFields=1");
        var node = result.Items("Metadata").Single();
        var track = Track.Read(node);
        if (track.Id != id || node.Str("type") != "track" || (track.LibraryId != "" ? track.LibraryId : result.Str("librarySectionID")) != scope.Library)
            throw new IOException("The track no longer belongs to the selected library.");
        var values = Labels.Keys.ToDictionary(k => k, k => node.Str(k));
        var advertised = node.Items("Field").Select(f => f.Str("name")).ToList();
        foreach (var name in advertised.Concat(new[] { "titleSort", "summary", "year", "originallyAvailableAt" }))
            if ((node[name] is JsonValue || node[name] == null && advertised.Contains(name)) && name.Length > 0 && name.All(char.IsAsciiLetterOrDigit) &&
               name is not ("thumb" or "art" or "theme" or "key" or "ratingKey" or "guid" or "type"))
                values.TryAdd(name, node.Str(name));
        foreach (var (key, element) in Tags)
            values[key] = CanonicalTags(node.Items(element).Select(t => t.Str("tag")));
        var locks = node.Items("Field").Where(f => f.Str("locked").ToLowerInvariant() is "true" or "1")
            .Select(f => f.Str("name")).ToHashSet();
        return new(track, values, locks);
    }
    TrackMetadata(Plex api, Scope scope, List<Snapshot> snapshots)
    {
        this.api = api; this.scope = scope; this.snapshots = snapshots;
        Fields = snapshots[0].Values.Keys.Where(k => snapshots.All(s => s.Values.ContainsKey(k))).Select(k =>
        {
            var first = snapshots[0].Values[k];
            return new MetadataField(k, Labels.GetValueOrDefault(k, k), snapshots.All(s => s.Values[k] == first) ? first : null, Tags.ContainsKey(k));
        }).ToList();
    }
    public static async Task<TrackMetadata> Open(Plex api, Scope scope, IEnumerable<string> ids)
    {
        var rows = new List<Snapshot>();
        foreach (var batch in ids.Distinct().Chunk(6)) rows.AddRange(await Task.WhenAll(batch.Select(id => Read(api, scope, id))));
        if (rows.Count == 0) throw new IOException("Select at least one track.");
        return new(api, scope, rows);
    }
    static void Validate(string key, string value)
    {
        if (key == "title" && string.IsNullOrWhiteSpace(value)) throw new IOException("Enter a track title.");
        if (value == "") return;
        if (key is "index" or "parentIndex" or "year" or "addedAt" && (!long.TryParse(value, out var n) || n < 0))
            throw new IOException("Enter a nonnegative integer for " + key + ".");
        if (key is "rating" or "userRating" or "audienceRating" && (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var rating) || rating < 0 || rating > 10))
            throw new IOException("Ratings must be between 0 and 10.");
    }
    static bool Same(string key, string left, string right) => left == right ||
        (key is "rating" or "userRating" or "audienceRating" or "index" or "parentIndex" &&
        decimal.TryParse(left, CultureInfo.InvariantCulture, out var a) && decimal.TryParse(right, CultureInfo.InvariantCulture, out var b) && a == b);
    static async Task Write(Plex api, Scope scope, Snapshot now, Dictionary<string, string> changes)
    {
        List<string> args = [];
        foreach (var (key, value) in changes)
        {
            args.Add(key + ".locked=" + (now.Locked.Contains(key) ? "1" : "0"));
            if (!Tags.ContainsKey(key)) { args.Add(key + ".value=" + Plex.E(value)); continue; }
            var desired = value.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var remove = now.Values[key].Split('\n', StringSplitOptions.RemoveEmptyEntries).Except(desired).Select(Plex.E).ToArray();
            if (remove.Length > 0) args.Add(Plex.E(key + "[].tag.tag-") + "=" + Plex.E(string.Join(",", remove)));
            int i = 0;
            foreach (var tag in desired) args.Add(Plex.E($"{key}[{i++}].tag.tag") + "=" + Plex.E(tag));
        }
        if (args.Count == 0) return;
        await api.Call($"/library/sections/{Plex.E(scope.Library)}/all?type=10&id={Plex.E(now.Track.Id)}&" + string.Join("&", args), HttpMethod.Put);
        var after = await Read(api, scope, now.Track.Id);
        if (changes.Any(kv => !Same(kv.Key, after.Values.GetValueOrDefault(kv.Key, ""), kv.Value)))
            throw new IOException("Plex did not retain all changes to " + now.Track.Title + ". Reopen Edit to review its metadata.");
    }
    public async Task Save(Store store, Dictionary<string, string> patch)
    {
        if (patch.Count == 0) return;
        foreach (var (key, value) in patch)
        {
            if (!Fields.Any(f => f.Key == key) || value == null) throw new IOException("Unsupported metadata field.");
            Validate(key, value);
        }
        var batch = Guid.NewGuid().ToString();
        int done = 0;
        try
        {
            foreach (var item in snapshots)
            {
                var now = await Read(api, scope, item.Track.Id);
                if (now.Track.Identity != item.Track.Identity) throw new IOException("Recording changed. Reopen Edit.");
                var next = new Dictionary<string, string>(); var before = new Dictionary<string, string>();
                foreach (var (key, raw) in patch)
                {
                    var value = Tags.ContainsKey(key) ? CanonicalTags(raw.Split('\n')) : raw.Trim();
                    if (Same(key, now.Values[key], value)) continue;
                    if (now.Values[key] != item.Values[key]) throw new IOException("Metadata changed since opening Edit. Reopen it.");
                    next[key] = value; before[key] = now.Values[key];
                }
                if (next.Count == 0) continue;
                var journalId=store.Remember(scope, batch, "Edit track metadata", new(now.Track, "Metadata", "", "", JsonSerializer.Serialize(before), JsonSerializer.Serialize(next)));
                try { await Write(api, scope, now, next); }
                catch
                {
                    // Keep the undo record for partial writes and lost acknowledgments.
                    Snapshot? observed=null;
                    try { observed=await Read(api,scope,now.Track.Id); }
                    catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException) { }
                    if(observed!=null && before.All(kv=>observed.Values.TryGetValue(kv.Key,out var v) && Same(kv.Key,v,kv.Value)))
                        store.Forget(scope,journalId);
                    throw;
                }
                done++;
            }
        }
        catch (Exception ex) { throw new IOException($"{done} tracks updated. {ex.Message}", ex); }
    }
    public static async Task Restore(Plex api, Scope scope, Change change)
    {
        var now = await Read(api, scope, change.Track.Id);
        var before = JsonSerializer.Deserialize<Dictionary<string, string>>(change.Before)!;
        var after = JsonSerializer.Deserialize<Dictionary<string, string>>(change.After)!;
        if (now.Track.Identity != change.Track.Identity || before.Any(k => !now.Values.TryGetValue(k.Key, out var v) ||
            (!Same(k.Key, v, k.Value) && !Same(k.Key, v, after[k.Key])))) throw new IOException("The track changed since this edit.");
        var remaining=before.Where(kv=>!Same(kv.Key,now.Values[kv.Key],kv.Value)).ToDictionary();
        if(remaining.Count>0)await Write(api, scope, now, remaining);
    }
}
