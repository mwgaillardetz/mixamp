using System.Text.Json;
using System.Text.Json.Nodes;
using Mixamp.Core;
using Microsoft.AspNetCore.Http;

namespace Mixamp;

sealed record Request(string Action, string Id = "", string Value = "", string Extra = "", string[]? Ids = null);

sealed class MacLibrary : IDisposable
{
    readonly Store store;
    readonly Account account;
    readonly SemaphoreSlim gate = new(1);
    readonly string tmp = Directory.CreateTempSubdirectory("mixamp-player-").FullName;
    Scope scope;
    Plex? api, candidate;
    JsonNode[] servers = [], sections = [];
    string user = "", machine = "";
    bool owner, candidateOwner;
    sealed record StreamPart(string Name, Plex Server, string Key, long Size);
    volatile StreamPart? remote;
    string? mediaKey, mediaPath;
    string progress = "Ready";
    string planId = "";
    Func<Task>? pending;
    List<RadioOption> radioStations = [];
    List<RadioChoice> radioChoices = [];
    RadioOption? selectedRadio;
    Library Library => new(api ?? throw new IOException("Connect to Plex first."), scope);
    public string Progress => Volatile.Read(ref progress);
    public MacLibrary(string directory)
    {
        store = new Store(directory);
        account = new(store, Keychain.Read, Keychain.Write);
        scope = JsonSerializer.Deserialize<Scope>(store.Get("installation","lastScope") ?? "null") ?? new("","","");
    }
    async Task Sync()
    {
        try
        {
            await Library.Sync(store, () => Task.CompletedTask, default, (n,total,diff) =>
                Volatile.Write(ref progress, $"{(diff ? "Checking for changes" : "Loading library")} · {n:N0} / {Math.Max(n,total):N0}"));
        }
        finally { Volatile.Write(ref progress, "Ready"); }
    }
    async Task SelectServer(string id)
    {
        var server = servers.Single(s => s.Str("clientIdentifier") == id);
        candidate?.Dispose();
        candidate = await account.Connect(server,default);
        machine = id;
        candidateOwner = server["owned"]?.GetValue<bool>() == true;
        sections = (await candidate.Call("/library/sections")).Items("Directory").Where(l => l.Str("type") == "artist").ToArray();
    }
    void SelectLibrary(string id)
    {
        _ = sections.Single(s => s.Str("key") == id);
        api?.Dispose(); api = candidate; candidate = null;
        owner = candidateOwner; scope = new(user,machine,id);
        pending = null; mediaKey = null; remote = null;
        radioStations.Clear(); radioChoices.Clear(); selectedRadio = null;
        store.Set("installation","lastScope",JsonSerializer.Serialize(scope));
    }
    async Task Demo()
    {
        candidate?.Dispose();
        candidate = new Plex(new Uri("http://127.0.0.1:32491"),account.Identifier,"fixture-only");
        if ((await candidate.Call("/identity")).Str("machineIdentifier") != "mixamp-demo") throw new IOException("Demo server unavailable.");
        sections = (await candidate.Call("/library/sections")).Items("Directory").ToArray();
        machine = "mixamp-demo"; user = "demo"; candidateOwner = true; SelectLibrary("1");
    }
    public async Task<object> Command(Request r)
    {
        await gate.WaitAsync();
        try
        {
            switch(r.Action)
            {
                case "cached": return store.Search(scope, limit:-1);
                case "resume":
                    if (scope.Library == "") return false;
                    if (scope.Account == "demo" && scope.Server == "mixamp-demo") await Demo();
                    else
                    {
                        var discovery = await account.Discover(default,false);
                        if (discovery.AccountId != scope.Account) throw new IOException("Reconnect to the saved account.");
                        user = discovery.AccountId; servers = discovery.Servers;
                        await SelectServer(scope.Server); SelectLibrary(scope.Library);
                    }
                    await Sync(); return true;
                case "connect":
                    (user,servers) = await account.Discover(default);
                    return servers.Select(s => new { id=s.Str("clientIdentifier"), name=s.Str("name") });
                case "server":
                    await SelectServer(r.Id); return sections.Select(s => new { id=s.Str("key"), name=s.Str("title") });
                case "library": SelectLibrary(r.Id); await Sync(); return true;
                case "demo": await Demo(); await Sync(); return true;
                case "sync": await Sync(); return true;
                case "stations":
                    radioStations = await Library.RadioStations();
                    return radioStations;
                case "radioChoices":
                    selectedRadio = radioStations[int.Parse(r.Id)];
                    radioChoices = await Library.RadioChoices(selectedRadio,store);
                    return radioChoices;
                case "startRadio":
                    var station = radioStations[int.Parse(r.Id)];
                    var choices = r.Ids ?? [];
                    if (station.Kind != "station" && (station != selectedRadio || choices.Except(radioChoices.Select(c=>c.Id)).Any()))
                        throw new IOException("Reopen this station and choose again.");
                    var radio = await Library.StartRadio(station,choices,store);
                    return new { id=radio.Str("playQueueID"), tracks=radio.Items("Metadata").Select(Track.Read).ToList() };
                case "continueRadio":
                    return (await Library.ContinueQueue(r.Id,r.Value)).Items("Metadata").Select(Track.Read).ToList();
                case "playlists": return (await Library.Playlists()).Select(p => new { id=p.Str("ratingKey"), name=p.Str("title") });
                case "playlist": return (await Library.PlaylistItems(r.Id)).Items("Metadata").Select(Track.Read);
                case "newList": await Library.CreatePlaylist(Required(r.Value)); return true;
                case "removeList": await Library.DeletePlaylist(r.Id); return true;
                case "addList":
                    foreach (var id in r.Ids ?? []) await Library.AddToPlaylist(r.Id,"",await Fresh(id));
                    return true;
                case "removeEntry": await Library.Remove(r.Id,r.Value); return true;
                case "mapping":
                    var root=Required(r.Value).TrimEnd('/'); var mount=Path.GetFullPath(Required(r.Extra));
                    if (!r.Value.StartsWith('/') || !Directory.Exists(mount) || mount == "/") throw new IOException("Choose an existing mounted music directory.");
                    store.Set(scope.Key,"mount",JsonSerializer.Serialize(new Mapping(root,mount))); return true;
                case "cue":
                    var cueTrack=await Fresh(r.Id);
                    var cue=r.Value==""&&r.Extra=="" ? new Cue() : new Cue(r.Value==""?null:Cue.Parse(r.Value),r.Extra==""?null:Cue.Parse(r.Extra));
                    cue.Validate(cueTrack.Duration);
                    var saved=store.ReadCue(scope,cueTrack);
                    if(saved!=cue)
                    {
                        store.Remember(scope,Guid.NewGuid().ToString(),"Edit playback range",new(cueTrack,"Cue","","",JsonSerializer.Serialize(saved),JsonSerializer.Serialize(cue)));
                        store.SaveCue(scope,cueTrack,cue);
                    }
                    return true;
                case "readCue": return store.ReadCue(scope,await Fresh(r.Id));
                case "editFile":
                    var edited=await Fresh(r.Id);
                    if(edited.Parts.Length!=1 || r.Value is not ("TITLE" or "ARTIST" or "ALBUM" or "ALBUMARTIST" or "GENRE"))
                        throw new IOException("Choose one mapped track and a supported tag.");
                    string filePath=Local(edited.FilePath);
                    if(!TagEditor.Supported(filePath)) throw new IOException("Choose an MP3, FLAC, or M4A file.");
                    var fileInfo=new FileInfo(filePath);
                    if(fileInfo.Length!=edited.Parts[0].Size)throw new IOException("The mapped file has changed.");
                    var staged=Path.Combine(fileInfo.DirectoryName!,".mixamp-"+Guid.NewGuid().ToString("N")+fileInfo.Extension);
                    var changes=new Dictionary<string,string>{{r.Value,r.Extra}};
                    var identity=Replacement.Digest(filePath);
                    try
                    {
                        await using(var original=File.Create(staged)) await api!.CopyOriginal(edited.Parts[0].Key,original,default);
                        if(Replacement.Digest(staged)!=identity)throw new IOException("Mapped file differs from the server original.");
                        TagEditor.EditCopy(staged,changes);
                        if(Local(edited.FilePath)!=filePath)throw new IOException("The share mapping changed.");
                        new Replacement(store,scope,edited.Id).Commit(filePath,identity,staged,JsonSerializer.Serialize(changes),
                            recordBackup:(backup,after)=>store.Remember(scope,Guid.NewGuid().ToString(),"Edit file tags",
                                new(edited,"Tags",edited.FilePath,backup,identity,after,filePath)));
                    }
                    finally {if(File.Exists(staged))File.Delete(staged);}
                    await api!.Call("/library/metadata/"+Plex.E(edited.Id)+"/refresh",HttpMethod.Put);
                    await Sync();return true;
                case "title":
                    var song=await Fresh(r.Id); var title=Required(r.Value);
                    store.Remember(scope,Guid.NewGuid().ToString(),"Rename track",new(song,"Title","","",song.Title,title));
                    await api!.SetFields(scope.Library,song,new(){["title"]=title}); await Sync(); return true;
                case "prepare":
                    var track=await Fresh(r.Id);
                    if (track.Parts.Length != 1) throw new IOException("Select a single-version track.");
                    var part = track.Parts[0];
                    string extension = Path.GetExtension(part.File);
                    if (extension.Length > 8 || extension.Any(c => c!='.' && !char.IsAsciiLetterOrDigit(c))) extension=".audio";
                    try
                    {
                        var probe = await api!.Range(part.Key,0,1,null,default);
                        remote = new(Guid.NewGuid().ToString("N")+extension,api,part.Key,probe.Size);
                        return new { url="/audio/"+remote.Name, cue=store.ReadCue(scope,track), track };
                    }
                    catch (Exception ex) when (ex is IOException or HttpRequestException) { remote=null; }
                    // A server without byte ranges needs a seekable local original for WebKit.
                    var key=scope.Key+track.Identity+track.Id;
                    if (mediaKey != key)
                    {
                        var ext=Path.GetExtension(track.FilePath);
                        if (ext.Length > 8 || ext.Any(c => c!='.' && !char.IsAsciiLetterOrDigit(c))) ext=".audio";
                        var path=Path.Combine(tmp,Guid.NewGuid().ToString("N")+ext);
                        try
                        {
                            await using (var f=File.Create(path)) await api!.CopyOriginal(track.Parts[0].Key,f,default);
                            var old=mediaPath; mediaPath=path; mediaKey=key;
                            if (old != null) File.Delete(old);
                        }
                        catch { if(mediaPath != path) File.Delete(path); throw; }
                    }
                    return new { url="/audio/"+Path.GetFileName(mediaPath), cue=store.ReadCue(scope,track), track };
                case "eq": return ToneProfile.Choose(r.Value,r.Extra);
                case "config": return new { eq=store.Get("installation","eq") ?? "Auto", columns=store.Get("installation","columns"),bars=store.Get("installation","bars") != "off",
                    themes=ThemeCatalog.All, theme=ThemeCatalog.Resolve(store.Get("installation","theme")).Name };
                case "setting":
                    if (r.Id is not ("eq" or "columns" or "bars" or "theme")) throw new ArgumentException();
                    if (r.Id=="theme" && ThemeCatalog.All.All(t=>t.Name!=r.Value)) throw new ArgumentException("Unknown theme.");
                    store.Set("installation",r.Id,r.Value); return true;
                case "review": return await Review(r);
                case "history": return new { undo=store.HistoryName(scope,false), redo=store.HistoryName(scope,true) };
                case "apply":
                    if (pending == null || r.Id != planId) throw new IOException("Review this action again.");
                    var action=pending; pending=null; await action(); return true;
                case "undo":
                case "redo": return ReviewHistory(r.Action == "redo");
                default: throw new IOException("Unknown action.");
            }
        }
        finally { gate.Release(); }
    }
    static string Required(string s) => !string.IsNullOrWhiteSpace(s) ? s.Trim() : throw new IOException("Enter a value.");
    async Task<Track> Fresh(string id)
    {
        var t=await (api ?? throw new IOException("Connect to Plex first.")).Fresh(id);
        if (t.LibraryId != scope.Library) throw new IOException("Select a track in the connected library.");
        return t;
    }
    object Plan(string title, List<string> lines,Func<Task> action)
    {
        planId=Guid.NewGuid().ToString("N"); pending=action;
        return new {id=planId,title,lines};
    }
    Mapping Mount => JsonSerializer.Deserialize<Mapping>(store.Get(scope.Key,"mount") ?? "null")
        ?? throw new IOException("Set the server root and mounted music folder in Library → Share mapping.");
    static string Component(string s)
    {
        s=Required(s).Replace('/','_').Replace(':','_');
        if(s is "." or ".." || s.Any(char.IsControl)) throw new IOException("Invalid folder name.");
        return s;
    }
    string Local(string server)
    {
        var m=Mount; string prefix=m.ServerRoot.TrimEnd('/')+"/";
        if (!server.StartsWith(prefix,StringComparison.Ordinal)) throw new IOException("File is outside the configured share.");
        var parts=server[prefix.Length..].Split('/');
        if(parts.Any(s=>s is "" or "." or ".." || s.Any(char.IsControl))) throw new IOException("Invalid file path.");
        var root=Path.GetFullPath(m.Share);
        if(!Directory.Exists(root)) throw new IOException("Mount the music share first.");
        string path=root;
        for (var p=new DirectoryInfo(root);p!=null;p=p.Parent)
            if(p.LinkTarget!=null) throw new IOException("Choose a direct mounted path.");
        foreach(var piece in parts)
        {
            path=Path.Combine(path,piece);
            if(new FileInfo(path).LinkTarget != null) throw new IOException("Symbolic links cannot be moved.");
        }
        return path;
    }
    async Task<object> Review(Request r)
    {
        var ids=(r.Ids ?? []).Distinct().ToArray();
        if(ids.Length==0) throw new IOException("Select tracks first.");
        if(r.Id=="delete")
        {
            var deletion=new Deletion(api!,scope,owner);
            List<DeletionReview> reviews=[];
            foreach(var id in ids) reviews.Add(await deletion.Review(id));
            return Plan("Permanently delete selected tracks?",reviews.SelectMany(d=>d.Track.Parts.Select(p=>p.File)).ToList(),async()=>{
                try { foreach(var d in reviews) if(!await deletion.Commit(d)) throw new IOException("Plex still reports the track."); }
                finally { await Sync(); }
            });
        }
        if(r.Id is not ("rename" or "organize")) throw new ArgumentException();
        var moves=new List<(Track Track,string To,string Source,string Target,DateTime Modified)>();
        var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var id in ids)
        {
            var t=await Fresh(id);
            if(t.Parts.Length!=1) throw new IOException("Multiple media parts for "+t.Title);
            var name=Path.GetFileName(t.FilePath);
            string dest;
            if(r.Id=="rename")
            {
                var file=Required(r.Value);
                if(ids.Length!=1 || Component(file)!=file || Path.GetExtension(file)!=Path.GetExtension(name)) throw new IOException("Keep the original filename extension.");
                dest=t.FilePath[..(t.FilePath.LastIndexOf('/')+1)]+file;
            }
            else
            {
                var artist=r.Value switch {"album"=>string.IsNullOrWhiteSpace(t.Artist)?t.ArtistDisplay:t.Artist,"track"=>t.ArtistDisplay,_=>r.Extra};
                dest=$"{Mount.ServerRoot.TrimEnd('/')}/{Component(artist)}/{Component(string.IsNullOrWhiteSpace(t.Album)?"Unknown Album":t.Album)}/{name}";
            }
            var src=Local(t.FilePath); var dst=Local(dest);
            if(src==dst) continue;
            if(Path.Exists(dst)||!paths.Add(dst)) throw new IOException("Destination already occupied or duplicated: "+dst);
            var info=new FileInfo(src);
            if(info.Length!=t.Parts[0].Size) throw new IOException("File size changed.");
            moves.Add((t,dest,src,dst,info.LastWriteTimeUtc));
        }
        if(moves.Count==0) throw new IOException("Files already have the requested paths.");
        return Plan(r.Id=="rename"?"Rename file":"Organize by artist",moves.Select(m=>m.Source+" → "+m.Target).ToList(),async()=>{
            string batch=Guid.NewGuid().ToString();
            try
            {
                foreach(var m in moves)
                {
                    var fresh=await Fresh(m.Track.Id);
                    if(fresh.Identity!=m.Track.Identity||fresh.Artist!=m.Track.Artist||fresh.ArtistDisplay!=m.Track.ArtistDisplay||
                        fresh.Album!=m.Track.Album||Local(m.Track.FilePath)!=m.Source||Local(m.To)!=m.Target||
                        File.GetLastWriteTimeUtc(m.Source)!=m.Modified||Path.Exists(m.Target)) throw new IOException("The move changed since review.");
                    Directory.CreateDirectory(Path.GetDirectoryName(m.Target)!);
                    string hash=Replacement.Digest(m.Source);
                    var entry=store.Remember(scope,batch,r.Id=="rename"?"Rename file":"Organize by artist",new(m.Track,"Move",m.Track.FilePath,m.To,hash,hash,m.Source,m.Target));
                    try { File.Move(m.Source,m.Target,false); }
                    catch { if(File.Exists(m.Source))store.Forget(scope,entry); throw; }
                }
            }
            finally { await Rescan(moves.Select(m=>m.Track).ToArray()); }
        });
    }
    async Task Rescan(Track[] tracks)
    {
        store.InvalidateMovedPaths(scope,tracks);
        await api!.Call("/library/sections/"+Plex.E(scope.Library)+"/refresh");
        await Sync();
        store.InvalidateMovedPaths(scope,tracks);
    }
    object ReviewHistory(bool redo)
    {
        var steps=store.LastChanges(scope, redo);
        var action = redo ? "Redo" : "Undo";
        if(steps.Count==0) throw new IOException($"No changes to {action.ToLowerInvariant()}.");
        return Plan(action+" "+steps[0].Name,steps.Select(s=>s.Change.Type=="Move"?s.Change.To+" → "+s.Change.From:s.Change.Track.Title).ToList(),async()=>{
            foreach(var s in steps)
            {
                var c=s.Change;
                if(c.Type=="Move")
                {
                    string original=Local(c.From),moved=Local(c.To);
                    if(original!=c.SourcePath||moved!=c.TargetPath) throw new IOException("Mount mapping changed.");
                    if(File.Exists(moved))
                    {
                        if(Path.Exists(original)||Replacement.Digest(moved)!=c.After) throw new IOException("The moved file changed or its original path is occupied.");
                        Directory.CreateDirectory(Path.GetDirectoryName(original)!); File.Move(moved,original,false);
                    }
                    else if(Replacement.Digest(original)!=c.Before) throw new IOException("Cannot verify original file.");
                }
                else if(c.Type=="Cue")
                {
                    var previous=JsonSerializer.Deserialize<Cue>(c.Before)!;
                    var next=JsonSerializer.Deserialize<Cue>(c.After)!;
                    var actual=store.ReadCue(scope,c.Track);
                    if(actual!=previous && actual!=next)throw new IOException("Playback range changed after this edit.");
                    store.SaveCue(scope,c.Track,previous);
                }
                else if(c.Type=="Tags")
                {
                    var original=Local(c.From);
                    if(original!=c.SourcePath || Path.GetDirectoryName(c.To)!=Path.GetDirectoryName(original) || new FileInfo(c.To).LinkTarget!=null)
                        throw new IOException("Backup mapping changed.");
                    var hash=Replacement.Digest(original);
                    if(hash!=c.Before)
                    {
                        if(hash!=c.After||Replacement.Digest(c.To)!=c.Before)throw new IOException("File or backup changed after editing.");
                        var opposite=c.Reverse().To;
                        if(Path.GetDirectoryName(opposite)!=Path.GetDirectoryName(original)||Path.Exists(opposite)||new FileInfo(opposite).LinkTarget!=null)
                            throw new IOException("The history backup path is occupied.");
                        File.Replace(c.To,original,opposite);
                    }
                }
                else if(c.Type=="Title")
                {
                    var t=await Fresh(c.Track.Id);
                    if(t.Identity!=c.Track.Identity||t.Title!=c.Before&&t.Title!=c.After) throw new IOException("Track changed after this edit.");
                    if(t.Title!=c.Before)await api!.SetFields(scope.Library,t,new(){["title"]=c.Before});
                }
                else throw new IOException("Unknown undo operation.");
                store.CompleteChange(scope,s,redo);
            }
            if(steps.Any(s=>s.Change.Type is "Move" or "Tags")) await Rescan(steps.Select(s=>s.Change.Track).ToArray()); else await Sync();
        });
    }
    public IResult Audio(string name)
    {
        var stream=remote;
        var path=mediaPath;
        var type=Path.GetExtension(name).ToLowerInvariant() switch { ".mp3"=>"audio/mpeg",".flac"=>"audio/flac",".wav"=>"audio/wav",".m4a" or ".mp4"=>"audio/mp4",".aac"=>"audio/aac",".ogg"=>"audio/ogg",_=>"application/octet-stream" };
        if (stream != null && name == stream.Name)
            return Results.Stream(new RangeStream(stream.Server,stream.Key,stream.Size),type,enableRangeProcessing:true);
        if(path==null||Path.GetFileName(path)!=name) return Results.NotFound();
        return Results.File(path,type,enableRangeProcessing:true);
    }
    public void Dispose(){api?.Dispose();candidate?.Dispose();Directory.Delete(tmp,true);}
}
