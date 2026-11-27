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
    CancellationTokenSource connection = new(),
        sync = new(),
        playback = new(),
        search = new();
    readonly SemaphoreSlim database = new(1);
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
    string libraryProgress = "";
    public string LibraryProgress
    {
        get => libraryProgress;
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
            section = value;
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
    public bool Playing => audio.Playing;
    public string TransportAction => Playing ? "Pause" : "Play";
    public bool Descending => descending;
    public sealed record PlaylistEntry(string Id, string Name);
    public IReadOnlyList<PlaylistEntry> Playlists { get; private set; } = [];
    IReadOnlyDictionary<string, Track>? snapshot;
    public string Now => Current?.Title ?? "Ready to play";
    public System.Windows.Media.Imaging.BitmapImage? Artwork { get; private set; }
    public string Byline =>
        Current == null
            ? ""
            : Current.ArtistDisplay + "   ·   " + Current.Format.ToUpperInvariant();
    public Track? Current { get; private set; }
    public string Clock => Current == null ? "0:00" : Cue.Format(audio.Position);
    public long Position => audio.Position;
    public bool Seekable => Current?.Duration > 0;
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

    public async Task Cached(bool reconnect = false)
    {
        if (Store.Get("installation", "lastScope") is not { } value)
            return;
        Scope = JsonSerializer.Deserialize<Scope>(value)!;
        Caption = Store.Get(Scope.Key, "caption") ?? "Cached library";
        LibraryProgress = "Loading saved library…";
        try { await Refresh(); }
        finally { LibraryProgress = ""; }
        Status = "Cached library ready.";
        if (reconnect && Api == null && !connection.IsCancellationRequested)
        {
            try { await Connect(Scope.Account == "demo" && Scope.Server == "mixamp-demo", Scope); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Status = "Saved library available. " + Message(ex); }
        }
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
            if (resume == null) snapshot = null;
            Playlists = [];
            Changed(nameof(Playlists));
            Caption = LibraryName + "  /  " + name;
            Store.Set("installation", "lastScope", JsonSerializer.Serialize(Scope));
            Store.Set(Scope.Key, "caption", Caption);
            RestoreQueue();
            Section = "Songs";
            if (resume == null) await Refresh();
            await Sync();
            await ReloadPlaylists();
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
        var token = sync.Token;
        var g = generation;
        Status = "Syncing music library…";
        try
        {
            LibraryProgress = "Checking library…";
            bool changed = await Library.Sync(Store,
                () => token.IsCancellationRequested || snapshot?.Count > 0 ? Task.CompletedTask : Refresh(),
                token, (count, total, incremental) =>
                {
                    if (token.IsCancellationRequested) return;
                    var action = incremental ? "Checking for changes" : "Loading library";
                    LibraryProgress = total < 0 ? action + "…" : $"{action} · {count:N0} / {total:N0}";
                });
            if (g == generation && !token.IsCancellationRequested)
            {
                if (changed) await Refresh();
                Status = changed ? "Library synced." : "Library is up to date.";
            }
        }
        catch
        {
            if (g == generation && sync.Token == token) await Refresh();
            throw;
        }
        finally
        {
            if (sync.Token == token) LibraryProgress = "";
        }
    }

    async void ScheduleSearch()
    {
        search.Cancel();
        search = new();
        var ct = search.Token;
        try
        {
            await Task.Delay(60, ct);
            await Refresh(ct, reuse: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status = Message(ex);
        }
    }

    public async Task Refresh(CancellationToken ct = default, bool reuse = false)
    {
        if (!reuse)
            snapshot = null;
        if (Section != "Songs")
            return;
        int seq = ++readGeneration;
        var scope = Scope;
        string term = SearchText,
            sort = order;
        var cached = snapshot;
        bool desc = descending;
        await database.WaitAsync(ct);
        List<Track> result;
        try
        {
            if (seq != readGeneration)
                return;
            result = await Task.Run(() =>
            {
                if (cached == null)
                {
                    var all = Store.Search(scope, sort: sort, descending: desc, limit: -1, ct: ct);
                    cached = all.ToDictionary(t => t.Id);
                    if (term.Length == 0)
                        return all;
                }
                return Store.Search(scope, term, sort, desc, limit: -1, snapshot: cached, ct: ct);
            }, ct);
        }
        finally
        {
            database.Release();
        }
        if (seq == readGeneration && Section == "Songs" && scope == Scope && !ct.IsCancellationRequested)
        {
            snapshot = cached;
            Show(result);
        }
    }

    void Show(IReadOnlyList<Track> list)
    {
        Rows = list;
        Changed(nameof(Rows));
    }

    public Task Sort(string col)
    {
        descending = col == order && !descending;
        order = col;
        return Refresh(reuse: true);
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
                    ?? Store.ReadCue(
                        track.LibraryId is { Length: > 0 }
                            ? Scope with
                            {
                                Library = track.LibraryId,
                            }
                            : Scope,
                        track
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

    async Task PlaySelection(bool full)
    {
        var selected = Selection();
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
        if (next >= Queue.Count && queueId != "" && Queue.Count > 0)
        {
            var last = Queue[^1].QueueItem;
            var pendingQueue=queueId;
            var window=sonicRadio
                ? await Library.ContinueSonic(queueId,last,Queue[^1].Id,Queue.Select(t=>t.Id).ToArray())
                : await Library.ContinueQueue(queueId,last);
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
        playlistId = id;
        Section = "Playlists";
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
        var id = playlistId;
        var api = Api;
        var tracks = (await Library.PlaylistItems(id)).Items("Metadata").Select(Track.Read).ToList();
        if (Section == "Playlists" && playlistId == id && api == Api)
            Show(tracks);
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

    public Task<List<RadioChoice>> RadioChoices(RadioOption option) => Library.RadioChoices(option, Store);

    int radioPlayRequest;
    public void CancelPendingStation() => ++radioPlayRequest;
    public async Task PlayRadio(RadioOption option, IReadOnlyList<string> choices)
    {
        var api = Api;
        int request = ++radioPlayRequest;
        int browse = stationRequest;
        string originSection = Section;
        var q = await Library.StartRadio(option, choices, Store);
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
                await Refresh(reuse: true);
                break;
            case "New playlist":
            case "Add to playlist":
                var library = Library;
                var api = Api;
                var tracks = Selection();
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
                    int i = Rows.ToList().IndexOf(Selected);
                    if (i < 1)
                        return;
                    await Library.Move(
                        playlistId,
                        Selected.PlaylistItem,
                        i == 1 ? "0" : Rows[i - 2].PlaylistItem
                    );
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
                await Advance(cmd == "Previous" ? -1 : 1, cmd == "Ended");
                break;
            case "Add to Queue":
            case "Play Next":
                int at = cmd == "Play Next" ? Math.Clamp(index + 1, 0, Queue.Count) : Queue.Count;
                foreach (var t in Selection())
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
                var sample = await Task.Run(() => Store.Sample(Scope));
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
                var seed = Selection().Single();
                await PlayRadio(new("sonic", "", seed.Title + " Radio"), [seed.Id]);
                break;
            case "Edit…":
                var metadataApi = Api!;
                var metadataScope = Scope;
                var metadata = await TrackMetadata.Open(metadataApi,metadataScope,Selection().Select(t=>t.Id));
                if (Api != metadataApi || Scope != metadataScope) break;
                var changes = MetadataWindow.Edit(metadata, this);
                if (changes != null)
                {
                    try { await metadata.Save(Store,changes); }
                    finally { await Sync(); }
                }
                break;
            case "Song Info":
                var sel = Selection();
                if (sel.Any(t => t.LibraryId is { Length: > 0 } && t.LibraryId != Scope.Library))
                    throw new IOException(
                        "Connect to the track’s music library to edit this entry."
                    );
                if (sel.Length > 0)
                    new Editor(this, sel).ShowDialog();
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
        SaveQueue();
        sync.Cancel();
        connection.Cancel();
        search.Cancel();
        Stop();
        Api?.Dispose();
        audio.Dispose();
    }
}
