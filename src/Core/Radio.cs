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

    public async Task<List<RadioChoice>> RadioChoices(RadioOption option, Store store)
    {
        if (option.Kind is "artist" or "album") return await Task.Run(() => store.MixSeeds(scope, option.Kind));
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

    public async Task<JsonNode> StartRadio(RadioOption option, IReadOnlyList<string> choices, Store store)
    {
        if (option.Kind == "station") return await StationQueue(option.Key);
        if (option.Kind == "folder") return await StationQueue(choices.Single());
        string path;
        if (option.Kind is "artist" or "album")
        {
            var tracks = await Task.Run(() => store.MixTrackIds(scope, option.Kind, choices));
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
