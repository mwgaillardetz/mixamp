using System.Text.Json;

namespace Mixamp;

sealed partial class Session
{
    bool batchBusy;

    async Task BatchFiles(string command)
    {
        if (batchBusy || Api == null || (command == "Delete tracks from server" && !owned)) return;
        var tracks = Selection().DistinctBy(t => t.Id).ToArray();
        if (tracks.Length == 0) return;
        if (tracks.Any(t => t.Id.Length == 0 || t.LibraryId is { Length: > 0 } && t.LibraryId != Scope.Library))
            throw new IOException("Select tracks from the connected music library.");
        batchBusy = true;
        try
        {
            if (command == "Delete tracks from server") await DeleteTracks(tracks, Api, Scope);
            else if (command == "Rename file") await RenameFile(tracks.Single(), api: Api, scope: Scope);
            else await OrganizeTracks(tracks, Api, Scope);
        }
        finally { batchBusy = false; }
    }

    async Task DeleteTracks(Track[] tracks, Plex api, Scope scope)
    {
        var deletion = new Deletion(api, scope, owned);
        var reviews = new List<DeletionReview>();
        foreach (var track in tracks)
        {
            Status = $"Reviewing deletion · {reviews.Count + 1:N0} / {tracks.Length:N0}";
            var review = await deletion.Review(track.Id);
            if (Api != api) return;
            if (track.Identity != review.Track.Identity)
                throw new IOException($"The files for {track.Title} changed. Sync and review again.");
            reviews.Add(review);
        }
        var entries = reviews.SelectMany(r => r.Track.Parts.Select(p => $"{r.Track.Title}   ·   {p.File}"));
        if (!Dialogs.Review("Delete tracks from server",
            $"Permanently delete {reviews.Count:N0} selected tracks and their media files from {serverName} / {LibraryName}?",
            entries, $"Delete {reviews.Count:N0} tracks"))
        {
            Status = "Deletion canceled.";
            return;
        }
        var mappings = JsonSerializer.Deserialize<Mapping[]>(Store.Get(scope.Key, "mappings") ?? "[]")!;
        int verifiedOnShare = 0;
        int removed = 0;
        var details = new List<string>();
        foreach (var review in reviews)
        {
            if (Api != api) break;
            try
            {
                Status = $"Deleting · {removed + 1:N0} / {reviews.Count:N0}";
                if (!await deletion.Commit(review))
                    throw new IOException("Plex still reports the track.");
                removed++;
                if (mappings.Length > 0 && await Task.Run(() => review.Track.Parts.All(p =>
                {
                    try { MappedFiles.Resolve(p.File, mappings); return false; }
                    catch (FileNotFoundException) { return true; }
                    catch (DirectoryNotFoundException) { return false; }
                    catch (IOException) { return false; }
                    catch (UnauthorizedAccessException) { return false; }
                }))) verifiedOnShare++;
                if (Api != api) break;
                if (Current?.Id == review.Track.Id) Stop();
                for (int i = Queue.Count - 1; i >= 0; i--)
                    if (Queue[i].Id == review.Track.Id)
                    {
                        Queue.RemoveAt(i);
                        if (i <= index) index--;
                    }
            }
            catch (Exception ex)
            {
                details.Add($"{review.Track.Title}   ·   {Message(ex)}");
                break;
            }
        }
        if (Api == api)
        {
            try
            {
                await Sync();
                if (Section == "Playlists") await PlaylistRows();
            }
            catch (Exception ex) { details.Add("Library refresh · " + Message(ex)); }
        }
        var summary = $"Plex confirmed deletion of {removed:N0} of {reviews.Count:N0} tracks.";
        if (verifiedOnShare > 0) summary += $" Verified on the mapped share: {verifiedOnShare:N0}.";
        Status = summary;
        if (details.Count > 0) Dialogs.Review("Deletion results", summary, details, null);
    }

