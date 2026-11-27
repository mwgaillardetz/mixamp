using System.Text.Json.Nodes;

namespace Mixamp.Core;

public sealed class Library(Plex api, Scope scope)
{
    static string E(string value) => Plex.E(value);

    public async Task Sync(Store store, Func<Task> firstPage, CancellationToken ct)
    {
        int offset = 0,
            expected = -1;
        string stamp = Guid.NewGuid().ToString("N");
        while (true)
        {
            var n = await api.Call(
                $"/library/sections/{E(scope.Library)}/all?type=10&sort=id&X-Plex-Container-Start={offset}&X-Plex-Container-Size=256",
                ct: ct
            );
            if (n["offset"] != null && n.Num("offset") != offset)
                throw new IOException("Plex returned a different page.");
            if (n["totalSize"] != null)
            {
                var total = checked((int)n.Num("totalSize"));
                if (expected >= 0 && expected != total)
                    throw new IOException("Library changed during sync. Sync again.");
                expected = total;
            }
            var page = n.Items("Metadata").Select(Track.Read).ToList();
            if (page.Any(t => t.Id == "") || (page.Count == 0 && expected > offset))
                throw new IOException("Incomplete Plex page.");
            ct.ThrowIfCancellationRequested();
            await Task.Run(() => store.Page(scope, page, stamp), ct);
            if (offset == 0)
                await firstPage();
            offset += page.Count;
            if (page.Count == 0 || offset == expected)
                break;
            if (expected >= 0 && offset > expected || expected < 0 && n["offset"] == null)
                throw new IOException("Server pagination could not be verified.");
        }
        ct.ThrowIfCancellationRequested();
        if (expected >= 0 && !await Task.Run(() => store.Complete(scope, stamp, expected), ct))
            throw new IOException("Catalog changed during sync. Cached tracks retained.");
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
        var hubs = await api.Call("/hubs/sections/" + E(scope.Library) + "?includeStations=1");
        return hubs.Items("Hub")
            .Where(h => h.Str("context") == "hub.music.stations")
            .SelectMany(h => h.Items("Metadata"))
            .ToList();
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

    public Task<JsonNode> Move(string id, string entry, string after) =>
        api.Call($"/playlists/{E(id)}/items/{E(entry)}/move?after={E(after)}", HttpMethod.Put);

    public Task<JsonNode> Remove(string id, string entry) =>
        api.Call($"/playlists/{E(id)}/items/{E(entry)}", HttpMethod.Delete);
}
