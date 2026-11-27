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
    long begin,
        end;
    double volume = .7;
    readonly System.Windows.Threading.Dispatcher ui = System.Windows.Application.Current.Dispatcher;
    Windows.Web.Http.HttpClient? hlsClient;
    Windows.Media.Streaming.Adaptive.AdaptiveMediaSource? adaptive;
    public string Route { get; private set; } = "";
    public event Action? Ended;
    public event Action<bool>? StateChanged;
    public event Action<string>? Failed;
    public event Action<int>? Skip;
    public bool Playing => player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
    public long Position => (long)(player?.PlaybackSession.Position.TotalMilliseconds ?? 0);
    public double Volume
    {
        get => volume;
        set
        {
            volume = Math.Clamp(value, 0, 1);
            if (player != null)
                player.Volume = volume;
        }
    }

    public async Task Start(Track t, Cue cue, Plex? api, Uri? uri, CancellationToken ct)
    {
        IRandomAccessStream? input = null;
        if (uri == null)
        {
            if (t.Parts.Length != 1)
                throw new IOException("Choose a single-version, single-part track for playback.");
            var p = t.Parts[0];
            if (p.Key.Length == 0)
                throw new IOException("Plex did not supply a safe media part key.");
            try
            {
                var probe = await api!.Range(p.Key, 0, 1, p.Size > 0 ? p.Size : null, ct);
                ct.ThrowIfCancellationRequested();
                input = new RangeAccess(api, p.Key, probe.Size);
            }
            catch (IOException) when (cue.Start == null && cue.End == null)
            { /* Negotiate streaming on a non-range route. */
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
            string ext = (
                uri?.IsFile == true
                    ? Path.GetExtension(uri.LocalPath).TrimStart('.')
                    : t.Parts.FirstOrDefault()?.Container ?? ""
            ).ToLowerInvariant();
            bool direct = input != null && api != null && uri == null;
            if (input == null && uri == null)
                media = await Transcode(api!, t, ct);
            else
                media =
                    input == null
                        ? MediaSource.CreateFromUri(uri!)
                        : MediaSource.CreateFromStream(
                            input,
                            ext switch
                            {
                                "mp3" => "audio/mpeg",
                                "m4a" or "mp4" => "audio/mp4",
                                "wav" => "audio/wav",
                                "flac" => "audio/flac",
                                _ => "application/octet-stream",
                            }
                        );
            Route =
                input == null && uri == null ? "Plex audio transcode · HLS"
                : direct ? "Direct play · authenticated HTTP ranges"
                : uri?.IsFile == true ? "Local file · Windows media engine"
                : "Internet radio · live";
            begin = cue.Start ?? 0;
            end = cue.End ?? t.Duration;
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
                            Route = "Plex audio transcode · HLS";
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
            Stop();
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
        if (player?.PlaybackSession.CanSeek == true && end > begin)
            player.PlaybackSession.Position = TimeSpan.FromMilliseconds(Math.Clamp(ms, begin, end));
    }

    public void Toggle()
    {
        if (Playing)
            player?.Pause();
        else
            player?.Play();
    }

    public void Stop()
    {
        var old = player;
        player = null;
        old?.Dispose();
        media?.Dispose();
        media = null;
        random?.Dispose();
        random = null;
        adaptive?.Dispose();
        adaptive = null;
        hlsClient?.Dispose();
        hlsClient = null;
    }

    public void Dispose() => Stop();
}
