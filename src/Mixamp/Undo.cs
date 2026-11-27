using System.Text.Json;

namespace Mixamp;

sealed partial class Session
{
    static string Field(string s) => s == "0" ? "" : s;
    internal static string FieldValue(Track t, string k) => k switch
    {
        "title" => t.Title, "originalTitle" => t.Performer, "index" => t.Number == 0 ? "" : t.Number.ToString(),
        _ => throw new ArgumentException("Unsupported field.")
    };

    void MoveWithUndo(ArtistMove plan, Mapping[] maps, Scope scope, string batch, string name)
    {
        string hash = Replacement.Digest(MappedFiles.Resolve(plan.Track.FilePath, maps));
        var id = Store.Remember(scope, batch, name, new(plan.Track, "Move", plan.Track.FilePath,
            plan.ServerDestination, hash, hash, plan.Source, plan.Destination));
        try { ArtistOrganizer.Move(plan, maps); }
        catch
        {
            if (File.Exists(plan.Source)) Store.Forget(scope, id);
            throw;
        }
    }

    async Task RenameFile(Track selected, Plex api, Scope scope)
    {
        var track = await api.Fresh(selected.Id);
        if (api != Api || track.Identity != selected.Identity || track.LibraryId != scope.Library || track.Parts.Length != 1)
            throw new IOException("Select a single mapped media file in this library.");
        var maps = JsonSerializer.Deserialize<Mapping[]>(Store.Get(scope.Key, "mappings") ?? "[]")!;
        var path = MappedFiles.Resolve(track.FilePath, maps);
        string name = Dialogs.Input("Rename file (keep the extension)", Path.GetFileName(path));
        if (name != Path.GetFileName(name) || !string.Equals(Path.GetExtension(name), Path.GetExtension(path), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Enter a filename with the original extension.");
        var target = track.FilePath[..(track.FilePath.LastIndexOf('/')+1)] + name;
        var destination = MappedFiles.Resolve(target, maps, allowMissing:true);
        if (Path.Exists(destination)) throw new IOException("That filename is already in use.");
        var move = new ArtistMove(track, path, destination, target, File.GetLastWriteTimeUtc(path));
        await Task.Run(() => MoveWithUndo(move, maps, scope, Guid.NewGuid().ToString(), "Rename file"));
        Store.InvalidateMovedPaths(scope, [track]);
        Status = "File renamed. Undo is available in Edit.";
        await api.Call($"/library/sections/{Plex.E(scope.Library)}/refresh");
    }

    async Task ReplayHistory(bool redo)
    {
        if (Api == null || batchBusy) return;
        var api = Api;
        var scope = Scope;
        string action = redo ? "Redo" : "Undo";
        var steps = Store.LastChanges(scope, redo);
        if (steps.Count == 0) { Status = $"No changes to {action.ToLowerInvariant()} in this library."; return; }
        if (!Dialogs.Review(action + " " + steps[0].Name, redo ? "Reapply these changes?" : "Restore the previous state of these changes?",
            steps.Select(s => s.Change.Type == "Move" ? $"{s.Change.To} → {s.Change.From}" : s.Change.Track.Title), action)) return;
        batchBusy = true;
        int restored = 0;
        bool scan = false;
        try
        {
            foreach (var step in steps)
            {
                if (api != Api) break;
                var c = step.Change;
                switch (c.Type)
                {
                    case "Move":
                    case "Tags":
                        var maps = JsonSerializer.Deserialize<Mapping[]>(Store.Get(scope.Key, "mappings") ?? "[]")!;
                        await Task.Run(() => ArtistOrganizer.RestoreFile(c, maps));
                        Store.InvalidateMovedPaths(scope, [c.Track]);
                        scan = true;
                        break;
                    case "Plex":
                        var track = await api.Fresh(c.Track.Id);
                        if (track.Identity != c.Track.Identity || track.LibraryId != scope.Library)
                            throw new IOException("The recording has changed.");
                        var before = JsonSerializer.Deserialize<Dictionary<string,string>>(c.Before)!;
                        var after = JsonSerializer.Deserialize<Dictionary<string,string>>(c.After)!;
                        foreach (var k in before.Keys)
                        {
                            var current = FieldValue(track, k);
                            if (current != (k == "index" ? Field(before[k]) : before[k]) && current != (k == "index" ? Field(after[k]) : after[k]))
                                throw new IOException("A Plex field changed after this edit.");
                        }
                        await api.SetFields(scope.Library, track, before);
                        break;
                    case "Cue":
                        var cue = Store.ReadCue(scope, c.Track);
                        if (cue != JsonSerializer.Deserialize<Cue>(c.Before) && cue != JsonSerializer.Deserialize<Cue>(c.After))
                            throw new IOException("The playback range changed after this edit.");
                        Store.SaveCue(scope, c.Track, JsonSerializer.Deserialize<Cue>(c.Before)!);
                        break;
                    default: throw new IOException("Unknown undo operation.");
                }
                Store.CompleteChange(scope, step, redo);
                restored++;
            }
        }
        finally
        {
            batchBusy = false;
            if (api == Api)
            {
                if (scan) await api.Call($"/library/sections/{Plex.E(scope.Library)}/refresh");
                await Sync();
                Status = $"{action} · {restored} of {steps.Count} changes.";
            }
        }
    }

}
