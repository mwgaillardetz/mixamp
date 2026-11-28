using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mixamp.Core;
using Sharpcaster;
using Sharpcaster.Models;
using Sharpcaster.Models.Media;
using Sharpcaster.Models.Queue;
using Track = Mixamp.Core.Track;

namespace Mixamp;

sealed record CastChoice(string Id,string Name);
sealed record CastSong(string Id,string Title,string Artist,string Album,string Format)
{
    public static CastSong From(Track t) => new(t.Id,t.Title,t.ArtistDisplay,t.Album,t.Parts.FirstOrDefault()?.Container ?? t.Format);
}

// Only the selected receiver can read this session's unguessable media paths.
// Authentication to Plex stays inside the desktop app.
sealed class CastSession
{
    Dictionary<string,ChromecastReceiver> devices=[];
    ChromecastClient? client;
    long? mediaSession;
    IReadOnlyList<CastSong> queue=[];
    public bool Playing => client?.MediaStatus?.PlayerState is PlayerStateType.Playing or PlayerStateType.Buffering;
    public CastSong? CurrentSong
    {
        get
        {
            var tracks=queue;
            if(tracks.Count==0)return null;
            var id=client?.MediaStatus?.Media?.ContentId;
            return Uri.TryCreate(id,UriKind.Absolute,out var url) && int.TryParse(url.Segments.Last(),out var index)
                && (uint)index<tracks.Count ? tracks[index] : tracks[0];
        }
    }
    public double Position => client?.MediaStatus?.CurrentTime ?? 0;
    readonly object lifetime=new();
    CancellationTokenSource? starting;

    WebApplication? relay;
    public string? DeviceName { get; private set; }
    readonly SemaphoreSlim gate=new(1);

    public async Task<List<CastChoice>> Discover()
    {
        using var locator=new ChromecastLocator();
        var found=await locator.FindReceiversAsync();
        var snapshot=found.ToDictionary(_=>Guid.NewGuid().ToString("N"),d=>d);
        devices=snapshot;
        return snapshot.Select(d=>new CastChoice(d.Key,d.Value.Name)).OrderBy(d=>d.Name).ToList();
    }

    static string Mime(string extension) => extension.TrimStart('.').ToLowerInvariant() switch {
        "mp3"=>"audio/mpeg","flac"=>"audio/flac","m4a" or "mp4" or "alac"=>"audio/mp4","aac"=>"audio/aac",
        "wav"=>"audio/wav","ogg" or "opus"=>"audio/ogg","webm"=>"audio/webm",_=>"application/octet-stream"
    };

