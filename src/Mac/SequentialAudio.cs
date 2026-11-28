using Microsoft.Win32.SafeHandles;

namespace Mixamp;

// WebKit can ask for several overlapping ranges even when Plex serves only a sequential response.
// Keep one temporary original for the active track. Readers can consume bytes as they arrive.
sealed class SequentialAudio : IDisposable
{
    readonly SafeFileHandle file = File.OpenHandle(Path.Combine(Path.GetTempPath(),"mixamp-audio-"+Guid.NewGuid().ToString("N")),
        FileMode.CreateNew,FileAccess.ReadWrite,FileShare.Read | FileShare.Delete,FileOptions.Asynchronous | FileOptions.DeleteOnClose);
    readonly CancellationTokenSource stop=new();
    readonly object gate=new();
    TaskCompletionSource changed=new(TaskCreationOptions.RunContinuationsAsynchronously);
    long available;
    bool complete;
    Exception? failure;

    public SequentialAudio(HttpResponseMessage response) => _ = Download(response);

    async Task Download(HttpResponseMessage response)
    {
        using (response)
        using (var idle=CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        try
        {
            await using var source=await response.Content.ReadAsStreamAsync(idle.Token);
            var buffer=new byte[64*1024];
            while(true)
            {
                idle.CancelAfter(TimeSpan.FromSeconds(20));
                int read=await source.ReadAsync(buffer,idle.Token);
                if(read==0)break;
                await RandomAccess.WriteAsync(file,buffer.AsMemory(0,read),available,idle.Token);
                lock(gate){available+=read;Wake();}
            }
        }
        catch(Exception ex) when(ex is IOException or HttpRequestException or OperationCanceledException or ObjectDisposedException)
        { lock(gate)failure=ex; }
        finally { lock(gate){complete=true;Wake();} }
    }

    void Wake()
    {
        var before=changed;
        changed=new(TaskCreationOptions.RunContinuationsAsynchronously);
        before.TrySetResult();
    }

    public async Task<int> Read(long offset,Memory<byte> buffer,CancellationToken ct)
    {
        int count;
        while(true)
        {
            Task wait;
            lock(gate)
            {
                count=(int)Math.Clamp(available-offset,0,buffer.Length);
                if(count>0)break;
                if(failure!=null)throw new IOException("Plex stopped sending audio.",failure);
                if(complete)return 0;
                wait=changed.Task;
            }
            await wait.WaitAsync(ct);
        }
        return await RandomAccess.ReadAsync(file,buffer[..count],offset,ct);
    }

    public void Dispose(){stop.Cancel();file.Dispose();stop.Dispose();}
}
