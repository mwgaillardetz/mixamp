using System.Security.Cryptography;
using System.Text.Json;

namespace CrateDeck.Core;

// Callers establish the approved path and hold their source and directory handles through Commit.
public sealed class Replacement(Store store, Scope scope, string id)
{
    record Entry(
        string Path,
        string Before,
        string After,
        string Temp,
        string Backup,
        string Change
    );

    string Key => "write/" + id;

    public static string Digest(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f));
    }

    public string? Reconcile(string path, string change)
    {
        var value = store.Get(scope.Key, Key);
        if (string.IsNullOrEmpty(value))
            return null;
        var entry = JsonSerializer.Deserialize<Entry>(value)!;
        if (entry.Path != path)
            throw new IOException(
                "Mapping changed after a replacement. Review the backup at " + entry.Backup
            );
        string current = Digest(path);
        if (current == entry.After && entry.Change == change)
            return "Previously completed file change verified. Backup at " + entry.Backup;
        if (current != entry.After && current != entry.Before)
            throw new IOException(
                "External change or incomplete replacement. Review the backup at " + entry.Backup
            );
        store.Set(scope.Key, Key, "");
        return null;
    }

    public string Commit(
        string path,
        string before,
        string temp,
        string change,
        Action<string, string, string>? replace = null
    )
    {
        var entry = new Entry(path, before, Digest(temp), temp, temp + ".bak", change);
        if (Digest(path) != before)
            throw new IOException("Source changed during staging.");
        store.Set(scope.Key, Key, JsonSerializer.Serialize(entry));
        (replace ?? ((source, target, backup) => File.Replace(source, target, backup, false)))(
            temp,
            path,
            entry.Backup
        );
        if (Digest(path) != entry.After || Digest(entry.Backup) != before)
            throw new IOException(
                "Replacement acknowledgement uncertain. Backup at " + entry.Backup
            );
        return "Verified file tags and encoded frames. Backup at " + entry.Backup;
    }
}
