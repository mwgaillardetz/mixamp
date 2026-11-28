using System.Text.Json.Nodes;

namespace Mixamp.Core;

public record RadioOption(string Kind, string Key, string Name, string Filter = "");
public record RadioChoice(string Id, string Name);

public sealed partial class Library
{
    public async Task<List<RadioOption>> RadioStations(Track? artist = null)
    {
        var rows = await Stations(artist);
        var stations = rows.Where(x => x.Str("title") != "" && x.Str("key").StartsWith('/'))
            .DistinctBy(x => x.Str("key"))
            .Select(x => new RadioOption(x.Str("type") == "directory" || x["directory"]?.ToString() == "1" ? "folder" : "station",
                x.Str("key"), x.Str("title"))).ToList();
        if (artist != null) return stations;
        var filters = await TrackRadioFilters();
        foreach (var filter in filters)
        {
            var field = filter.Str("filter");
            var facet = field.Split('.').Last();
            if (facet is not ("genre" or "style" or "mood" or "decade")) continue;
            var name = char.ToUpperInvariant(facet[0]) + facet[1..] + " Radio";
            if (!stations.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                stations.Add(new("filter", filter.Str("key"), name, field));
        }
        stations.Add(new("artist", "", "Artist Mix Builder"));
        stations.Add(new("album", "", "Album Mix Builder"));
        return stations;
    }

    async Task<List<JsonNode>> TrackRadioFilters()
    {
        try
        {
            var meta = await api.Call($"/library/sections/{E(scope.Library)}/all?type=10&includeMeta=1&includeAdvanced=1&X-Plex-Container-Start=0&X-Plex-Container-Size=0");
            return meta["Meta"]?.Items("Type").Where(t => t.Str("type") is "track" or "10")
                .SelectMany(t => t.Items("Filter")).Where(f => f.Str("key").StartsWith('/')).ToList() ?? [];
        }
        catch (HttpRequestException e) when (e.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.BadRequest)
        { return []; }
    }

    public async Task<List<RadioChoice>> RadioChoices(RadioOption option, Store? store)
    {
        if (option.Kind is "artist" or "album")
        {
            if (store != null) return await Task.Run(() => store.MixSeeds(scope, option.Kind));
            List<RadioChoice> choices = [];
            int type = option.Kind == "artist" ? 8 : 9;
            int offset = 0;
            while (true)
            {
                var page = await api.Call($"/library/sections/{E(scope.Library)}/all?type={type}&X-Plex-Container-Start={offset}&X-Plex-Container-Size=256");
                var rows = page.Items("Metadata").ToArray();
                if (page["offset"] != null && page.Num("offset") != offset) throw new IOException("Plex returned a different page.");
                choices.AddRange(rows.Select(n => new RadioChoice(n.Str("ratingKey"), option.Kind=="album" && n.Str("parentTitle")!="" ?
                    n.Str("title")+" — "+n.Str("parentTitle") : n.Str("title"))));
                offset += rows.Length;
                if (rows.Length==0 || (page["totalSize"]!=null ? offset>=page.Num("totalSize") : rows.Length<256)) break;
            }
            return choices;
        }
        if (option.Kind == "folder")
        {
            var folder = await api.Call(option.Key);
            return folder.Items("Metadata").Concat(folder.Items("Directory"))
                .Where(n => n.Str("key").StartsWith('/')).Select(n => new RadioChoice(n.Str("key"), n.Str("title"))).ToList();
        }
        if (option.Kind != "filter")
            throw new ArgumentException("Unknown radio category.");
        var result = await api.Call(option.Key);
        return result.Items("Directory").Select(n => new RadioChoice(n.Str("key"), n.Str("title"))).ToList();
    }

    async Task<List<string>> SonicTracks(string id)
    {
        const int stationSize = 30;
        var seed = await api.Fresh(id);
        if (seed.LibraryId != scope.Library) throw new IOException("Select a track in the connected library.");
        List<string> tracks = [id];
        HashSet<string> keys = [id], recordings = [seed.Guid];
        // Prefer the seed's nearest tracks. Explore their neighbors if Plex returns a short list.
        for (int i = 0; i < tracks.Count && tracks.Count < stationSize; i++)
        {
            JsonNode near;
            try { near = await api.Call("/library/metadata/" + E(tracks[i]) + "/nearest?limit=50"); }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.BadRequest)
            {
                if (i == 0) throw new IOException("Sonic radio requires a Plex library with completed sonic analysis.", ex);
                continue;
            }
            foreach (var entry in near.Items("Metadata"))
            {
                var key = entry.Str("ratingKey");
                var library = entry.Str("librarySectionID");
                var guid = entry.Str("guid");
                if (entry.Str("type") != "track" || library != "" && library != scope.Library ||
                    key == "" || !keys.Add(key) || guid != "" && !recordings.Add(guid)) continue;
                tracks.Add(key);
                if (tracks.Count == stationSize) break;
            }
        }
        if (tracks.Count == 1) throw new IOException("Plex found no sonically similar tracks. Check sonic analysis for this library.");
        return tracks;
    }

    public async Task<JsonNode> StartRadio(RadioOption option, IReadOnlyList<string> choices, Store? store)
    {
        if (option.Kind == "station") return await StationQueue(option.Key);
        if (option.Kind == "folder") return await StationQueue(choices.Single());
        string path;
        if (option.Kind == "sonic")
        {
            var ids = await SonicTracks(choices.Single());
            path = "/library/metadata/" + string.Join(",", ids.Select(E));
        }
        else if (option.Kind is "artist" or "album")
        {
            List<string> tracks;
            if (store != null) tracks = await Task.Run(() => store.MixTrackIds(scope, option.Kind, choices));
            else
            {
                if (choices.Count is 0 or >100) throw new ArgumentException("Choose between 1 and 100 seeds.");
                List<Queue<string>> groups = [];
                foreach (var seed in choices.Distinct())
                {
                    var page = await api.Call($"/library/sections/{E(scope.Library)}/all?type=10&{option.Kind}.id={E(seed)}&sort=random&X-Plex-Container-Size=100");
                    var group = new Queue<string>(page.Items("Metadata").Select(n => n.Str("ratingKey")).Where(id => id!="").Take(100));
                    if (group.Count>0) groups.Add(group);
                }
                tracks = Store.InterleaveMix(groups);
            }
            if (tracks.Count == 0) throw new IOException("No tracks are available for this mix.");
            path = "/library/metadata/" + string.Join(",", tracks.Select(E));
        }
        else if (option.Kind == "filter")
            path = $"/library/sections/{E(scope.Library)}/all?type=10&{E(option.Filter)}={E(choices.Single())}&sort=random";
        else throw new ArgumentException("Unknown radio selection.");
        var uri = "library:///directory/" + E(path);
        return await api.Call("/playQueues?type=audio&uri=" + E(uri), HttpMethod.Post);
    }
}
