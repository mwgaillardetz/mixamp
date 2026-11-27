using System.IO;
using Mixamp.Core;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace Mixamp;

interface IPlayback : IDisposable
{
    event Action? Ended;
    event Action<bool>? StateChanged;
    event Action<string>? Failed;
    event Action<int>? Skip;
    Task Start(Track track, Cue cue, Plex? server, Uri? source, CancellationToken ct);
    string EqMode { get; set; }
    string EqProfile { get; }
    string Route { get; }
    bool Playing { get; }
    long Position { get; }
    double Volume { get; set; }
    void Toggle();
    void Seek(long ms);
    void Stop();
}

sealed class Playback : IPlayback
{
    MediaPlayer? player;
    MediaSource? media;
    IRandomAccessStream? random;
    string? originalPath;
    long begin,
        end;
    double volume = .7;
    readonly System.Windows.Threading.Dispatcher ui = System.Windows.Application.Current.Dispatcher;
    Windows.Web.Http.HttpClient? hlsClient;
    Windows.Media.Streaming.Adaptive.AdaptiveMediaSource? adaptive;
    ToneOutput? tone;
    string genre = "", eqMode = "Auto";
    public string EqProfile => tone == null ? "Flat" : ToneProfile.Choose(eqMode, genre).Name;
    public string EqMode
    {
        get => eqMode;
        set { eqMode = value; tone?.Apply(ToneProfile.Choose(value, genre)); }
    }
    public string Route { get; private set; } = "";
    public event Action? Ended;
    public event Action<bool>? StateChanged;
    public event Action<string>? Failed;
    public event Action<int>? Skip;
    public bool Playing => tone?.Playing ?? player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
    public long Position => tone?.Position ?? (long)(player?.PlaybackSession.Position.TotalMilliseconds ?? 0);
    public double Volume
    {
        get => volume;
        set
        {
            volume = Math.Clamp(value, 0, 1);
            if (tone != null) tone.Volume = volume;
            if (player != null)
                player.Volume = volume;
        }
    }

