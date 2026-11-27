using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mixamp.Core;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
    checks++;
    Console.WriteLine("PASS " + message);
}
void Reject(Action act, string message)
{
    try
    {
        act();
    }
    catch
    {
        Check(true, message);
        return;
    }
    throw new Exception(message);
}
var dir = Directory.CreateTempSubdirectory("mixamp-check-");
try
{
    var scope = new Scope("account", "fixture", "1");
    var store = new Store(dir.FullName);
    Track Make(int i) =>
        new(
            i.ToString(),
            $"Session {i:000000}",
            i % 7 == 0 ? "池田" : "Mira",
            "Various Artists",
            "Album " + i % 1000,
            "a" + i % 1000,
            "g",
            1,
            i % 12,
            2024,
            260000,
            "Jazz",
            "flac",
            768,
            "fixture:" + i,
            [new(i.ToString(), "/part/" + i, "/demo/" + i + ".flac", 12345, "flac")]
        );
    var track = Make(1);
    Check(ToneProfile.Choose("Auto","Jazz, Blues") == ToneProfile.Warm &&
        ToneProfile.Choose("Auto","Jazz; House") == ToneProfile.Flat && ToneProfile.Choose("Auto","") == ToneProfile.Flat &&
        ToneProfile.Choose("Auto","Unclassified") == ToneProfile.Flat && ToneProfile.Choose("Punch","") == ToneProfile.Punch,
        "auto EQ selects compatible tags, ambiguous and missing tags stay flat, manual override works");
    Check(Mixamp.ArtistOrganizer.Destination(track, new Mapping("/demo", ""), "AC/DC") == "/demo/AC_DC/Album 1/1.flac",
        "artist layout keeps album and filename, sanitizes separator");
    if (args.Contains("--artist-move"))
    {
        var unc = @"\\localhost\" + dir.FullName[0] + "$" + dir.FullName[2..];
        var maps = new[] { new Mapping("/demo", unc) };
        var source = Path.Combine(dir.FullName, "1.flac");
        var bytes = new byte[track.Parts[0].Size];
        Random.Shared.NextBytes(bytes);
        File.WriteAllBytes(source, bytes);
        var plan = Mixamp.ArtistOrganizer.Plan(track, maps, track.Artist);
        Directory.CreateDirectory(Path.GetDirectoryName(plan.Destination)!);
        File.WriteAllText(plan.Destination, "existing");
        Reject(() => Mixamp.ArtistOrganizer.Move(plan, maps), "artist move never overwrites an existing file");
        Check(File.Exists(source) && File.ReadAllText(plan.Destination) == "existing", "collision retains both files");
        File.Delete(plan.Destination);
        var secondPlan = Mixamp.ArtistOrganizer.Plan(track, maps, "New artist");
        Mixamp.ArtistOrganizer.Move(secondPlan, maps);
        Check(!File.Exists(source) && File.ReadAllBytes(secondPlan.Destination).SequenceEqual(bytes),
            "verified SMB move into newly created artist and album directories");
        var hash = Replacement.Digest(secondPlan.Destination);
        var entry = new Change(track, "Move", track.FilePath, secondPlan.ServerDestination, hash, hash,
            secondPlan.Source, secondPlan.Destination);
        store.Remember(scope,"test batch","Organize files by artist",entry);
        File.WriteAllText(source,"occupied");
        Reject(() => Mixamp.ArtistOrganizer.RestoreFile(entry,maps),"undo protects occupied original path");
        File.Delete(source);
        var reopened = new Store(dir.FullName);
        void ReplayMove(bool redo)
        {
            var step = reopened.LastChanges(scope, redo).Single();
            Mixamp.ArtistOrganizer.RestoreFile(step.Change,maps);
            reopened.CompleteChange(scope,step,redo);
        }
        ReplayMove(false);
        Check(File.ReadAllBytes(source).SequenceEqual(bytes) && !File.Exists(secondPlan.Destination) &&
            store.LastChanges(scope).Count==0, "persistent undo restores move");
        ReplayMove(true);
        Check(!File.Exists(source) && File.ReadAllBytes(secondPlan.Destination).SequenceEqual(bytes), "redo reapplies physical move");
        ReplayMove(false);
        var backup = Path.Combine(Path.GetDirectoryName(secondPlan.Source)!, "fixture.bak");
        File.Copy(secondPlan.Source, backup);
        File.WriteAllText(secondPlan.Source, "modified tags fixture");
        var tagChange = new Change(track, "Tags", track.FilePath, backup, hash,
            Replacement.Digest(secondPlan.Source), secondPlan.Source);
        Mixamp.ArtistOrganizer.RestoreFile(tagChange, maps);
        Check(File.ReadAllBytes(source).SequenceEqual(bytes), "tag undo restores verified backup on SMB");
        Mixamp.ArtistOrganizer.RestoreFile(tagChange.Reverse(), maps);
        Check(File.ReadAllText(source) == "modified tags fixture", "redo restores edited tags");
        Mixamp.ArtistOrganizer.RestoreFile(tagChange.Reverse().Reverse(), maps);
        Check(File.ReadAllBytes(source).SequenceEqual(bytes), "repeated tag undo retains both versions");
    }

    Check(Cue.Format(248999) == "4:08" && Cue.Format(3600100) == "60:00", "display whole minutes and seconds");
    var cue = new Cue(Cue.Parse("0:42"), Cue.Parse("4:08"));
    cue.Validate(track.Duration);
    Check(
        cue.End - cue.Start == 206000 && Cue.Parse("1:02:03.45") == 3723450,
        "original and clipped durations"
    );
    foreach (var bad in new[] { "-1:05", "0:60", "1:60:00", "0:00.1234", "1 .." })
        Reject(() => Cue.Parse(bad), "reject " + bad);
    Reject(() => new Cue(1, 1).Validate(100), "reject zero length");
    Reject(() => new Cue(null, 101).Validate(100), "reject stop beyond duration");
    store.Page(scope, [track, Make(2)], "a");
    Check(store.Complete(scope, "a", 2), "complete sync");
    store.Page(scope, [track with { Title = "Changed" }], "b");
    Check(
        !store.Complete(scope, "b", 2) && store.Search(scope).Count == 2,
        "interrupted sync retains unseen tracks"
    );
    Check(
        store.Search(scope, "Chang").Single().Title == "Changed"
            && store.Search(scope, "Session").Count == 1,
        "FTS update removes old words"
    );
    var other = scope with { Server = "other" };
    var history = scope with { Library = "history" };
    var firstChange = new Change(track,"Title","","","first","second");
    store.Remember(history,"batch","Rename",firstChange);
    store.Remember(history,"batch","Rename",firstChange with { Before="second", After="third" });
    foreach (var step in store.LastChanges(history)) store.CompleteChange(history,step,false);
    Check(new Store(dir.FullName).LastChanges(history,true).Select(x=>x.Change.Before).SequenceEqual(["second","third"]),
        "redo survives restart and replays the batch in forward order");
    store.Remember(history,"new","Rename",firstChange);
    Check(store.LastChanges(history,true).Count==0,"a new edit clears that library's redo branch");
    var mixes = scope with { Library = "mix" };
    store.Page(mixes,[track,Make(2),Make(3) with {ArtistId="h",Artist="Another artist"}],"mix");
    var seedIds = store.MixSeeds(mixes,"artist").Select(x=>x.Id).ToList();
    var mix = store.MixTrackIds(mixes,"artist",seedIds);
    Check(seedIds.Count==2 && mix.Count==3 && mix.Take(2).Contains("3") && mix.Distinct().Count()==3,
        "artist mix includes each chosen artist before repeating a seed");
    store.Page(other, [track], "a");
    Check(store.Search(other, "Changed").Count == 0, "server isolation");
    store.Complete(scope, "b", 1);
    Check(
        store.Search(scope).Count == 1 && store.Search(other).Count == 1,
        "confirmed removal is scoped"
    );
    store.SaveCue(scope, track, cue);
    Check(
        new Store(dir.FullName).ReadCue(scope, track) == cue
            && store.ReadCue(other, track) == new Cue(),
        "user cues survive reopen"
    );
    Check(
        store.ReadCue(scope, track with { Parts = [track.Parts[0] with { Size = 1 }] })
            == new Cue(),
        "replacement never inherits cue silently"
    );
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    File.Delete(Path.Combine(dir.FullName, "catalog.db"));
    store = new Store(dir.FullName);
    Check(store.ReadCue(scope, track) == cue, "cache rebuild preserves range");
    var rebuilt = track with { Id = "replacement-key" };
    store.Page(scope, [rebuilt], "rebuilt");
    Check(
        store.ReadCue(scope, rebuilt) == cue,
        "unique verified recording reattaches after id rebuild"
    );
    store.Page(scope, [rebuilt with { Id = "duplicate" }], "rebuilt");
    Check(
        store.ReadCue(scope, rebuilt) == new Cue(),
        "ambiguous recording never inherits old id cue"
    );
    using var api = new Plex(new Uri("http://127.0.0.1:32491"), "fixture-client", "fixture-only");
    Reject(() => api.Resolve("//example.com/escape"), "reject cross-origin media URL");
    Reject(() => MappedFiles.Resolve("/music/../escape", []), "reject traversal");
    Reject(
        () => MappedFiles.Resolve("/music-other/t.flac", [new("/music", @"\\nas\Music")]),
        "reject root prefix collision"
    );
    if (args.Contains("--scenario"))
    {
        string scenario = args[Array.IndexOf(args, "--scenario") + 1];
        int port = int.Parse(args[Array.IndexOf(args, "--scenario") + 2]);
        using var mock = new Plex(new Uri("http://127.0.0.1:" + port), "test", "fixture-only");
        var demo = new Scope("demo", "mixamp-demo", "1");
        if (scenario == "revoked")
        {
            try
            {
                await mock.Call("/identity");
                throw new Exception("Expected credential rejection.");
            }
            catch (HttpRequestException ex)
                when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                Check(true, "revoked credentials rejected");
            }
        }
        else
        {
            Check(
                (await mock.Call("/identity")).Str("machineIdentifier") == demo.Server,
                "isolated fixture identity"
            );
            var lib = new Library(mock, demo);
            if (scenario == "faults")
            {
                var sentinel = Make(9999);
                store.Page(demo, [sentinel], "old");
                try
                {
                    await lib.Sync(store, () => Task.CompletedTask, default);
                    throw new Exception("Expected partial sync failure.");
                }
                catch (HttpRequestException)
                {
                    Check(
                        store.Search(demo, "Session 009999").Single().Id == sentinel.Id,
                        "interrupted network scan retains an unseen cached row"
                    );
                }
                Check(
                    (await mock.Call("/fixture/rate")).Str("ready").ToLowerInvariant() == "true"
                        && (await mock.Call("/fixture/counters")).Num("retries") == 3,
                    "bounded 429 retry succeeds"
                );
                try
                {
                    await mock.Call("/fixture/redirect");
                    throw new Exception("Followed an origin-changing redirect.");
                }
                catch (HttpRequestException ex)
                    when (ex.StatusCode == System.Net.HttpStatusCode.Found) { }
                Check(
                    (await mock.Call("/fixture/counters")).Num("redirects") == 0,
                    "authenticated request never reaches redirect target"
                );
                try
                {
                    await mock.Range("/parts/1", 1000, 16, null, default);
                    throw new Exception("Accepted non-seekable response.");
                }
                catch (IOException)
                {
                    Check(true, "non-range source refused before clip playback");
                }
                try
                {
                    await lib.Stations(null);
                    throw new Exception("Expected radio denial.");
                }
                catch (HttpRequestException ex)
                    when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    Check(true, "station denial retained as permission error");
                }
            }
            if (scenario == "denied")
            {
                try
                {
                    await new Deletion(mock, demo, true).Review("1");
                    throw new Exception("Expected denied deletion.");
                }
                catch (IOException)
                {
                    Check(true, "disabled server deletion");
                }
                try
                {
                    await mock.SetFields(
                        "1",
                        await mock.Fresh("1"),
                        new() { ["title"] = "disposable" }
                    );
                    throw new Exception("Expected denied edit.");
                }
                catch (HttpRequestException ex)
                    when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    Check(true, "server refuses edits");
                }
            }
            if (scenario == "absent")
                Check((await lib.Stations(null)).Count == 0, "unavailable library station");
            if (scenario == "multi")
            {
                var delete = new Deletion(mock, demo, true);
                var review = await delete.Review("1");
                Check(
                    review.Track.Parts.Length == 2
                        && review.Track.Parts.Select(p => p.File).Distinct().Count() == 2,
                    "both versions listed before confirmation"
                );
            }
            if (scenario == "album")
            {
                var before = (await mock.Call("/library/metadata/a/children"))
                    .Items("Metadata")
                    .Select(Track.Read)
                    .ToArray();
                var otherAlbum = await mock.Fresh("4");
                await lib.AlbumArtist(before, "Various Artists", true, list =>
                    list.Count == before.Length
                );
                var after = (await mock.Call("/library/metadata/a/children"))
                    .Items("Metadata")
                    .Select(Track.Read)
                    .ToArray();
                Check(
                    before.All(c =>
                        after.Any(a =>
                            a.Id == c.Id
                            && a.ArtistDisplay == c.ArtistDisplay
                            && a.Artist == "Various Artists"
                        )
                    ),
                    "album grouping and track performers"
                );
                Check(
                    (await mock.Fresh("4")).Artist == otherAlbum.Artist,
                    "different album retains grouping"
                );
            }
        }
    }
    if (args.Contains("--mock"))
    {
        Check(
            (await api.Call("/identity")).Str("machineIdentifier") == "mixamp-demo",
            "verified disposable fixture identity"
        );
        var library = new Library(api, scope);
        int first = 0;
        await library.Sync(
            store,
            () =>
            {
                first = store.Search(scope).Count;
                return Task.CompletedTask;
            },
            default
        );
        Check(first > 0, "first page callback during sync");
        Check(!await library.Sync(store, () => Task.CompletedTask, default), "unchanged scan skips catalog writes");
        var t = await api.Fresh("1");
        var part = t.Parts.Single();
        var stream = new RangeStream(api, part.Key, part.Size);
        byte[] bytes = new byte[4];
        await stream.ReadExactlyAsync(bytes);
        Check(bytes.SequenceEqual("RIFF"u8.ToArray()), "authenticated first range");
        stream.Seek(44 + 42 * 16000, SeekOrigin.Begin);
        await stream.ReadExactlyAsync(bytes);
        Check(BitConverter.ToInt16(bytes, 2) > 1500, "seek directly to marker tone");
        await api.SetFields(scope.Library, t, new() { ["title"] = "Edited fixture" });
        Check(
            (await api.Fresh("1")).Title == "Edited fixture",
            "write title and reread through Plex adapter"
        );
        var metadata=await TrackMetadata.Open(api,scope,["2","3"]);
        Check(metadata.Count==2 && metadata.Fields.Single(f=>f.Key=="title").Value==null,"batch editor shows mixed titles");
        await metadata.Save(store,new(){["genre"]="Post-rock\nVocal, jazz",["parentIndex"]="5"});
        foreach(var step in store.LastChanges(scope,false))
        {
            var fresh=await api.Fresh(step.Change.Track.Id);
            Check(fresh.Title==step.Change.Track.Title && fresh.Disc==5 && fresh.Genre.Contains("Vocal, jazz"),"batch writes selected metadata fields only");
            await TrackMetadata.Restore(api,scope,step.Change);
            store.CompleteChange(scope,step,false);
            Check((await api.Fresh(fresh.Id)).Genre==step.Change.Track.Genre,"metadata undo restores the previous tag set");
        }
        var sonic=await library.StartRadio(new("sonic","","Similar radio"),["1"],store);
        var neighbors=sonic.Items("Metadata").Select(Track.Read).ToArray();
        Check(neighbors.Select(t=>t.Id).SequenceEqual(new[]{"1","2","3","4"}),"sonic radio starts with the seed and Plex nearest tracks");
        var tail=await library.ContinueSonic(sonic.Str("playQueueID"),neighbors[^1].QueueItem,neighbors[^1].Id,neighbors.Select(t=>t.Id).ToArray());
        Check(tail.Items("Metadata").Last().Str("ratingKey")=="7","sonic radio extends using the last track");
        var station = (await library.Stations(null)).Single();
        var offers = await library.RadioStations();
        var genre = offers.Single(o=>o.Name=="Genre Radio");
        var jazz = (await library.RadioChoices(genre,store)).Single();
        Check((await library.StartRadio(genre,[jazz.Id],store)).Items("Metadata").Any(), "server-advertised genre starts a filtered queue");
        var builder = offers.Single(o=>o.Name=="Album Mix Builder");
        var albums = await library.RadioChoices(builder,store);
        var mixed = (await library.StartRadio(builder,albums.Select(a=>a.Id).ToList(),store)).Items("Metadata").Select(Track.Read).ToArray();
        Check(mixed.Length>1 && mixed.Take(albums.Count).Select(t=>t.AlbumId).Distinct().Count()==albums.Count,
            "Plex mix queue preserves interleaved seed recordings");
        var queue = await library.StationQueue(station.Str("key"));
        var firstBatch = queue.Items("Metadata").Select(Track.Read).ToArray();
        var more = await library.ContinueQueue(queue.Str("playQueueID"), firstBatch[^1].QueueItem);
        Check(
            firstBatch.Select(x => x.Id).Distinct().Count() < firstBatch.Length
                && more.Items("Metadata").Last().Str("playQueueItemID") != firstBatch[^1].QueueItem,
            "station continuation and repeated recording identity"
        );
        var empty = (await library.CreatePlaylist("Empty")).Items("Metadata").Single().Str("ratingKey");
        Check(!(await library.PlaylistItems(empty)).Items("Metadata").Any(), "create an empty playlist");
        await library.DeletePlaylist(empty);
        Check(!(await library.Playlists()).Any(p => p.Str("ratingKey") == empty), "remove whole playlist");
        var pl = (await library.AddToPlaylist(null, "Fixture playlist", t))
            .Items("Metadata")
            .Single()
            .Str("ratingKey");
        await library.AddToPlaylist(pl, "", t);
        var items = (await library.PlaylistItems(pl)).Items("Metadata").Select(Track.Read).ToList();
        await library.Move(pl, items[1].PlaylistItem, "0");
        Check(
            (await library.PlaylistItems(pl)).Items("Metadata").First().Str("playlistItemID")
                == items[1].PlaylistItem,
            "move duplicate by playlist entry"
        );
        await library.Remove(pl, items[0].PlaylistItem);
        Check(
            (await library.PlaylistItems(pl)).Items("Metadata").Count() == 1
                && (await api.Fresh("1")).Parts.Length == 1,
            "playlist removal retains source"
        );
        Check(await library.Sync(store, () => Task.CompletedTask, default)
            && store.Search(scope, "Edited fixture").Any(row => row.Id == t.Id), "incremental track edit");
        var deletion = new Deletion(api, new Scope("demo", "mixamp-demo", "1"), true);
        long submitted = (await api.Call("/fixture/deletes")).Num("count");
        var review = await deletion.Review("4");
        Check(
            review.Track.Parts.Single().File == "/demo/track-4.wav",
            "review exact disposable source"
        );
        Check(await deletion.Commit(review), "delete once and reconcile through metadata reread");
        Check(
            (await api.Call("/fixture/deletes")).Num("count") == submitted + 1,
            "exactly one deletion request after lost acknowledgement"
        );
        Check(await library.Sync(store, () => Task.CompletedTask, default)
            && !store.Revisions(scope).ContainsKey("4"), "incremental removal after complete scan");
        try
        {
            await api.Range("/parts/4", 0, 1, null, default);
            throw new Exception("Deleted part was readable.");
        }
        catch (IOException)
        {
            Check(true, "removed mock media part unavailable");
        }
    }
    string original = Path.Combine(dir.FullName, "original.flac"),
        temporary = Path.Combine(dir.FullName, "staged.flac");
    File.WriteAllText(original, "original disposable payload");
    File.WriteAllText(temporary, "verified edited copy");
    string digest = Replacement.Digest(original);
    var journal = new Replacement(store, scope, "fixture");
    Reject(
        () =>
            journal.Commit(original, digest, temporary, "change", (a, b, c) =>
                throw new IOException("fixture disconnect")
            ),
        "disconnect before replace"
    );
    Check(
        journal.Reconcile(original, "change") == null && Replacement.Digest(original) == digest,
        "unapplied change retains original"
    );
    Reject(() => journal.Commit(original, digest, temporary, "change", (a, b, c) =>
            {
                File.Replace(a, b, c);
                throw new IOException("fixture acknowledgement lost");
            }), "disconnect after replace");
    Check(
        new Replacement(new Store(dir.FullName), scope, "fixture").Reconcile(original, "change")
            != null
            && Replacement.Digest(temporary + ".bak") == digest,
        "reopened journal verifies completed half and original backup"
    );
    File.WriteAllText(original, "external edit");
    Reject(
        () => journal.Reconcile(original, "change"),
        "external edit after disconnect needs review"
    );
    if (args.Contains("--smb-fixture"))
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        string share = args[Array.IndexOf(args, "--smb-fixture") + 1];
        if (
            !System.Text.RegularExpressions.Regex.IsMatch(
                share,
                @"^\\\\localhost\\MixampFixture-[a-f0-9]{32}$"
            )
        )
            throw new ArgumentException("Expected the isolated loopback fixture share.");
        using var fixture = new Plex(
            new Uri("http://127.0.0.1:32493"),
            "disposable-smb",
            "fixture-only"
        );
        var target = new Scope("test", "mixamp-demo", "1");
        Check(
            (await fixture.Call("/identity")).Str("machineIdentifier") == target.Server,
            "isolated SMB fixture server"
        );
        var item = await fixture.Fresh("1");
        store.Set(
            target.Key,
            "mappings",
            JsonSerializer.Serialize(new[] { new Mapping("/demo", share) })
        );
        var source = MappedFiles.Resolve(item.Parts.Single().File, [new Mapping("/demo", share)]);
        string beforeHash = Replacement.Digest(source);
        var writer = new Mixamp.FileWriter(store, target, fixture);
        string result = await writer.Write(item, new() { ["ALBUMARTIST"] = "Various Artists" });
        using (var media = TagLib.File.Create(source))
            Check(
                media.Tag.AlbumArtists.SequenceEqual(["Various Artists"])
                    && media.Tag.Performers.SequenceEqual(["Mira", "池田"]),
                "reopen UNC file and verify performers and album artist"
            );
        Check(
            Directory
                .EnumerateFiles(Path.GetDirectoryName(source)!, ".mixamp-*.bak")
                .Any(f => Replacement.Digest(f) == beforeHash),
            "SMB replacement retains verified backup"
        );
        Reject(
            () => MappedFiles.Resolve("/demo/../outside.flac", [new Mapping("/demo", share)]),
            "UNC mapping rejects traversal"
        );
    }
    int tag = Array.IndexOf(args, "--tag");
    if (tag >= 0)
    {
        TagEditor.EditCopy(args[tag + 1], new() { ["ALBUMARTIST"] = "Various Artists" });
        Check(true, "verified tag update " + Path.GetExtension(args[tag + 1]));
    }
    int bench = Array.IndexOf(args, "--benchmark");
    if (bench >= 0)
    {
        var benchScope = scope with { Server = "bench" };
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 100000; i += 256)
            store.Page(
                benchScope,
                Enumerable.Range(i, Math.Min(256, 100000 - i)).Select(Make).ToArray(),
                "bench"
            );
        Check(store.Complete(benchScope, "bench", 100000), "100k distinct catalog rows");
        double initial = watch.Elapsed.TotalMilliseconds;
        var raw = new List<object>();
        var durations = new List<double>();
        for (int i = 0; i < 120; i++)
        {
            string term = (i % 5) switch
            {
                0 => $"Session {i * 811:000000}",
                1 => $"Album {i % 1000}",
                2 => "池田",
                3 => "Various Artists",
                _ => "missing" + i,
            };
            watch.Restart();
            var rows = store.Search(benchScope, term);
            double ms = watch.Elapsed.TotalMilliseconds;
            durations.Add(ms);
            raw.Add(
                new
                {
                    term,
                    ms,
                    rows = rows.Count,
                }
            );
        }
        var report = new
        {
            workload = "Release C# store, generated data, first 160 rows, no network or WPF",
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            runtime = Environment.Version.ToString(),
            processors = Environment.ProcessorCount,
            initialMs = initial,
            searchP95Ms = durations.Order().ElementAt(113),
            raw,
        };
        File.WriteAllText(
            args[bench + 1],
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })
        );
    }
    Console.WriteLine($"{checks} checks passed");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    dir.Delete(true);
}