    public async Task Start(string deviceId,Plex api,Scope scope,IReadOnlyList<CastSong> songs,double startSeconds)
    {
        if(!devices.TryGetValue(deviceId,out var device))throw new IOException("Choose the speaker again.");
        if(songs.Count==0 || songs.Any(s=>string.IsNullOrEmpty(s.Id)))throw new IOException("Choose a Plex track to cast.");
        if(!IPAddress.TryParse(device.DeviceUri.Host.Trim('[',']'),out var peer))throw new IOException("The speaker has no usable network address.");
        using var handoff=new CancellationTokenSource();
        lock(lifetime){starting?.Cancel();starting=handoff;}
        var ct=handoff.Token;
        await gate.WaitAsync();
        try
        {
            ct.ThrowIfCancellationRequested();
            await Close();
            using var route=new Socket(peer.AddressFamily,SocketType.Dgram,ProtocolType.Udp);
            route.Connect(new IPEndPoint(peer,device.Port));
            var local=((IPEndPoint)route.LocalEndPoint!).Address;
            var builder=WebApplication.CreateSlimBuilder(new WebApplicationOptions {Args=[]});
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel(options=>options.Listen(local,0));
            relay=builder.Build();
            var ticket=Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            relay.MapMethods("/"+ticket+"/{index:int}",["GET","HEAD","OPTIONS"],async (int index,HttpContext ctx)=>{
                if(ctx.Connection.RemoteIpAddress?.MapToIPv6().Equals(peer.MapToIPv6())!=true || (uint)index>=songs.Count)
                {ctx.Response.StatusCode=404;return;}
                ctx.Response.Headers.AccessControlAllowOrigin="*";
                ctx.Response.Headers.AccessControlAllowMethods="GET, HEAD, OPTIONS";
                ctx.Response.Headers.AccessControlAllowHeaders="Range";
                ctx.Response.Headers.AccessControlExposeHeaders="Content-Length, Content-Range, Accept-Ranges";
                if(HttpMethods.IsOptions(ctx.Request.Method)){ctx.Response.StatusCode=204;return;}
                var ct=ctx.RequestAborted;
                var t=await api.Fresh(songs[index].Id,ct);
                if(t.LibraryId!=scope.Library || t.Parts is not [var part] || part.Key==""){ctx.Response.StatusCode=404;return;}
                System.Net.Http.Headers.RangeHeaderValue? range=null;
                if(ctx.Request.Headers.TryGetValue("Range",out var value) &&
                    (!System.Net.Http.Headers.RangeHeaderValue.TryParse(value.ToString(),out range) || range.Unit!="bytes" || range.Ranges.Count!=1))
                {ctx.Response.StatusCode=416;return;}
                using var source=await api.OpenAudio(part.Key,range,ct);
                ctx.Response.StatusCode=(int)source.StatusCode;
                if(source.Content.Headers.ContentRange is {} bounds)ctx.Response.Headers.ContentRange=bounds.ToString();
                if(!source.IsSuccessStatusCode)return;
                ctx.Response.ContentType=Mime(part.Container);
                ctx.Response.ContentLength=source.Content.Headers.ContentLength;
                if(source.Headers.AcceptRanges.Contains("bytes") || source.StatusCode==HttpStatusCode.PartialContent)
                    ctx.Response.Headers.AcceptRanges="bytes";
                if(!HttpMethods.IsHead(ctx.Request.Method))await source.Content.CopyToAsync(ctx.Response.Body,ct);
            });
            await relay.StartAsync(ct);
            var origin=relay.Urls.Single();
            client=new ChromecastClient();
            client.Disconnected+=ReceiverDisconnected;
            await client.ConnectChromecast(device).WaitAsync(TimeSpan.FromSeconds(12),ct);
            await client.LaunchApplicationAsync("CC1AD845").WaitAsync(ct);
            var items=songs.Select((s,i)=>new QueueItem {
                Media=new Media { ContentId=$"{origin}/{ticket}/{i}", ContentType=Mime(s.Format),
                    Metadata=new MusicTrackMetadata {Title=s.Title,SongName=s.Title,Artist=s.Artist,AlbumName=s.Album} },
                StartTime=i==0 ? (long)Math.Max(0,startSeconds) : null
            }).ToArray();
            // Keep each Cast message under the receiver's payload limit for normal track metadata.
            foreach(var batch in items.Chunk(20).Select((group,i)=>(group,i))){
                var status=batch.i==0 ? await client.MediaChannel.QueueLoadAsync(batch.group).WaitAsync(ct) :
                    await client.MediaChannel.QueueInsertAsync(batch.group).WaitAsync(ct);
                if(status==null || status.IdleReason=="ERROR")throw new IOException("The speaker did not accept the music queue.");
                mediaSession=status.MediaSessionId;
            }
            queue=songs;
            DeviceName=device.Name;
        }
        catch(Exception error)
        {
            await Close();
            if(error is IOException)throw;
            throw new IOException("Could not start playback on the Cast speaker. Check that it is reachable and try again.",error);
        }
        finally
        {
            lock(lifetime){if(starting==handoff)starting=null;}
            gate.Release();
        }
    }

    public Task Control(string command)
    {
        if(command=="stop")lock(lifetime){starting?.Cancel();}
        return Task.Run(()=>ControlReceiver(command));
    }

    async Task ControlReceiver(string command)
    {
        await gate.WaitAsync();
        try
        {
            if(command=="stop"){await Close();return;}
            if(mediaSession is null || mediaSession!=client?.MediaStatus?.MediaSessionId)
                throw new IOException("This Cast session has ended. Choose the speaker again.");
            var channel=client!.MediaChannel;
            _=command switch {
                "toggle"=>client.MediaStatus?.PlayerState==PlayerStateType.Paused ? await channel.PlayAsync() : await channel.PauseAsync(),
                "play"=>await channel.PlayAsync(),"pause"=>await channel.PauseAsync(),
                "next"=>await channel.QueueNextAsync(),"previous"=>await channel.QueuePrevAsync(),
                _=>throw new ArgumentException("Unknown Cast control.")
            };
        }
        finally{gate.Release();}
    }

    void ReceiverDisconnected(object? sender,EventArgs e)
    {
        _=Task.Run(async ()=>{
            await gate.WaitAsync();
            try{if(ReferenceEquals(client,sender))await Close();}
            finally{gate.Release();}
        });
    }

    async Task Close()
    {
        var current=client;client=null;DeviceName=null;queue=[];
        if(current!=null){
            current.Disconnected-=ReceiverDisconnected;
            try{if(mediaSession.HasValue && current.MediaStatus?.MediaSessionId==mediaSession)await current.MediaChannel.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));}catch{}
            try{await current.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(2));}
            catch{try{await current.Dispose().WaitAsync(TimeSpan.FromSeconds(1));}catch{}}
        }
        mediaSession=null;
        if(relay!=null){
            var host=relay;relay=null;
            using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try{await host.StopAsync(stop.Token);}finally{await host.DisposeAsync();}
        }
    }
}
