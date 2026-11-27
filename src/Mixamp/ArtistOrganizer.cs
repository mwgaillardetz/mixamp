using Mixamp.Core;

namespace Mixamp;

sealed record ArtistMove(Track Track, string Source, string Destination, string ServerDestination, DateTime Modified);

static class ArtistOrganizer
{
    static string Folder(string name)
    {
        string result = string.Concat(name.Trim().Select(c =>
            c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c)).TrimEnd('.', ' ');
        if (result.Length == 0) throw new IOException("Artist or album folder name is empty.");
        return result;
    }

    public static string Destination(Track track, Mapping mapping, string artist)
    {
        if (track.Parts.Length != 1) throw new IOException("Multiple or missing media parts.");
        string album = string.IsNullOrWhiteSpace(track.Album) ? "Unknown Album" : track.Album;
        string filename = track.FilePath.Split('/')[^1];
        return $"{mapping.ServerRoot.TrimEnd('/')}/{Folder(artist)}/{Folder(album)}/{filename}";
    }

    public static ArtistMove Plan(Track track, Mapping[] maps, string artist)
    {
        var root = MappedFiles.Match(track.FilePath, maps);
        var serverDestination = Destination(track, root, artist);
        if (MappedFiles.Match(serverDestination, maps) != root)
            throw new IOException("Destination belongs to another mapping.");
        var source = MappedFiles.Resolve(track.FilePath, maps);
        var destination = MappedFiles.Resolve(serverDestination, maps, allowMissing: true);
        if (StringComparer.OrdinalIgnoreCase.Equals(source, destination))
            throw new IOException("Already in the artist folder.");
        if (Path.Exists(destination)) throw new IOException("Destination already exists.");
        var file = new FileInfo(source);
        if (file.Length != track.Parts[0].Size) throw new IOException("Source size changed.");
        return new(track, source, destination, serverDestination, file.LastWriteTimeUtc);
    }

    public static void Move(ArtistMove plan, Mapping[] maps)
    {
        var source = MappedFiles.Resolve(plan.Track.FilePath, maps);
        var destination = MappedFiles.Resolve(plan.ServerDestination, maps, allowMissing: true);
        if (source != plan.Source || destination != plan.Destination)
            throw new IOException("Mapping changed after review.");
        using var sourceLease = new FileWriter.DirectoryLease(source);
        // Pin each parent before creating its child on the same mapped share.
        var root = Path.GetFullPath(MappedFiles.Match(plan.ServerDestination, maps).Share);
        var parent = Path.GetDirectoryName(destination)!;
        var dir = root;
        foreach (var segment in Path.GetRelativePath(root, parent).Split(Path.DirectorySeparatorChar))
        {
            dir = Path.Combine(dir, segment);
            using var parentLease = new FileWriter.DirectoryLease(dir);
            Directory.CreateDirectory(dir);
        }
        using var destinationLease = new FileWriter.DirectoryLease(destination);
        var file = new FileInfo(MappedFiles.Resolve(plan.Track.FilePath, maps));
        if (file.Length != plan.Track.Parts[0].Size || file.LastWriteTimeUtc != plan.Modified)
            throw new IOException("Source changed after review.");
        File.Move(source, destination, overwrite: false);
    }
    public static void RestoreFile(Change change, Mapping[] maps)
    {
        string original = MappedFiles.Resolve(change.From, maps, allowMissing:true);
        if (original != change.SourcePath) throw new IOException("The library mapping changed.");
        if (change.Type == "Move")
        {
            string moved = MappedFiles.Resolve(change.To, maps, allowMissing:true);
            if (moved != change.TargetPath) throw new IOException("The library mapping changed.");
            if (!File.Exists(moved) && File.Exists(original) && Replacement.Digest(original) == change.Before) return;
            if (Path.Exists(original) || Replacement.Digest(moved) != change.After)
                throw new IOException("The original path is occupied or the moved file has changed.");
            var part = change.Track.Parts.Single() with { File = change.To };
            var track = change.Track with { Parts = [part] };
            ArtistOrganizer.Move(new(track, moved, original, change.From, File.GetLastWriteTimeUtc(moved)), maps);
            return;
        }
        using var lease = new FileWriter.DirectoryLease(original);
        using var originalHandle = FileWriter.OpenPart(original);
        var current = Replacement.Digest(original);
        if (current == change.Before) return;
        if (Path.GetDirectoryName(change.To) != Path.GetDirectoryName(original)
            || (File.GetAttributes(change.To) & FileAttributes.ReparsePoint) != 0
            || current != change.After)
            throw new IOException("The edited file or its backup changed.");
        using (var backupHandle = FileWriter.OpenPart(change.To))
        {
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(backupHandle));
            if (digest != change.Before) throw new IOException("Backup changed.");
        }
        string opposite = change.Reverse().To;
        if (Path.GetDirectoryName(opposite) != Path.GetDirectoryName(original) || Path.Exists(opposite))
            throw new IOException("The history backup path is occupied.");
        File.Replace(change.To, original, opposite);
    }
}