    async Task OrganizeTracks(Track[] tracks, Plex api, Scope scope)
    {
        var maps = JsonSerializer.Deserialize<Mapping[]>(Store.Get(scope.Key, "mappings") ?? "[]")!;
        if (maps.Length == 0) throw new IOException("Set up Library → Share mapping for the music folder first.");
        int mode = Dialogs.Choose("Choose the artist folder for each file",
            ["Album artist (keep albums together)", "Track artist", "One artist folder for all selected tracks"]);
        string custom = mode == 2 ? Dialogs.Input("Artist folder name", tracks[0].ArtistDisplay) : "";
        if (mode == 2 && custom.Length == 0) throw new IOException("Enter an artist folder name.");
        string Artist(Track t) => mode switch { 0 => string.IsNullOrWhiteSpace(t.Artist) ? t.ArtistDisplay : t.Artist,
            1 => t.ArtistDisplay, _ => custom };

        Status = $"Checking {tracks.Length:N0} files on the mapped share…";
        var skipped = new List<string>();
        var plans = await Task.Run(() =>
        {
            var candidates = new List<ArtistMove>();
            foreach (var track in tracks)
                try { candidates.Add(ArtistOrganizer.Plan(track, maps, Artist(track))); }
                catch (Exception ex) { skipped.Add($"{track.Title}   ·   {Message(ex)}"); }
            var conflicts = candidates.GroupBy(p => p.Destination, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1).SelectMany(g => g)
                .Concat(candidates.GroupBy(p => p.Source, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1).SelectMany(g => g)).ToHashSet();
            foreach (var p in conflicts) skipped.Add($"{p.Track.Title}   ·   Shared source or destination in selection.");
            return candidates.Where(p => !conflicts.Contains(p)).ToList();
        });
        if (Api != api) return;
        var preview = plans.Select(p => $"{p.Track.Title}\n{p.Source}\n→ {p.Destination}")
            .Concat(skipped.Select(s => "Skip · " + s));
        var summary = $"{plans.Count:N0} files to move into artist and album folders. {skipped.Count:N0} skipped. Filenames and audio tags are preserved.";
        if (!Dialogs.Review("Organize files by artist", summary, preview, plans.Count == 0 ? null : $"Move {plans.Count:N0} files"))
        {
            Status = "No files moved.";
            return;
        }
        var results = new List<string>();
        string batch = Guid.NewGuid().ToString();
        var moved = new List<Track>();
        foreach (var plan in plans)
        {
            if (api != Api) break;
            try
            {
                Status = $"Moving file {results.Count + 1:N0} / {plans.Count:N0}";
                var fresh = await api.Fresh(plan.Track.Id);
                if (api != Api) break;
                if (fresh.LibraryId != scope.Library || fresh.Identity != plan.Track.Identity ||
                    Artist(fresh) != Artist(plan.Track) || fresh.Album != plan.Track.Album)
                    throw new IOException("Track changed after review.");
                var currentMaps = JsonSerializer.Deserialize<Mapping[]>(Store.Get(scope.Key, "mappings") ?? "[]")!;
                await Task.Run(() => MoveWithUndo(plan, currentMaps, scope, batch, "Organize files by artist"));
                moved.Add(fresh);
                results.Add($"Moved · {plan.Destination}");
            }
            catch (Exception ex) { results.Add($"{plan.Source}   ·   {Message(ex)}"); }
        }
        string scan = "";
        if (moved.Count > 0) await Task.Run(() => Store.InvalidateMovedPaths(scope, moved));
        if (moved.Count > 0 && Api == api)
        {
            try
            {
                await api.Call($"/library/sections/{Plex.E(scope.Library)}/refresh");
                Status = "Waiting for Plex to index the moved files…";
                bool scanning = true;
                for (int i = 0; i < 12 && scanning && api == Api; i++)
                {
                    await Task.Delay(1500);
                    var sections = await api.Call("/library/sections");
                    scanning = sections.Items("Directory").FirstOrDefault(n => n.Str("key") == scope.Library)?
                        .Str("refreshing").ToLowerInvariant() is "1" or "true";
                }
                if (api == Api)
                {
                    await Sync();
                    // A scheduled server scan may still expose the old media paths.
                    await Task.Run(() => Store.InvalidateMovedPaths(scope, moved));
                }
                scan = scanning ? " Plex is still indexing. Sync again when its scan finishes." : " Library refreshed.";
            }
            catch (Exception ex) { scan = " Library refresh incomplete. " + Message(ex); }
        }
        Status = $"Moved {moved.Count:N0} of {plans.Count:N0} reviewed files.{scan}";
        Dialogs.Review("File move results", Status, results.Concat(skipped), null);
    }
}