    public async Task Start(Track t, Cue cue, Plex? api, Uri? uri, CancellationToken ct)
    {
        IRandomAccessStream? input = null;
        bool buffered = false;
        if (uri == null)
        {
            if (t.Parts.Length != 1)
                throw new IOException("Choose a single-version, single-part track for playback.");
            var p = t.Parts[0];
            if (p.Key.Length == 0)
                throw new IOException("Plex did not supply a safe media part key.");
            try
            {
                var probe = await api!.Range(p.Key, 0, 1, null, ct);
                ct.ThrowIfCancellationRequested();
                input = new RangeAccess(api, p.Key, probe.Size);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                buffered = true;
            }
        }
        else if (uri.IsFile)
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(uri.LocalPath);
            input = await file.OpenReadAsync();
        }
        if (ct.IsCancellationRequested)
        {
            input?.Dispose();
            ct.ThrowIfCancellationRequested();
        }
        Stop();
        random = input;
        try
        {
            var part = t.Parts.FirstOrDefault();
            string ext = uri?.IsFile == true ? Path.GetExtension(uri.LocalPath)
                : string.IsNullOrWhiteSpace(part?.Container) ? Path.GetExtension(part?.File) ?? "" : part.Container;
            string mime = ext.TrimStart('.').ToLowerInvariant() switch
            {
                "mp3" => "audio/mpeg", "m4a" or "mp4" or "alac" => "audio/mp4",
                "aac" => "audio/aac", "wav" or "wave" => "audio/wav", "flac" => "audio/flac",
                "ogg" or "oga" or "opus" => "audio/ogg", "wma" or "asf" => "audio/x-ms-wma",
                "aif" or "aiff" => "audio/aiff", _ => "application/octet-stream",
            };
            bool direct = api != null && uri == null;
            if (buffered)
                random = await BufferedOriginal(api!, part!, ct);
            media = random == null ? MediaSource.CreateFromUri(uri!) : MediaSource.CreateFromStream(random, mime);
            Route = direct ? "Direct play"
                : uri?.IsFile == true ? "Local file · Windows media engine"
                : "Internet radio · live";
            begin = cue.Start ?? 0;
            end = cue.End ?? t.Duration;
            genre = t.Genre;
            if (uri?.IsFile != false)
            {
                ToneOutput? candidate = null;
                try
                {
                    candidate = await ToneOutput.Open(MediaSource.CreateFromStream(random!.CloneStream(), mime), cue, ct);
                    ct.ThrowIfCancellationRequested();
                    candidate.Apply(ToneProfile.Choose(eqMode, genre));
                    candidate.Volume = volume;
                    var activeTone = candidate;
                    activeTone.Ended += () => ui.InvokeAsync(() => { if (tone == activeTone) Ended?.Invoke(); });
                    activeTone.Failed += () => ui.InvokeAsync(() =>
                    {
                        if (tone == activeTone) { Stop(); Failed?.Invoke("Audio output disconnected."); }
                    });
                    activeTone.Toggle();
                    tone = activeTone;
                    BindToneControls(t, activeTone);
                    _ = ui.InvokeAsync(() => { if (tone == activeTone) StateChanged?.Invoke(true); });
                    return;
                }
                catch (OperationCanceledException) { candidate?.Dispose(); throw; }
                catch (Exception)
                {
                    candidate?.Dispose();
                    ct.ThrowIfCancellationRequested();
                    // The neutral player uses its original, independent stream.
                }
            }
            var start = TimeSpan.FromMilliseconds(begin);
            var item = cue.End.HasValue
                ? new MediaPlaybackItem(media, start, TimeSpan.FromMilliseconds(end - begin))
                : new MediaPlaybackItem(media, start);
            var props = item.GetDisplayProperties();
            props.Type = MediaPlaybackType.Music;
            props.MusicProperties.Title = t.Title;
            props.MusicProperties.Artist = t.ArtistDisplay;
            item.ApplyDisplayProperties(props);
            var active = player = new MediaPlayer { Volume = volume };
            bool completed = false;
            active.PlaybackSession.PlaybackStateChanged += (session, e) =>
                ui.InvokeAsync(() =>
                {
                    if (player != active)
                        return;
                    var state = session.PlaybackState;
                    if (state is MediaPlaybackState.Playing or MediaPlaybackState.Paused)
                        StateChanged?.Invoke(state == MediaPlaybackState.Playing);
                });
            active.MediaEnded += (s, e) =>
                ui.InvokeAsync(() =>
                {
                    if (player == active && !completed)
                    {
                        completed = true;
                        Ended?.Invoke();
                    }
                });
            active.MediaFailed += (s, e) =>
                ui.InvokeAsync(async () =>
                {
                    if (player != active)
                        return;
                    if (direct && !buffered)
                    {
                        buffered = true;
                        try
                        {
                            var original = await BufferedOriginal(api!, part!, ct);
                            ct.ThrowIfCancellationRequested();
                            random?.Dispose();
                            random = original;
                            media?.Dispose();
                            media = MediaSource.CreateFromStream(original, mime);
                            active.Source = cue.End.HasValue
                                ? new MediaPlaybackItem(media, start, TimeSpan.FromMilliseconds(end - begin))
                                : new MediaPlaybackItem(media, start);
                            active.Play();
                            return;
                        }
                        catch (OperationCanceledException) { return; }
                        catch (Exception ex)
                        {
                            if (player != active) return;
                            Stop();
                            Failed?.Invoke("Could not read the original Plex file. " + Session.Message(ex));
                            return;
                        }
                    }
                    if (direct && cue == new Cue())
                    {
                        direct = false;
                        try
                        {
                            var fallback = await Transcode(api!, t, ct);
                            if (active != player)
                            {
                                fallback.Dispose();
                                return;
                            }
                            media?.Dispose();
                            media = fallback;
                            active.Source = new MediaPlaybackItem(media);
                            Route = "Transcoding";
                            active.Play();
                            return;
                        }
                        catch (Exception)
                        {
                            if (active != player)
                                return;
                        }
                    }
                    Stop();
                    Failed?.Invoke(
                        cue != new Cue()
                            ? "Windows could not play this clip through the seekable route. Use Play full track once to allow a transcode."
                            : "Windows playback failed ("
                                + e.Error
                                + "). Check the connection and installed codec."
                    );
                });
            active.CommandManager.NextReceived += (s, e) =>
            {
                e.Handled = true;
                ui.InvokeAsync(() =>
                {
                    if (player == active)
                        Skip?.Invoke(1);
                });
            };
            active.CommandManager.PreviousReceived += (s, e) =>
            {
                e.Handled = true;
                ui.InvokeAsync(() =>
                {
                    if (player == active)
                        Skip?.Invoke(-1);
                });
            };
            active.CommandManager.PositionReceived += (s, e) =>
            {
                var target = (long)e.Position.TotalMilliseconds;
                e.Handled = true;
                ui.InvokeAsync(() =>
                {
                    if (player == active)
                        Seek(target);
                });
            };
            active.CommandManager.NextBehavior.EnablingRule = MediaCommandEnablingRule.Always;
            active.CommandManager.PreviousBehavior.EnablingRule = MediaCommandEnablingRule.Always;
            active.Source = item;
            active.Play();
        }
        catch
        {
            if (!ct.IsCancellationRequested) Stop();
            throw;
        }
    }

    void BindToneControls(Track track, ToneOutput active)
    {
        try
        {
            player = new MediaPlayer();
            player.CommandManager.IsEnabled = false;
            var controls = player.SystemMediaTransportControls;
            controls.IsEnabled = controls.IsPlayEnabled = controls.IsPauseEnabled = true;
            controls.IsNextEnabled = controls.IsPreviousEnabled = true;
            var display = controls.DisplayUpdater;
            display.Type = MediaPlaybackType.Music;
            display.MusicProperties.Title = track.Title;
            display.MusicProperties.Artist = track.ArtistDisplay;
            display.Update();
            controls.PlaybackStatus = MediaPlaybackStatus.Playing;
            controls.ButtonPressed += (_, e) => ui.InvokeAsync(() =>
            {
                if (tone != active) return;
                if (e.Button == SystemMediaTransportControlsButton.Next) Skip?.Invoke(1);
                else if (e.Button == SystemMediaTransportControlsButton.Previous) Skip?.Invoke(-1);
                else if ((e.Button == SystemMediaTransportControlsButton.Play && !Playing)
                    || (e.Button == SystemMediaTransportControlsButton.Pause && Playing)) Toggle();
            });
            controls.PlaybackPositionChangeRequested += (_, e) => ui.InvokeAsync(() =>
            {
                if (tone == active) Seek((long)e.RequestedPlaybackPosition.TotalMilliseconds);
            });
        }
        catch (System.Runtime.InteropServices.COMException) { player?.Dispose(); player = null; }
    }

    async Task<IRandomAccessStream> BufferedOriginal(Plex api, Part part, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mixamp-audio-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await api.CopyOriginal(part.Key, output, ct);
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path).AsTask(ct);
            var stream = await file.OpenReadAsync().AsTask(ct);
            if (ct.IsCancellationRequested)
            {
                stream.Dispose();
                ct.ThrowIfCancellationRequested();
            }
            originalPath = path;
            return stream;
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    async Task<MediaSource> Transcode(Plex api, Track track, CancellationToken ct)
    {
        string path =
            "/audio/:/transcode/universal/start.m3u8?protocol=hls&directPlay=0&directStream=0&audioCodec=aac&mediaIndex=0&partIndex=0&offset=0&path="
            + Plex.E("/library/metadata/" + track.Id);
        var client = new Windows.Web.Http.HttpClient(new MediaFilter(api));
        try
        {
            var result = await Windows
                .Media.Streaming.Adaptive.AdaptiveMediaSource.CreateFromUriAsync(
                    api.Resolve(path),
                    client
                )
                .AsTask(ct);
            ct.ThrowIfCancellationRequested();
            if (
                result.Status
                != Windows.Media.Streaming.Adaptive.AdaptiveMediaSourceCreationStatus.Success
            )
                throw new IOException("Plex audio transcode unavailable.");
            adaptive = result.MediaSource;
            hlsClient = client;
            return MediaSource.CreateFromAdaptiveMediaSource(adaptive);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    sealed class MediaFilter(Plex api) : Windows.Web.Http.Filters.IHttpFilter
    {
        readonly Windows.Web.Http.Filters.HttpBaseProtocolFilter filter = new()
        {
            AllowAutoRedirect = false,
            AllowUI = false,
            CookieUsageBehavior = Windows.Web.Http.Filters.HttpCookieUsageBehavior.NoCookies,
        };

        public Windows.Foundation.IAsyncOperationWithProgress<
            Windows.Web.Http.HttpResponseMessage,
            Windows.Web.Http.HttpProgress
        > SendRequestAsync(Windows.Web.Http.HttpRequestMessage request)
        {
            api.ApplyMediaHeaders(request.RequestUri, (k, v) =>
                request.Headers.TryAppendWithoutValidation(k, v)
            );
            return filter.SendRequestAsync(request);
        }

        public void Dispose() => filter.Dispose();
    }

    public void Seek(long ms)
    {
        if (tone != null && end > begin) tone.Seek(Math.Clamp(ms, begin, end));
        else if (player?.PlaybackSession.CanSeek == true && end > begin)
            player.PlaybackSession.Position = TimeSpan.FromMilliseconds(Math.Clamp(ms, begin, end));
    }

    public void Toggle()
    {
        if (tone != null)
        {
            tone.Toggle();
            if (player != null) player.SystemMediaTransportControls.PlaybackStatus =
                tone.Playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
            StateChanged?.Invoke(tone.Playing);
            return;
        }
        if (Playing)
            player?.Pause();
        else
            player?.Play();
    }

    public void Stop()
    {
        var oldTone = tone;
        tone = null;
        oldTone?.Dispose();
        var old = player;
        player = null;
        old?.Dispose();
        media?.Dispose();
        media = null;
        random?.Dispose();
        random = null;
        if (originalPath != null)
        {
            try { File.Delete(originalPath); } catch (IOException) { }
            originalPath = null;
        }
        adaptive?.Dispose();
        adaptive = null;
        hlsClient?.Dispose();
        hlsClient = null;
    }

    public void Dispose() => Stop();
}
