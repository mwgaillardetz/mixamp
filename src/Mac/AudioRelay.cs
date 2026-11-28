using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Mixamp.Core;

namespace Mixamp;

sealed class AudioRelay(Plex server,string key,long reportedSize,string name) : IDisposable
{
    readonly object gate=new();
    SequentialAudio? original;
    long originalSize;
    bool disposed;

    public async Task Send(HttpContext context)
    {
        var request=context.Request;
        var response=context.Response;
        RangeHeaderValue? range=null;
        if (request.Headers.TryGetValue("Range",out var header) &&
            (!RangeHeaderValue.TryParse(header.ToString(),out range) || range.Unit!="bytes" || range.Ranges.Count!=1))
        {
            response.StatusCode=416;
            return;
        }
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var idle=TimeSpan.FromSeconds(20);
        HttpResponseMessage? upstream=null;
        try
        {
            deadline.CancelAfter(idle);
            SequentialAudio? cached;
            long totalSize;
            lock(gate){cached=original;totalSize=originalSize;}
            if(cached==null)
            {
                upstream=await server.OpenAudio(key,range,deadline.Token);
                if (upstream.StatusCode==HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    response.StatusCode=416;
                    if(upstream.Content.Headers.ContentRange is {} invalid)response.Headers.ContentRange=invalid.ToString();
                    return;
                }
                upstream.EnsureSuccessStatusCode();
                totalSize=upstream.Content.Headers.ContentLength ?? reportedSize;
                if(upstream.StatusCode==HttpStatusCode.OK && range!=null && totalSize>0)
                {
                    lock(gate)
                    {
                        ObjectDisposedException.ThrowIf(disposed,this);
                        if(original==null)
                        {
                            originalSize=totalSize;
                            original=new SequentialAudio(upstream);
                            upstream=null; // The download owns this response through completion.
                        }
                        cached=original;
                        totalSize=originalSize;
                    }
                }
            }
            long position=0;
            long? length=cached!=null ? totalSize : upstream!.Content.Headers.ContentLength;
            if (cached==null && upstream!.StatusCode==HttpStatusCode.PartialContent)
            {
                var actual=upstream.Content.Headers.ContentRange;
                if (range==null || actual?.From is not long from || actual.To is not long to ||
                    actual.Length is not long total || total<=to || from>to ||
                    Bounds(range.Ranges.Single(),total)!=(from,to))
                    throw new IOException("Invalid audio range from Plex.");
                response.StatusCode=206;
                response.Headers.ContentRange=actual.ToString();
                length=to-from+1;
            }
            else if (cached!=null || upstream!.StatusCode==HttpStatusCode.OK)
            {
                long total=totalSize;
                if (range!=null && total>0)
                {
                    var (first,last)=Bounds(range.Ranges.Single(),total);
                    if (first>last)
                    {
                        response.StatusCode=416;
                        response.Headers.ContentRange=$"bytes */{total}";
                        return;
                    }
                    response.StatusCode=206;
                    response.Headers.ContentRange=$"bytes {first}-{last}/{total}";
                    position=first;
                    length=last-first+1;
                }
            }
            else throw new IOException("Unexpected audio response from Plex.");
            response.ContentLength=length;
            response.Headers.AcceptRanges="bytes";
            response.ContentType=Path.GetExtension(name).ToLowerInvariant() switch
            {
                ".mp3"=>"audio/mpeg", ".flac"=>"audio/flac", ".wav"=>"audio/wav", ".m4a" or ".mp4"=>"audio/mp4",
                ".aif" or ".aiff"=>"audio/aiff", ".aac"=>"audio/aac", ".ogg" or ".opus"=>"audio/ogg",
                _=>upstream?.Content.Headers.ContentType?.MediaType ?? "application/octet-stream"
            };
            if (HttpMethods.IsHead(request.Method))return;
            await using var input=cached==null ? await upstream!.Content.ReadAsStreamAsync(deadline.Token) : null;
            var buffer=new byte[32*1024];
            while(length!=0)
            {
                deadline.CancelAfter(idle);
                int wanted=(int)Math.Min(buffer.Length,length ?? buffer.Length);
                int read=cached==null ? await input!.ReadAsync(buffer.AsMemory(0,wanted),deadline.Token) :
                    await cached.Read(position,buffer.AsMemory(0,wanted),deadline.Token);
                if (read==0)
                {
                    if (length is >0)throw new EndOfStreamException();
                    break;
                }
                position+=read;
                await response.Body.WriteAsync(buffer.AsMemory(0,read),deadline.Token);
                length-=read;
            }
        }
        catch(Exception ex) when(ex is IOException or HttpRequestException or OperationCanceledException or ObjectDisposedException)
        {
            if (response.HasStarted || context.RequestAborted.IsCancellationRequested)context.Abort();
            else {response.Clear();response.StatusCode=deadline.IsCancellationRequested ? 504 : 502;}
        }
        finally{upstream?.Dispose();}
    }

    public void Dispose(){lock(gate){disposed=true;original?.Dispose();}}

    static (long First,long Last) Bounds(RangeItemHeaderValue range,long total) =>
        range.From is long first ? (first,Math.Min(range.To ?? total-1,total-1))
            : (Math.Max(0,total-range.To!.Value),total-1);
}
