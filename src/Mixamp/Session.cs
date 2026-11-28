using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using System.Windows.Threading;

namespace Mixamp;

sealed class Command(Func<string, Task> run, Action<string> error, Func<string, bool> available)
    : ICommand
{
    readonly HashSet<string> active = [];
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) =>
        available(parameter as string ?? "") && !active.Contains(parameter as string ?? "");

    public async void Execute(object? parameter)
    {
        var key = parameter as string ?? "";
        if (!CanExecute(parameter) || !active.Add(key))
            return;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await run(key);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            error(Session.Message(ex));
        }
        finally
        {
            active.Remove(key);
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

sealed partial class Session : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new(name));

    public static string Message(Exception ex) =>
        ex is HttpRequestException h
            ? $"Connection failed{(h.StatusCode.HasValue ? $" (HTTP {(int)h.StatusCode})" : "")}. Check access and reconnect."
        : ex is IOException or FormatException or ArgumentException or TimeoutException ? ex.Message
        : "Operation failed (" + ex.GetType().Name + ").";

    public Store Store { get; }
    readonly Account account;
    public Plex? Api { get; private set; }
    public Scope Scope { get; private set; } = new("", "", "");
    Library Library =>
        Api == null ? throw new IOException("Connect to a library first.") : new(Api, Scope);
    readonly IPlayback audio;
    readonly CastSession cast=new();
    CancellationTokenSource connection = new(),
        sync = new(),
        playback = new(),
        search = new();
    CancellationTokenSource scrolling = new();
    CancellationTokenSource? playlistRead;
    PlaylistBrowser? playlistBrowser;
    string playlistSort="";
    bool playlistDescending;
    System.Windows.Data.ListCollectionView? queueView;
    public System.Collections.IEnumerable DisplayRows => (System.Collections.IEnumerable?)queueView ?? Rows;
    bool Ranged => Section is "Songs" or "Playlists";
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    DateTime nextReport;
    int generation,
        readGeneration,
        index = -1;
    string status = "Connect to Plex or open the loopback demo.",
        searchText = "",
        section = "Songs",
        caption = "Your library",
        order = "Title",
        queueId = "",
        playlistId = "";
    bool descending,
        owned,
        writable;
    public string Caption
    {
        get => caption;
        set
        {
            caption = value;
            Changed();
        }
    }
    string libraryProgress = "", rangeProgress = "", refreshProgress = "";
    bool retryRange,retryRefresh;
    public bool CanRetryBrowse => Ranged && (retryRange || retryRefresh);
    void BrowseStatusChanged()
    {
        Changed(nameof(LibraryProgress));
        Changed(nameof(CanRetryBrowse));
    }
    void RangeStatus(string text,bool failed=false)
    {
        rangeProgress=text;retryRange=failed;BrowseStatusChanged();
    }
    void RefreshStatus(string text,bool failed=false)
    {
        refreshProgress=text;retryRefresh=failed;BrowseStatusChanged();
    }
    public Task RetryBrowse() => retryRange ? Viewport(viewportFirst,viewportLast) : Refresh();
    static string BrowseFailure(Exception ex) => ex switch {
        OperationCanceledException or TimeoutException => "Plex timed out.",
        IOException or HttpRequestException => Message(ex),
        _ => "Mixamp could not read the track list returned by Plex."
    };
    public string LibraryProgress
    {
        get => Ranged && rangeProgress!="" ? rangeProgress : Ranged && refreshProgress!="" ? refreshProgress : libraryProgress;
        private set { libraryProgress = value; Changed(); }
    }

    public string Status
    {
        get => status;
        set
        {
            status = value;
            Changed();
        }
    }
    public string Section
    {
        get => section;
        private set
        {
            if(section!=value){
                playlistRead?.Cancel();playlistBrowser=null;
                search.Cancel();search=new();searchText="";Changed(nameof(SearchText));
                scrolling.Cancel();
                RangeStatus("");RefreshStatus("");
            }
            section = value;
            BrowseStatusChanged();
            Changed();
            Changed(nameof(ViewTitle));
            Changed(nameof(IsStationQueue));
        }
    }
    public string SearchText
    {
        get => searchText;
        set
        {
            searchText = value;
            Changed();
            ScheduleSearch();
        }
    }
    public string ViewTitle => Section == "Playlists"
        ? Playlists.FirstOrDefault(p => p.Id == playlistId)?.Name ?? "Playlist"
        : IsStationQueue ? stationName + " · Up Next" : Section;
    string stationName = "";
    bool sonicRadio;
    public bool IsStationQueue => Section == "Up Next" && stationName.Length > 0;
    void SetStationName(string name,bool sonic=false)
    {
        stationName = name;
        sonicRadio = sonic;
        Changed(nameof(ViewTitle));
        Changed(nameof(IsStationQueue));
    }
    public void PreviewEq(string mode) => audio.EqMode = mode;
    public string EqMode
    {
        get => audio.EqMode;
        set { audio.EqMode = value; Store.Set("installation", "eq", value); Status = $"EQ · {audio.EqProfile}"; }
    }
    public bool Playing => cast.DeviceName==null ? audio.Playing : cast.Playing;
    public string TransportAction => Playing ? "Pause" : "Play";
    public bool Descending => Section=="Playlists" ? playlistDescending : descending;
    public string SortColumn => Section=="Playlists" ? playlistSort : order;

    public void RestoreSort(IEnumerable<string> columns)
    {
        if (Store.Get("installation", "songSort")?.Split('|') is [var column, var direction]
            && direction is ("asc" or "desc") && columns.Contains(column))
        {
            order = column;
            descending = direction == "desc";
        }
    }
    public sealed record PlaylistEntry(string Id, string Name);
    public IReadOnlyList<PlaylistEntry> Playlists { get; private set; } = [];
    public Func<Track,bool> IsRowSelected { get; set; } = _ => false;
    public string Now => cast.CurrentSong?.Title ?? Current?.Title ?? "Ready to play";
    public System.Windows.Media.Imaging.BitmapImage? Artwork { get; private set; }
    public string Byline => cast.CurrentSong is {} remote ? remote.Artist+"   ·   "+remote.Album :
        Current == null
            ? ""
            : Current.ArtistDisplay + "   ·   " + Current.Format.ToUpperInvariant();
    public Track? Current { get; private set; }
    public string Clock => Cue.Format(Position);
    public long Position => cast.DeviceName==null ? audio.Position : (long)(cast.Position*1000);
    public bool Seekable => cast.DeviceName==null && Current?.Duration > 0;
    Cue activeCue = new();
    bool previewing;
    public long Minimum => Current == null ? 0 : activeCue.Start ?? 0;
    public long Maximum => Current == null ? 0 : activeCue.End ?? Current.Duration;
    public string ClipClock =>
        Current == null
            ? ""
            : $"Original {Clock} / {Current.Length}   ·   Playback {Cue.Format(Math.Clamp(Position - Minimum, 0, Maximum - Minimum))} / {Cue.Format(Maximum - Minimum)}";
    public double Volume
    {
        get => audio.Volume;
        set => audio.Volume = value;
    }
    public bool Repeat { get; set; }
    Track? selected;
    public Track? Selected
    {
        get => selected;
        set
        {
            selected = value;
            Changed();
        }
    }
    public Func<int> SelectedIndex { get; set; } = () => -1;
    public Func<Track[]> Selection { get; set; } = () => [];
    public IReadOnlyList<Track> Rows { get; private set; } = [];
    public ObservableCollection<Track> Queue { get; } = [];
    public ICommand Action { get; }
    public bool CanWrite => writable && Api != null;
    public string LibraryName { get; private set; } = "";
    string serverName = "";
    Uri? external;
    int reconnects;

    public Session(Store store, IPlayback audio)
    {
        this.audio = audio;
        audio.EqMode = store.Get("installation", "eq") ?? "Auto";
        Store = store;
        account = new(store);
        Action = new Command(
            Run,
            s => Status = s,
            cmd =>
                cmd switch
                {
                    "Google Cast…" => Api!=null && (Queue.Count>0 || Selected!=null),
                    "Resume on speaker" or "Pause on speaker" or "Previous on speaker" or "Next on speaker" or "Stop casting" => cast.DeviceName!=null,
                    "Delete tracks from server" => !batchBusy && owned && Api != null && Selected != null,
                    "Undo" or "Redo" => !batchBusy && Api != null,
                    "Rename file" => !batchBusy && Api != null && Selected != null && Selection().Length == 1,
                    "Organize files by artist" => !batchBusy && Api != null && Selected != null,
                    "Remove from queue" => Section == "Up Next" && Selected != null,
                    "Remove from playlist" or "Move up" => Section == "Playlists"
                        && Selected?.PlaylistItem.Length > 0,
                    "New playlist" => Api != null,
                    "Sonically similar radio" => Api != null && Selected != null && Selection().Length == 1,
                    "Edit…" => Api != null && Selected != null,
                    "Add to playlist" => Api != null && Selected != null,
                    "Song Info"
                    or "Play"
                    or "Play full track once"
                    or "Add to Queue"
                    or "Play Next" => Selected != null,
                    "Sync" or "Share mapping" or "Radio" or "Playlists" => Api != null,
                    _ => true,
                }
        );
        audio.Ended += () => Action.Execute("Ended");
        audio.Skip += d => Action.Execute(d < 0 ? "Previous" : "Next");
        audio.StateChanged += playing =>
        {
            Changed(nameof(Playing));
            Changed(nameof(TransportAction));
            if (Current == null)
                return;
            if (playing)
                timer.Start();
            else
                timer.Stop();
            nextReport = DateTime.UtcNow.AddSeconds(10);
            _ = Report(playing ? "playing" : "paused");
            if (playing)
                Status = audio.Route;
        };
        audio.Failed += async msg =>
        {
            Changed(nameof(Playing));
            Changed(nameof(TransportAction));
            timer.Stop();
            _ = Report("stopped");
            Status = msg;
            var lost = external;
            var track = Current;
            var token = playback.Token;
            if (lost?.IsFile == false && track != null && reconnects++ < 2)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(reconnects), token);
                    if (external == lost && !token.IsCancellationRequested)
                        await Start(track, true, lost);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Status = Message(ex);
                }
            }
        };
        timer.Tick += (s, e) =>
        {
            if(cast.DeviceName!=null){UpdatePlayer();return;}
            if(!audio.Playing){timer.Stop();UpdatePlayer();return;}
            Changed(nameof(Clock));
            Changed(nameof(ClipClock));
            Changed(nameof(Position));
            if (DateTime.UtcNow >= nextReport)
            {
                nextReport = DateTime.UtcNow.AddSeconds(10);
                _ = Report(audio.Playing ? "playing" : "paused");
            }
        };
    }

    public void Seek(long ms)
    {
        if(cast.DeviceName!=null)return;
        audio.Seek(ms);
        Changed(nameof(Position));
        _ = Report(audio.Playing ? "playing" : "paused");
    }

    readonly SemaphoreSlim timeline = new(1);
    string currentQueue = "";

    async Task Report(string state)
    {
        if (Current == null || Api == null || external != null)
            return;
        var track = Current;
        var id = currentQueue;
        var api = Api;
        long position = audio.Position;
        await timeline.WaitAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await api.Call(
                $"/:/timeline?ratingKey={Plex.E(track.Id)}&key=/library/metadata/{Plex.E(track.Id)}&state={state}&time={position}&duration={track.Duration}"
                    + (
                        id != "" && track.QueueItem != ""
                            ? $"&containerKey=/playQueues/{Plex.E(id)}&playQueueItemID={Plex.E(track.QueueItem)}"
                            : ""
                    ),
                ct: timeout.Token
            );
        }
        catch (Exception)
        { /* Timeline availability must not interrupt audio. */
        }
        finally
        {
            timeline.Release();
        }
    }

    SongRows? startupRows;
    string startupOrder="";
    bool startupDescending;
    int viewportFirst,viewportLast;
    Task? restoring;
    public Func<bool> PlaylistsExpanded { get; set; } = () => false;

    public async Task Resume()
    {
        if (Store.Get("installation", "lastScope") is not { } value) return;
        var saved=JsonSerializer.Deserialize<Scope>(value)!;
        if (Store.ReadLaunchSongs(saved,order,descending) is { } last)
        {
            var rows = new SongRows();
            rows.Apply(0,last,0,_ => false);
            startupRows=rows;
            startupOrder=order;
            startupDescending=descending;
            Show(rows);
            Status = "Reconnecting to Plex…";
        }
        try { restoring=Connect(saved.Account=="demo" && saved.Server=="mixamp-demo", saved); await restoring; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status=Message(ex);if(Api==null) LibraryProgress="Reconnect to load tracks."; }
        finally {restoring=null;}
    }

    record QueueState(Track[] Tracks, int Index, string QueueId, bool Repeat, string? StationName = null,bool Sonic=false);

    void SaveQueue()
    {
        if (Api != null)
            Store.Set(
                Scope.Key,
                "queue",
                JsonSerializer.Serialize(new QueueState(Queue.ToArray(), index, queueId, Repeat, stationName, sonicRadio))
            );
    }

    void RestoreQueue()
    {
        if (Store.Get(Scope.Key, "queue") is not { } json)
            return;
        var state = JsonSerializer.Deserialize<QueueState>(json)!;
        foreach (var t in state.Tracks)
            Queue.Add(t);
        index = state.Index;
        queueId = state.QueueId;
        SetStationName(state.StationName ?? (queueId.Length > 0 ? "Radio" : ""),state.Sonic);
        Repeat = state.Repeat;
    }

    async Task Connect(bool demo, Scope? resume = null)
    {
        connection.Cancel();
        if(resume==null && startupRows!=null){startupRows=null;Show([]);}
        connection = new();
        var ct = connection.Token;
        LibraryProgress = resume == null ? "Connecting to library…" : "Reconnecting to library…";
        Plex? candidate = null;
        try
        {
            string user,
                machine,
                name;
            bool owner;
            if (demo)
            {
                user = "demo";
                machine = "mixamp-demo";
                name = "Demo server";
                owner = true;
                candidate = new(
                    new Uri("http://127.0.0.1:32491"),
                    account.Identifier,
                    "fixture-only"
                );
                if ((await candidate.Call("/identity", ct: ct)).Str("machineIdentifier") != machine)
                    throw new IOException("Loopback server is not the Mixamp fixture.");
            }
            else
            {
                Status = "Waiting for Plex sign-in and server discovery…";
                var discovery = await account.Discover(ct, interactive: resume == null);
                user = discovery.AccountId;
                var s = resume != null
                    ? discovery.Servers.FirstOrDefault(s => user == resume.Account && s.Str("clientIdentifier") == resume.Server)
                        ?? throw new IOException("The saved Plex server is unavailable.")
                    : discovery.Servers[
                    Dialogs.Choose(
                        "Plex servers",
                        discovery.Servers.Select(s => s.Str("name")).ToArray()
                    )
                ];
                machine = s.Str("clientIdentifier");
                name = s.Str("name");
                owner = s["owned"]?.GetValue<bool>() == true;
                candidate = await account.Connect(s, ct);
            }
            var sections = (await candidate.Call("/library/sections", ct: ct))
                .Items("Directory")
                .Where(d => d.Str("type") == "artist")
                .ToArray();
            var lib = resume != null
                ? sections.FirstOrDefault(l => l.Str("key") == resume.Library)
                    ?? throw new IOException("The saved music library is unavailable.")
                : sections[
                sections.Length == 1
                    ? 0
                    : Dialogs.Choose(
                        "Music libraries",
                        sections.Select(s => s.Str("title")).ToArray()
                    )
            ];
            ct.ThrowIfCancellationRequested();
            await cast.Control("stop");
            SaveQueue();
            sync.Cancel();
            search.Cancel();
            generation++;
            Stop();
            Queue.Clear();
            index = -1;
            queueId = playlistId = "";
            SetStationName("");
            Api?.Dispose();
            Api = candidate;
            candidate = null;
            Scope = new(user, machine, lib.Str("key"));
            LibraryName = lib.Str("title");
            serverName = name;
            owned = owner;
            writable = owner || lib.Str("allowEdit").ToLowerInvariant() is "1" or "true";
            Playlists = [];
            Changed(nameof(Playlists));
            Caption = LibraryName + "  /  " + name;
            Store.Set("installation", "lastScope", JsonSerializer.Serialize(Scope));
            Store.Set(Scope.Key, "caption", Caption);
            RestoreQueue();
            Section = "Songs";
            await Sync();
            if(PlaylistsExpanded())await ReloadPlaylists();
        }
        finally
        {
            candidate?.Dispose();
            if (connection.Token == ct) LibraryProgress = "";
        }
    }

    async Task Sync()
    {
        sync.Cancel();
        sync = new();
        await Refresh(sync.Token);
    }

    async void ScheduleSearch()
    {
        search.Cancel();
        search = new();
        var ct = search.Token;
        try
        {
            await Task.Delay(180, ct);
            if(Section=="Up Next")FilterQueue();else await Refresh(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = Message(ex); }
    }

    Task<SongPage> ReadPage(int offset,CancellationToken ct)
    {
        if(Section!="Playlists")return Library.Songs(offset,order,descending,SearchText,ct);
        var browser=playlistBrowser ?? throw new IOException("Reopen the playlist to continue.");
        var query=SearchText;var sort=playlistSort;var reverse=playlistDescending;
        return Task.Run(()=>browser.Read(offset,query,sort,reverse,ct),ct);
    }

    public async Task Refresh(CancellationToken ct = default)
    {
        if (!Ranged || Api==null || Section=="Playlists" && playlistBrowser==null) return;
        var sectionAtStart=Section;
        using var request=Section=="Playlists" ? CancellationTokenSource.CreateLinkedTokenSource(ct,playlistRead!.Token) : null;
        if(request!=null)ct=request.Token;
        int generation=++readGeneration;
        scrolling.Cancel();
        RangeStatus("");
        RefreshStatus(Section=="Playlists" ? (SearchText.Length==0?"Loading playlist…":"Searching playlist…") : startupRows!=null?"Refreshing library…":"Loading tracks…");
        var api=Api;
        var column=order;
        var reverse=descending;
        var query=SearchText;
        SongPage page;
        try { page=await ReadPage(0,ct); }
        catch(Exception ex)
        {
            if (generation==readGeneration && Api==api && Section==sectionAtStart){
                if(ct.IsCancellationRequested)RefreshStatus("");
                else RefreshStatus("Couldn’t load tracks. "+BrowseFailure(ex),true);
            }
            return;
        }
        if (generation!=readGeneration || Api!=api || Section!=sectionAtStart || ct.IsCancellationRequested) return;
        RefreshStatus("");
        if (Section=="Songs" && string.IsNullOrWhiteSpace(query)) Store.SaveLaunchSongs(Scope,column,reverse,page);
        if(Section=="Songs" && startupRows!=null && Rows==startupRows && column==startupOrder && reverse==startupDescending && query=="")
        {
            var rows=startupRows;
            startupRows=null;
            rows.Apply(0,page,viewportFirst,IsRowSelected);
            if(viewportLast>=SongPage.Size)await Viewport(viewportFirst,viewportLast);
        }
        else
        {
            startupRows=null;
            var rows=new SongRows();
            rows.Apply(0,page,0,IsRowSelected);
            Show(rows);
        }
        Status=page.Total is int count ? $"{count:N0} tracks" : "Connected to Plex";
    }

    (SongRows Rows,int First,int Last)? loadingRange;
    public async Task Viewport(int first,int last)
    {
        viewportFirst=first;viewportLast=last;
        if(Api==null || !Ranged || Rows is not SongRows rows || rows.Count==0)return;
        first=Math.Clamp(first,0,rows.Count-1);last=Math.Clamp(last,first,rows.Count-1);
        int start=first-first%SongPage.Size,end=last-last%SongPage.Size;
        if(loadingRange==(rows,start,end) && !scrolling.IsCancellationRequested)return;
        scrolling.Cancel();scrolling=new();
        var ct=scrolling.Token;
        var seq=readGeneration;
        loadingRange=(rows,start,end);
        try
        {
            for(int offset=start;offset<=end;offset+=SongPage.Size)
            {
                if(rows.HasPage(offset))continue;
                RangeStatus($"Loading tracks {offset+1:N0}–{Math.Min(offset+SongPage.Size,rows.Count):N0}…");
                var page=await ReadPage(offset,ct);
                if(ct.IsCancellationRequested || seq!=readGeneration || !Ranged || rows!=Rows)return;
                rows.Apply(offset,page,first,IsRowSelected);
            }
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { }
        catch(Exception ex)
        {
            if(!ct.IsCancellationRequested && seq==readGeneration && rows==Rows && Ranged)
                RangeStatus("Couldn’t load tracks. "+BrowseFailure(ex),true);
        }
        finally{
            if(scrolling.Token==ct){
                loadingRange=null;
                if(!retryRange || rows.HasPage(start) && rows.HasPage(end))RangeStatus("");
            }
        }
    }

    public Task<Cue> ReadCue(Scope scope,Track track,CancellationToken ct=default)
    {
        if(Api==null)
        {
            var cue=Store.FindCue(scope,track,out bool verify);
            return Task.FromResult(verify ? new Cue() : cue);
        }
        return new Library(Api,scope).PlaybackCue(Store,track,ct);
    }

    void Show(IReadOnlyList<Track> list)
    {
        queueView?.DetachFromSourceCollection();
        Rows = list;
        queueView=ReferenceEquals(list,Queue)?new System.Windows.Data.ListCollectionView(Queue):null;
        FilterQueue();
        Changed(nameof(Rows));Changed(nameof(DisplayRows));
    }

    void FilterQueue()
    {
        if(queueView==null)return;
        var words=MusicSearch.Words(SearchText);
        queueView.Filter=words.Length==0 ? null : item=>MusicSearch.Matches((Track)item,words);
    }

    public async Task Sort(string col)
    {
        if(Section=="Playlists"){
            playlistDescending=col==playlistSort && !playlistDescending;playlistSort=col;
            await Refresh();return;
        }
        descending = col == order && !descending;
        order = col;
        var direction = descending;
        await Refresh();
        if (order == col && descending == direction)
            Store.Set("installation", "songSort", $"{col}|{(direction ? "desc" : "asc")}");
    }

    void Stop()
    {
        playback.Cancel();
        timer.Stop();
        audio.Stop();
        Current = null;
        external = null;
        UpdatePlayer();
    }

    void UpdatePlayer()
    {
        foreach (
            var n in new[]
            {
                nameof(Playing),
                nameof(TransportAction),
                nameof(Now),
                nameof(Byline),
                nameof(Position),
                nameof(Maximum),
                nameof(Minimum),
                nameof(ClipClock),
                nameof(Seekable),
                nameof(Clock),
            }
        )
            Changed(n);
    }

    async Task Start(Track track, bool full = false, Uri? uri = null, Cue? preview = null)
    {
        if(cast.DeviceName!=null)await cast.Control("stop");
        if (uri == null && Api == null)
            throw new IOException("Connect to the selected Plex library for playback.");
        _ = Report("stopped");
        playback.Cancel();
        playback = new();
        var ct = playback.Token;
        audio.Stop();
        Current = null;
        external = null;
        timer.Stop();
        UpdatePlayer();
        if (uri == null)
        {
            var fresh = await Api!.Fresh(track.Id, ct);
            if (fresh.Identity != track.Identity)
                throw new IOException(
                    "The media part has changed. Sync and select the recording again."
                );
            track = fresh with { QueueItem = track.QueueItem, PlaylistItem = track.PlaylistItem };
        }
        var cue =
            full || uri?.IsFile == false
                ? new Cue()
                : preview
                    ?? await ReadCue(
                        track.LibraryId is { Length: > 0 }
                            ? Scope with
                            {
                                Library = track.LibraryId,
                            }
                            : Scope,
                        track, ct
                    );
        if (track.Duration > 0)
            cue.Validate(track.Duration);
        await audio.Start(track, cue, Api, uri, ct);
        ct.ThrowIfCancellationRequested();
        external = uri;
        currentQueue = queueId;
        activeCue = cue;
        previewing = preview != null;
        Current = track;
        UpdatePlayer();
        timer.Start();
        nextReport = DateTime.UtcNow.AddSeconds(10);
        Status = audio.Route;
        _ = LoadArt(track, ct);
    }

    async Task LoadArt(Track track, CancellationToken ct)
    {
        Artwork = null;
        Changed(nameof(Artwork));
        if (Api == null || track.Thumb == "")
            return;
        try
        {
            var bytes = await Api.Thumbnail(track.Thumb, ct);
            var image = await Task.Run(
                () =>
                {
                    using var stream = new MemoryStream(bytes);
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.DecodePixelWidth = 96;
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.StreamSource = stream;
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                },
                ct
            );
            ct.ThrowIfCancellationRequested();
            Artwork = image;
            Changed(nameof(Artwork));
        }
        catch (Exception)
        { /* A missing cover does not change playback. */
        }
    }

    public Task Preview(Track t, Cue cue) => Start(t, preview: cue);

    async Task<Track[]> SelectedTracks()
    {
        var tracks=Selection();
        if(Api==null && restoring!=null)await restoring;
        var rows=Rows;
        var generation=readGeneration;
        foreach(var group in tracks.Select((track,i)=>(track,i)).Where(x=>x.track is SongRows.Placeholder)
            .GroupBy(x=>((SongRows.Placeholder)x.track).Position / SongPage.Size))
        {
            var offset=group.Key*SongPage.Size;
            var page=await ReadPage(offset,search.Token);
            if(rows!=Rows || generation!=readGeneration)throw new IOException("Select the tracks again in the current view.");
            foreach(var (track,i) in group)
            {
                int local=((SongRows.Placeholder)track).Position-offset;
                if(local>=page.Tracks.Count)throw new IOException("The library changed. Select the tracks again.");
                tracks[i]=page.Tracks[local];
            }
        }
        if(Section=="Playlists" && tracks.Any(t=>t.Id==""))throw new IOException("This entry is unavailable.");
        return tracks;
    }

    async Task PlaySelection(bool full)
    {
        var selected = await SelectedTracks();
        if (selected.Length == 0)
            return;
        if (Section == "Up Next")
        {
            index = SelectedQueueIndex();
            if (index < 0)
                return;
        }
        else
        {
            Queue.Clear();
            foreach (var t in selected)
                Queue.Add(t);
            index = 0;
            queueId = "";
            SetStationName("");
        }
        await Start(Queue[index], full);
    }

    int SelectedQueueIndex()
    {
        int row = SelectedIndex();
        if(row>=0 && queueView?.Filter is {} accepts){
            for(int i=0;i<Queue.Count;i++)if(accepts(Queue[i]) && row--==0)return i;
            return -1;
        }
        if (row >= 0 && row < Queue.Count && ReferenceEquals(Queue[row], Selected)) return row;
        return Selected == null ? -1 : Queue.IndexOf(Selected);
    }

    async Task Advance(int step, bool ended = false)
    {
        if (ended && previewing)
        {
            await Report("stopped");
            Stop();
            return;
        }
        if (external != null)
        {
            if (ended && !external.IsFile && Current != null && reconnects++ < 2)
            {
                var url = external;
                var track = Current;
                var ct = playback.Token;
                await Task.Delay(TimeSpan.FromSeconds(reconnects), ct);
                await Start(track, true, url);
                return;
            }
            if (ended)
                Stop();
            return;
        }
        if (ended && Repeat && Current != null)
        {
            await Start(Current);
            return;
        }
        int next = index + step;
        if (next >= Queue.Count && !sonicRadio && queueId != "" && Queue.Count > 0)
        {
            var last = Queue[^1].QueueItem;
            var pendingQueue=queueId;
            var window=await Library.ContinueQueue(queueId,last);
            if(queueId!=pendingQueue || next!=index+step)return;
            var batch = window
                .Items("Metadata")
                .Select(Track.Read)
                .ToList();
            int anchor = batch.FindIndex(t => t.QueueItem == last);
            if (anchor >= 0)
                foreach (var t in batch.Skip(anchor + 1))
                    if (t.QueueItem != "" && !Queue.Any(q => q.QueueItem == t.QueueItem))
                        Queue.Add(t);
        }
        if (next >= Queue.Count)
        {
            await Report("stopped");
            Stop();
            return;
        }
        if (next < 0)
            return;
        index = next;
        await Start(Queue[index]);
    }

    public async Task ReloadPlaylists()
    {
        if (Api == null) return;
        var api = Api;
        var entries = (await Library.Playlists()).Select(p => new PlaylistEntry(p.Str("ratingKey"), p.Str("title"))).ToList();
        if (api != Api) return;
        Playlists = entries;
        Changed(nameof(Playlists));
        Changed(nameof(ViewTitle));
    }

    public async Task OpenPlaylist(string id)
    {
        playlistId = id;playlistSort="";playlistDescending=false;
        Section = "Playlists";
        searchText="";Changed(nameof(SearchText));
        Show([]);
        await PlaylistRows();
    }

    public async Task DeletePlaylist(PlaylistEntry entry)
    {
        if (MessageBox.Show($"Remove the playlist “{entry.Name}”? Its tracks stay in your library.",
                "Remove playlist", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await Library.DeletePlaylist(entry.Id);
        if (playlistId == entry.Id && Section == "Playlists")
            await Run("Songs");
        await ReloadPlaylists();
    }

    async Task PlaylistRows()
    {
        playlistRead?.Cancel();playlistRead?.Dispose();playlistRead=new();
        playlistBrowser=new(Library,playlistId);
        scrolling.Cancel();Show([]);
        await Refresh();

    }

    public IReadOnlyList<RadioOption> Stations { get; private set; } = [];
    public string RadioMessage { get; private set; } = "";
    int stationRequest;

    async Task BrowseRadio(bool byArtist)
    {
        var artist = byArtist ? Selected ?? throw new IOException("Select a track by this artist.") : null;
        var request = ++stationRequest;
        var api = Api;
        RadioMessage = "Loading stations…";
        Stations = [];
        Section = "Radio";
        Show([]);
        try
        {
            var stations = await Library.RadioStations(artist);
            if (request != stationRequest || Section != "Radio" || api != Api) return;
            Stations = stations;
            RadioMessage = stations.Count == 0 ? "Plex returned no stations for this artist." : "";
        }
        catch (Exception ex)
        {
            if (request != stationRequest || Section != "Radio" || api != Api) return;
            RadioMessage = Message(ex);
        }
        Changed(nameof(Stations));
    }

    public Task<List<RadioChoice>> RadioChoices(RadioOption option) => Library.RadioChoices(option, null);

    int radioPlayRequest;
    public void CancelPendingStation() => ++radioPlayRequest;
    public async Task PlayRadio(RadioOption option, IReadOnlyList<string> choices)
    {
        var api = Api;
        int request = ++radioPlayRequest;
        int browse = stationRequest;
        string originSection = Section;
        var q = await Library.StartRadio(option, choices, null);
        if (api != Api || Section != originSection || browse != stationRequest || request != radioPlayRequest) return;
        var entries = q.Items("Metadata").Select(Track.Read).ToList();
        if (entries.Count == 0)
            throw new IOException("Plex returned an empty station.");
        Queue.Clear();
        foreach (var t in entries)
            Queue.Add(t);
        index = 0;
        queueId = q.Str("playQueueID");
        SetStationName(option.Name,option.Kind=="sonic");
        Section = "Up Next";
        Show(Queue);
        await Start(Queue[0]);
    }

    public async Task Run(string cmd)
    {
        switch (cmd)
        {
            case "Google Cast…":
                var castApi=Api ?? throw new IOException("Connect to Plex first.");
                var castScope=Scope;
                var tracksToCast=Queue.Count>0 ? Queue.Skip(Math.Max(index,0)).ToArray() : await SelectedTracks();
                Status="Looking for Google Cast devices…";
                var speakers=await cast.Discover();
                if(speakers.Count==0)throw new IOException("No Google Cast devices found. Mixamp can’t cast to Echo speakers over Wi-Fi.");
                var chosen=speakers[Dialogs.Choose("Play on Google Cast",speakers.Select(s=>s.Name).ToArray())];
                if(Api!=castApi || Scope!=castScope)throw new IOException("The library changed. Choose the speaker again.");
                Status="Connecting to speaker…";
                var handoffPosition=Current?.Id==tracksToCast.FirstOrDefault()?.Id ? Position/1000.0 : 0;
                await Task.Run(()=>cast.Start(chosen.Id,castApi,castScope,tracksToCast.Select(CastSong.From).ToArray(),handoffPosition));
                if(audio.Playing)audio.Toggle();
                UpdatePlayer();timer.Start();
                Status="Playing on "+chosen.Name;
                break;
            case "Resume on speaker": await cast.Control("play");break;
            case "Pause on speaker": await cast.Control("pause");break;
            case "Previous on speaker": await cast.Control("previous");break;
            case "Next on speaker": await cast.Control("next");break;
            case "Stop casting": await cast.Control("stop");timer.Stop();UpdatePlayer();Status="Casting stopped";break;
            case "Connect":
            case "Demo":
                await Connect(cmd == "Demo");
                break;
            case "Cancel":
                connection.Cancel();
                sync.Cancel();
                break;
            case "Sync":
                await Sync();
                break;
            case "Songs":
                Section = "Songs";
                await Refresh();
                break;
            case "New playlist":
            case "Add to playlist":
                var library = Library;
                var api = Api;
                var tracks = await SelectedTracks();
                if (tracks.Length == 0 && cmd != "New playlist")
                    return;
                string? dest = null,
                    name = "";
                if (cmd == "New playlist")
                {
                    name = Dialogs.Input("New playlist name");
                    if (name.Length == 0) throw new IOException("Enter a playlist name.");
                    if (tracks.Length == 0)
                        dest = (await library.CreatePlaylist(name)).Items("Metadata").Single().Str("ratingKey");
                }
                else
                {
                    var lists = await library.Playlists();
                    var playlistChoice = Dialogs.Choose("Add to playlist",lists.Select(p=>p.Str("title")).Append("New playlist…").ToArray());
                    if (playlistChoice < lists.Count) dest = lists[playlistChoice].Str("ratingKey");
                    else
                    {
                        name = Dialogs.Input("New playlist name");
                        if (string.IsNullOrWhiteSpace(name)) throw new IOException("Enter a playlist name.");
                    }
                }
                foreach (var t in tracks)
                {
                    if (Api != api) return;
                    var r = await library.AddToPlaylist(dest, name, t);
                    dest ??= r.Items("Metadata").Single().Str("ratingKey");
                }
                if (Api != api) return;
                await ReloadPlaylists();
                if (Api == api) await OpenPlaylist(dest!);
                break;
            case "Remove from playlist":
            case "Move up":
                if (Section != "Playlists" || Selected == null || Selected.PlaylistItem == "")
                    throw new IOException("Select a playlist entry.");
                if (cmd == "Move up")
                {
                    if(playlistSort!="" || !string.IsNullOrWhiteSpace(SearchText))
                        throw new IOException("Reopen the playlist in its original order to move an entry.");
                    var selected=Selected;var view=playlistBrowser;var id=playlistId;
                    int i = SelectedIndex();
                    if (i < 1)
                        return;
                    string after="0";
                    if(i>1){
                        int start=(i-2)/SongPage.Size*SongPage.Size;
                        var page=await ReadPage(start,playlistRead!.Token);
                        if(view!=playlistBrowser || page.Tracks.Count<=i-2-start)return;
                        after=page.Tracks[i-2-start].PlaylistItem;
                        if(after=="")throw new IOException("The previous playlist entry is unavailable.");
                    }
                    await Library.Move(id,selected.PlaylistItem,after);
                }
                else
                    await Library.Remove(playlistId, Selected.PlaylistItem);
                await PlaylistRows();
                break;
            case "Radio":
                await BrowseRadio(false);
                break;
            case "Artist radio":
                await BrowseRadio(true);
                break;
            case "Internet radio":
                var urls = JsonSerializer.Deserialize<List<string>>(
                    Store.Get(Scope.Key, "stations") ?? "[]"
                )!;
                int choice = Dialogs.Choose("Internet radio", urls.Append("Add stream…").ToArray());
                string url =
                    choice == urls.Count
                        ? Dialogs.Input("Direct HTTP(S) stream URL")
                        : urls[choice];
                if (
                    !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    || uri.Scheme is not ("http" or "https")
                    || uri.UserInfo != ""
                    || uri.Query.Contains("X-Plex-Token", StringComparison.OrdinalIgnoreCase)
                )
                    throw new IOException(
                        "Enter a direct HTTP(S) radio URL without Plex credentials."
                    );
                if (!urls.Contains(url))
                {
                    urls.Add(url);
                    Store.Set(Scope.Key, "stations", JsonSerializer.Serialize(urls));
                }
                reconnects = 0;
                await Start(External(uri.Host, 0), true, uri);
                break;
            case "Local clip":
                var picker = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "Audio|*.wav;*.flac;*.m4a;*.mp3",
                };
                if (picker.ShowDialog() != true)
                    return;
                using (var f = TagLib.File.Create(picker.FileName))
                {
                    var local = External(
                        Path.GetFileName(picker.FileName),
                        (long)f.Properties.Duration.TotalMilliseconds
                    );
                    var clipStart = Cue.Parse(Dialogs.Input("Clip start", "0:00"));
                    var clipStop = Dialogs.Input("Clip stop", local.Length);
                    var cue = new Cue(clipStart, clipStop == local.Length ? local.Duration : Cue.Parse(clipStop));
                    cue.Validate(local.Duration);
                    await Start(local, false, new Uri(picker.FileName), cue);
                }
                break;
            case "Up Next":
                Section = cmd;
                Show(Queue);
                break;
            case "Play":
            case "Play full track once":
                await PlaySelection(cmd != "Play");
                break;
            case "Toggle":
                if(cast.DeviceName!=null){await cast.Control("toggle");break;}
                if (Current == null && Selected == null && Queue.Count > 0)
                {
                    index = Math.Clamp(index, 0, Queue.Count-1);
                    await Start(Queue[index]);
                }
                else if (Current == null)
                    await PlaySelection(false);
                else
                {
                    audio.Toggle();
                }
                break;
            case "Next":
            case "Ended":
            case "Previous":
                if(cast.DeviceName!=null){if(cmd!="Ended")await cast.Control(cmd=="Previous"?"previous":"next");break;}
                await Advance(cmd == "Previous" ? -1 : 1, cmd == "Ended");
                break;
            case "Add to Queue":
            case "Play Next":
                int at = cmd == "Play Next" ? Math.Clamp(index + 1, 0, Queue.Count) : Queue.Count;
                foreach (var t in await SelectedTracks())
                    Queue.Insert(at++, t);
                break;
            case "Remove from queue":
                if (Section != "Up Next" || Selected == null)
                    throw new IOException("Select an entry in Up Next.");
                int pos = SelectedQueueIndex();
                if (pos >= 0)
                {
                    Queue.RemoveAt(pos);
                    if (pos <= index)
                        index--;
                }
                break;
            case "Library Shuffle":
                var shuffled=await (Api ?? throw new IOException("Connect to Plex first.")).Call(
                    $"/library/sections/{Plex.E(Scope.Library)}/all?type=10&sort=random&X-Plex-Container-Size=100");
                var sample=shuffled.Items("Metadata").Take(100).Select(Track.Read).ToList();
                if (sample.Count == 0)
                    return;
                Queue.Clear();
                foreach (var t in sample)
                    Queue.Add(t);
                index = 0;
                queueId = "";
                SetStationName("");
                await Start(Queue[0]);
                break;
            case "Sonically similar radio":
                var seed = (await SelectedTracks()).Single();
                await PlayRadio(new("sonic", "", seed.Title + " Radio"), [seed.Id]);
                break;
            case "Edit…":
                var metadataApi = Api!;
                var metadataScope = Scope;
                var editIds = (await SelectedTracks()).Select(t => t.Id).Distinct().ToArray();
                if (!MetadataWindow.ConfirmSelection(editIds.Length)) break;
                var metadata = await TrackMetadata.Open(metadataApi, metadataScope, editIds);
                if (Api != metadataApi || Scope != metadataScope) break;
                var changes = MetadataWindow.Edit(metadata, this);
                if (changes != null)
                {
                    try { await metadata.Save(Store,changes); }
                    finally { await Sync(); }
                }
                break;
            case "Song Info":
                var sel = (await SelectedTracks()).DistinctBy(t => t.Id).ToArray();
                if (sel.Any(t => t.LibraryId is { Length: > 0 } && t.LibraryId != Scope.Library))
                    throw new IOException(
                        "Connect to the track’s music library to edit this entry."
                    );
                if (MetadataWindow.ConfirmSelection(sel.Length))
                    new Editor(this, sel, await ReadCue(Scope,sel[0])).ShowDialog();
                break;
            case "Delete tracks from server":
            case "Rename file":
            case "Organize files by artist":
                await BatchFiles(cmd);
                break;
            case "Undo":
            case "Redo":
                await ReplayHistory(cmd == "Redo");
                break;
            case "Share mapping":
                if (Api == null)
                    throw new IOException("Select a library first.");
                var root = Dialogs.Input("Plex library root (absolute Linux path)");
                var share = Dialogs.Input("Writable Windows UNC directory");
                if (
                    !root.StartsWith('/')
                    || root.Split('/').Any(p => p is "." or "..")
                    || !share.StartsWith(@"\\")
                )
                    throw new IOException("Enter an absolute Plex root and UNC directory.");
                var maps = JsonSerializer.Deserialize<List<Mapping>>(
                    Store.Get(Scope.Key, "mappings") ?? "[]"
                )!;
                maps.RemoveAll(m => m.ServerRoot == root);
                maps.Add(new(root, share));
                Store.Set(Scope.Key, "mappings", JsonSerializer.Serialize(maps));
                Status = "Share mapping saved for this library.";
                break;
        }
    }

    static Track External(string title, long ms) =>
        new("", title, "", "", "", "", "", 0, 0, 0, ms, "", "", 0, "", []);

    public void Dispose()
    {
        playlistRead?.Cancel();
        Task.Run(()=>cast.Control("stop")).GetAwaiter().GetResult();
        SaveQueue();
        scrolling.Cancel();
        sync.Cancel();
        connection.Cancel();
        search.Cancel();
        Stop();
        Api?.Dispose();
        audio.Dispose();
    }
}
