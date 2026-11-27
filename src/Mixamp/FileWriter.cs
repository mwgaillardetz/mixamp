using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Mixamp.Core;
using Microsoft.Win32.SafeHandles;

namespace Mixamp;

sealed class FileWriter(Store store, Scope scope, Plex api)
{
    [StructLayout(LayoutKind.Sequential)]
    struct FileInfoNative
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created,
            Read,
            Written;
        public uint Volume,
            High,
            Low,
            Links,
            IndexHigh,
            IndexLow;
    }

    [DllImport("kernel32", SetLastError = true)]
    static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInfoNative info);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(
        string name,
        uint access,
        uint share,
        IntPtr security,
        uint create,
        uint flags,
        IntPtr template
    );

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        System.Text.StringBuilder buffer,
        uint size,
        uint flags
    );

    internal static FileStream OpenPart(string path)
    {
        var file = CreateFileW(path, 0x80000000, 5, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        try
        {
            var resolved = new System.Text.StringBuilder(32768);
            uint length = GetFinalPathNameByHandleW(file, resolved, (uint)resolved.Capacity, 0);
            if (
                file.IsInvalid
                || length == 0
                || length >= resolved.Capacity
                || !string.Equals(
                    resolved.ToString(),
                    @"\\?\UNC\" + path[2..],
                    StringComparison.OrdinalIgnoreCase
                )
            )
                throw new IOException("The server cannot verify the final path of this file.");
            return new FileStream(file, FileAccess.Read);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    internal sealed class DirectoryLease : IDisposable
    {
        readonly List<SafeFileHandle> pins = [];

        public DirectoryLease(string file)
        {
            var directories = new Stack<string>();
            for (var d = Path.GetDirectoryName(file); d != null; d = Path.GetDirectoryName(d))
                directories.Push(d);
            try
            {
                foreach (var directory in directories)
                {
                    // Open the directory itself, deny writes/deletes while its descendants are resolved.
                    var pin = CreateFileW(
                        directory,
                        0x80,
                        1,
                        IntPtr.Zero,
                        3,
                        0x02200000,
                        IntPtr.Zero
                    );
                    pins.Add(pin);
                    if (
                        pin.IsInvalid
                        || !GetFileInformationByHandle(pin, out var info)
                        || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0
                    )
                        throw new IOException(
                            "Cannot secure the mapped directory during replacement."
                        );
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            foreach (var pin in pins)
                pin.Dispose();
        }
    }

    public async Task<string> Write(Track t, Dictionary<string, string> edits, string? batch = null)
    {
        var fresh = await api.Fresh(t.Id);
        if (
            fresh.LibraryId != scope.Library
            || fresh.Identity != t.Identity
            || fresh.Parts.Length != 1
        )
            throw new IOException("Media identity changed or is ambiguous.");
        var part = fresh.Parts[0];
        var maps = JsonSerializer.Deserialize<Mapping[]>(store.Get(scope.Key, "mappings") ?? "[]")!;
        var path = MappedFiles.Resolve(part.File, maps);
        if (!TagEditor.Supported(path))
            throw new IOException("This tag variant is available for inspection only.");
        using var lease = new DirectoryLease(path);
        MappedFiles.Resolve(part.File, maps);
        string change = JsonSerializer.Serialize(edits.OrderBy(x => x.Key, StringComparer.Ordinal));
        var journal = new Replacement(store, scope, t.Id);
        if (journal.Reconcile(path, change) is { } result)
            return result;
        var tmp = Path.Combine(
            Path.GetDirectoryName(path)!,
            ".mixamp-" + Guid.NewGuid().ToString("N") + Path.GetExtension(path)
        );
        bool pending = false;
        try
        {
            using var handle = OpenPart(path);
            if (
                !GetFileInformationByHandle(handle.SafeFileHandle, out var info)
                || info.Links != 1
                || (info.Attributes & (uint)(FileAttributes.ReadOnly | FileAttributes.ReparsePoint))
                    != 0
                || handle.Length != part.Size
            )
                throw new IOException(
                    "File size, link count, or attributes prevent a verified replacement."
                );
            var before = Convert.ToHexString(await SHA256.HashDataAsync(handle));
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (long pos = 0; pos < part.Size; )
            {
                var bytes = (
                    await api.Range(part.Key, pos, 1024 * 1024, part.Size, CancellationToken.None)
                ).Data;
                hash.AppendData(bytes);
                pos += bytes.Length;
            }
            if (Convert.ToHexString(hash.GetHashAndReset()) != before)
                throw new IOException("The mapped file differs from the Plex media part.");
            File.Copy(path, tmp);
            TagEditor.EditCopy(tmp, edits);
            if (MappedFiles.Resolve(part.File, maps) != path)
                throw new IOException("Mapping changed during editing.");
            using var final = OpenPart(path);
            if (
                !GetFileInformationByHandle(final.SafeFileHandle, out var current)
                || current.Links != 1
                || current.Volume != info.Volume
                || current.IndexHigh != info.IndexHigh
                || current.IndexLow != info.IndexLow
            )
                throw new IOException("Source identity changed before replacement.");
            pending = true;
            string outcome = journal.Commit(path, before, tmp, change, recordBackup: batch == null ? null :
                (backup, after) => store.Remember(scope, batch, "Edit song information",
                    new(t, "Tags", part.File, backup, before, after, path)));
            pending = false;
            return outcome;
        }
        finally
        {
            if (!pending && File.Exists(tmp))
                File.Delete(tmp);
        }
    }
}
