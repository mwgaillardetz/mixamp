using System.Text.Json.Nodes;

namespace Mixamp.Core;

public sealed partial class Library(Plex api, Scope scope)
{
    static string E(string value) => Plex.E(value);

    // Album edits can change inherited track fields without changing the track timestamp.
    static string Revision(JsonNode n) => n.Num("updatedAt") == 0 ? "" :
        System.Text.Json.JsonSerializer.Serialize(new[] { n.Str("updatedAt"), n.Str("title"),
            n.Str("grandparentTitle"), n.Str("parentTitle"), n.Str("originalTitle"),
            n.Str("grandparentRatingKey"), n.Str("parentRatingKey"), n.Str("parentIndex"),
            n.Str("index"), n.Str("year"), n.Str("duration"), n["Genre"]?.ToJsonString() ?? "" });

    public async Task<bool> Sync(Store store, Func<Task> firstPage, CancellationToken ct,
        Action<int,int,bool>? progress = null)
    {
        var missing = await Task.Run(() => store.Revisions(scope), ct);
        bool incremental = missing.Count > 0, changed = false;
        bool lightweight = missing.Values.Any(v => v.StartsWith('['));
        int offset = 0, expected = -1;
        HashSet<string> seen = [];
        progress?.Invoke(0, expected, incremental);
        while (true)
        {
            string path = $"/library/sections/{E(scope.Library)}/all?type=10&sort=id&X-Plex-Container-Start={offset}&X-Plex-Container-Size=256";
            var response = await api.Call(path + (lightweight ? "&excludeElements=Media" : ""), ct: ct);
            if (response["offset"] != null && response.Num("offset") != offset)
                throw new IOException("Plex returned a different page.");
            if (response["totalSize"] != null)
            {
                int total = checked((int)response.Num("totalSize"));
                if (expected >= 0 && expected != total)
                    throw new IOException("Library changed during sync. Sync again.");
                expected = total;
            }
            var page = response.Items("Metadata").ToArray();
            var ids = page.Select(n => n.Str("ratingKey")).ToArray();
            if (ids.Any(id => id.Length == 0 || !seen.Add(id)) || page.Length == 0 && expected > offset)
                throw new IOException("Incomplete Plex page.");
            bool NeedsFull(JsonNode n) => !missing.TryGetValue(n.Str("ratingKey"), out var prior)
                || n.Num("updatedAt") == 0 || Revision(n) != prior;
            if (lightweight && page.Any(NeedsFull) && page.Any(n => n["Media"] == null))
            {
                var full = await api.Call(path, ct: ct);
                page = full.Items("Metadata").ToArray();
                if (!page.Select(n => n.Str("ratingKey")).SequenceEqual(ids))
                    throw new IOException("Library changed during sync. Sync again.");
            }
            Dictionary<string,string> revisions = [];
            List<Track> updates = [];
            foreach (var n in page)
            {
                string id = n.Str("ratingKey"), version = Revision(n);
                missing.Remove(id, out var prior);
                if (version.Length > 0 && version == prior) continue;
                var track = Track.Read(n);
                if (version.Length == 0)
                    version = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(track)));
                if (version == prior) continue;
                updates.Add(track);
                revisions.Add(id, version);
            }
            if (updates.Count > 0)
            {
                await Task.Run(() => store.Page(scope, updates, "", revisions), ct);
                changed = true;
            }
            if (offset == 0) await firstPage();
            offset += page.Length;
            progress?.Invoke(offset, expected, incremental);
            if (page.Length == 0 || offset == expected) break;
            if (expected >= 0 && offset > expected || expected < 0 && response["offset"] == null)
                throw new IOException("Server pagination could not be verified.");
        }
        ct.ThrowIfCancellationRequested();
        if (expected == seen.Count && missing.Count > 0)
        {
            await Task.Run(() => store.RemoveMissing(scope, missing, ct), ct);
            changed = true;
        }
        return changed;
    }

    async Task<List<Track>> WholeAlbum(string path)
    {
        List<Track> tracks = [];
        int? expected = null;
        while (true)
        {
            var response = await api.Call(
                path + "?X-Plex-Container-Start=" + tracks.Count + "&X-Plex-Container-Size=256"
            );
            var page = response.Items("Metadata").Select(Track.Read).ToList();
            if (response["offset"] != null && response.Num("offset") != tracks.Count)
                throw new IOException("Album page changed during review.");
            if (response["totalSize"] != null)
            {
                int total = checked((int)response.Num("totalSize"));
                if (expected.HasValue && total != expected)
                    throw new IOException("Album size changed during review.");
                expected = total;
            }
            if (
                page.Any(t =>
                    (t.LibraryId != "" ? t.LibraryId : response.Str("librarySectionID"))
                    != scope.Library
                )
            )
                throw new IOException("Album scope could not be verified.");
            tracks.AddRange(page);
            if (expected == tracks.Count || expected == null && page.Count < 256)
                break;
            if (page.Count == 0 || expected < tracks.Count || response["offset"] == null)
                throw new IOException("The server did not establish the full album membership.");
        }
        if (tracks.Select(t => t.Id).Distinct().Count() != tracks.Count)
            throw new IOException("Album membership is ambiguous.");
        return tracks;
    }

    public async Task AlbumArtist(
        Track[] tracks,
        string name,
        bool requireSelection,
        Func<List<Track>, bool> confirm
    )
    {
        if (name == "")
            throw new IOException("Choose an existing album artist.");
        var artists = (
            await api.Call(
                $"/library/sections/{Plex.E(scope.Library)}/all?type=8&title={Plex.E(name)}"
            )
        )
            .Items("Metadata")
            .Where(n => n.Str("title") == name)
            .ToList();
        if (artists.Count != 1)
            throw new IOException(
                "Plex must return one existing artist with this name before reassignment."
            );
        foreach (var group in tracks.GroupBy(t => t.AlbumId))
        {
            var id = Plex.E(group.Key);
            string path = "/library/metadata/" + id + "/children";
            var children = await WholeAlbum(path);
            if (children.Count == 0 || group.Any(t => !children.Any(c => c.Id == t.Id)))
                throw new IOException("Album membership changed.");
            if (children.All(c => c.ArtistId == artists[0].Str("ratingKey")))
                continue;
            if (requireSelection && children.Any(c => !group.Any(t => t.Id == c.Id)))
                throw new IOException("Select the entire album for a Both album artist change.");
            if (!confirm(children))
                throw new OperationCanceledException();
            var confirmed = await WholeAlbum(path);
            if (
                !confirmed
                    .Select(t => (t.Id, t.ArtistId, t.Performer))
                    .SequenceEqual(children.Select(t => (t.Id, t.ArtistId, t.Performer)))
            )
                throw new IOException("The album changed after review.");
            foreach (var c in children.Where(c => c.Performer == "" && c.ArtistDisplay != ""))
                await api.SetFields(
                    scope.Library,
                    c,
                    new() { ["originalTitle"] = c.ArtistDisplay }
                );
            await api.Call(
                $"/library/sections/{Plex.E(scope.Library)}/all?type=9&id={id}&artist.id.value={Plex.E(artists[0].Str("ratingKey"))}",
                HttpMethod.Put
            );
            var after = await WholeAlbum(path);
            if (
                after.Count != children.Count
                || children.Any(c =>
                    !after.Any(a =>
                        a.Id == c.Id && a.Artist == name && a.ArtistDisplay == c.ArtistDisplay
                    )
                )
            )
                throw new IOException(
                    "Album grouping or performer verification failed. Reread the album."
                );
        }
    }

    public async Task<List<JsonNode>> Stations(Track? artist)
    {
        if (artist != null)
        {
            var a = (
                await api.Call("/library/metadata/" + E(artist.ArtistId) + "?includeStations=1")
            )
                .Items("Metadata")
                .FirstOrDefault();
            return a?["Stations"] is JsonObject st
                ? st.Items("Metadata").ToList()
                : a?.Items("Stations")
                    .SelectMany(n => n["Metadata"] != null ? n.Items("Metadata") : [n])
                    .ToList()
                    ?? [];
        }
        var hubs = await api.Call("/hubs/sections/" + E(scope.Library) + "?includeStations=1&count=100");
        List<JsonNode> entries = [];
        foreach (var hub in hubs.Items("Hub").Where(h => h.Str("context") == "hub.music.stations" || h.Str("hubIdentifier").StartsWith("music.stations")))
        {
            var items = hub;
            if (hub.Num("more") == 1 && hub.Str("key").StartsWith('/')) items = await api.Call(hub.Str("key"));
            entries.AddRange(items.Items("Metadata"));
            foreach (var folder in items.Items("Directory"))
            {
                folder["directory"] = "1";
                entries.Add(folder);
            }
        }
        return entries;
    }

    string ServerUri(string path) =>
        E($"server://{scope.Server}/com.plexapp.plugins.library{path}");

    public Task<JsonNode> StationQueue(string key) =>
        api.Call("/playQueues?type=audio&uri=" + ServerUri(key), HttpMethod.Post);

    public Task<JsonNode> ContinueQueue(string id, string last) =>
        api.Call($"/playQueues/{E(id)}?center={E(last)}&window=50");

    public async Task<List<JsonNode>> Playlists() =>
        (await api.Call("/playlists?playlistType=audio")).Items("Metadata").ToList();

    public Task<JsonNode> PlaylistItems(string id) => api.Call($"/playlists/{E(id)}/items");

    public Task<JsonNode> AddToPlaylist(string? id, string name, Track track)
    {
        var uri = ServerUri("/library/metadata/" + track.Id);
        return id == null
            ? api.Call($"/playlists?type=audio&smart=0&title={E(name)}&uri={uri}", HttpMethod.Post)
            : api.Call($"/playlists/{E(id)}/items?uri={uri}", HttpMethod.Put);
    }

    public Task<JsonNode> CreatePlaylist(string name) =>
        api.Call($"/playlists?type=audio&smart=0&title={E(name)}", HttpMethod.Post);

    public Task<JsonNode> DeletePlaylist(string id) =>
        api.Call($"/playlists/{E(id)}", HttpMethod.Delete);

    public Task<JsonNode> Move(string id, string entry, string after) =>
        api.Call($"/playlists/{E(id)}/items/{E(entry)}/move?after={E(after)}", HttpMethod.Put);

    public Task<JsonNode> Remove(string id, string entry) =>
        api.Call($"/playlists/{E(id)}/items/{E(entry)}", HttpMethod.Delete);
}
