using Windows.Foundation;
using Windows.Storage.Streams;

namespace CrateDeck;

// The stock managed stream adapter omits CloneStream and GetInputStreamAt.
// Each decoder reader needs its own cursor and bounded range window.
sealed class RangeAccess : IRandomAccessStream
{
    readonly Plex server;
    readonly string key;
    readonly long length;
    readonly RangeStream cursor;
    readonly IInputStream input;

    public RangeAccess(Plex server, string key, long length)
    {
        this.server = server;
        this.key = key;
        this.length = length;
        cursor = new RangeStream(server, key, length);
        input = cursor.AsInputStream();
    }

    public bool CanRead => true;
    public bool CanWrite => false;
    public ulong Position => (ulong)cursor.Position;
    public ulong Size
    {
        get => (ulong)length;
        set => throw new NotSupportedException();
    }

    public void Seek(ulong position) => cursor.Position = checked((long)position);

    public IRandomAccessStream CloneStream() => new RangeAccess(server, key, length);

    public IInputStream GetInputStreamAt(ulong position) =>
        new RangeStream(server, key, length) { Position = checked((long)position) }.AsInputStream();

    public IAsyncOperationWithProgress<IBuffer, uint> ReadAsync(
        IBuffer buffer,
        uint count,
        InputStreamOptions options
    ) => input.ReadAsync(buffer, count, options);

    public IOutputStream GetOutputStreamAt(ulong position) => throw new NotSupportedException();

    public IAsyncOperationWithProgress<uint, uint> WriteAsync(IBuffer buffer) =>
        throw new NotSupportedException();

    public IAsyncOperation<bool> FlushAsync() => throw new NotSupportedException();

    public void Dispose() => input.Dispose();
}
