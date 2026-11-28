using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Mixamp.Core;

public sealed class Store
{
    readonly string catalog,
        settings;

    public Store(string directory, bool useCatalog = true)
    {
        Directory.CreateDirectory(directory);
        catalog = Path.Combine(directory, "catalog.db");
        settings = Path.Combine(directory, "preferences.db");
        if (useCatalog) InitializeCatalog();
        using var prefs = Open(settings);
        Sql(
                prefs,
                "CREATE TABLE IF NOT EXISTS prefs(scope TEXT,k TEXT,v TEXT, PRIMARY KEY(scope,k)); CREATE TABLE IF NOT EXISTS undo(id INTEGER PRIMARY KEY,scope TEXT,batch TEXT,name TEXT,data TEXT); CREATE INDEX IF NOT EXISTS undo_scope ON undo(scope,id); CREATE TABLE IF NOT EXISTS redo(id INTEGER PRIMARY KEY,scope TEXT,batch TEXT,name TEXT,data TEXT); CREATE INDEX IF NOT EXISTS redo_scope ON redo(scope,id);"
            )
            .ExecuteNonQuery();
    }

    void InitializeCatalog()
    {
        using var db = Open(catalog);
        Sql(
                db,
                """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS tracks(scope TEXT,id TEXT,title TEXT COLLATE NOCASE,artist TEXT COLLATE NOCASE,albumArtist TEXT COLLATE NOCASE,album TEXT COLLATE NOCASE,disc INTEGER,number INTEGER,year INTEGER,duration INTEGER,data TEXT,stamp TEXT,PRIMARY KEY(scope,id));
                CREATE VIRTUAL TABLE IF NOT EXISTS words USING fts5(title,artist,albumArtist,album,genre);
                CREATE TRIGGER IF NOT EXISTS remove_words AFTER DELETE ON tracks BEGIN DELETE FROM words WHERE rowid=OLD.rowid; END;
                """
            )
            .ExecuteNonQuery();
        foreach (var (key, col) in Columns)
            Sql(db, $"CREATE INDEX IF NOT EXISTS idx_{(col.StartsWith("json_extract") ? key + "_sort" : col)} ON tracks(scope,{col},id)")
                .ExecuteNonQuery();
        Sql(
                db,
                "CREATE INDEX IF NOT EXISTS idx_genre ON tracks(scope,json_extract(data,'$.Genre'),artist,album)"
            )
            .ExecuteNonQuery();
        Sql(db, "CREATE INDEX IF NOT EXISTS idx_guid ON tracks(scope,json_extract(data,'$.Guid'))")
            .ExecuteNonQuery();
        foreach (var id in new[] { "AlbumId", "ArtistId" })
            Sql(
                    db,
                    $"CREATE INDEX IF NOT EXISTS idx_{id} ON tracks(scope,json_extract(data,'$.{id}'))"
                )
                .ExecuteNonQuery();
    }

    static readonly Dictionary<string, string> Columns = new()
    {
        ["Title"] = "title",
        ["ArtistDisplay"] = "artist",
        ["Artist"] = "albumArtist",
        ["Album"] = "album",
        ["Disc"] = "disc",
        ["Number"] = "number",
        ["Year"] = "year",
        ["Length"] = "duration",
        ["Genre"] = "json_extract(data,'$.Genre') COLLATE NOCASE",
        ["FilePath"] = "json_extract(data,'$.Parts[0].File') COLLATE NOCASE",
        ["Format"] = "json_extract(data,'$.Format') COLLATE NOCASE",
        ["Bitrate"] = "json_extract(data,'$.Bitrate')",
    };

    static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path }.ToString()
        );
        db.Open();
        return db;
    }

    static SqliteCommand Sql(SqliteConnection db, string query, params (string, object)[] args)
    {
        var c = db.CreateCommand();
        c.CommandText = query;
        foreach (var (k, v) in args)
            c.Parameters.AddWithValue(k, v);
        return c;
    }

    public void Page(Scope s, IReadOnlyList<Track> rows, string stamp, IReadOnlyDictionary<string,string>? revisions = null)
    {
        using var db = Open(catalog);
        using var tx = db.BeginTransaction();
        using var cmd = Sql(
            db,
            """
            INSERT INTO tracks VALUES($s,$i,$t,$p,$a,$b,$d,$n,$y,$l,$j,$g)
            ON CONFLICT(scope,id) DO UPDATE SET title=$t,artist=$p,albumArtist=$a,album=$b,disc=$d,number=$n,year=$y,duration=$l,data=$j,stamp=$g RETURNING rowid;
            """
        );
        foreach (var name in "$s $i $t $p $a $b $d $n $y $l $j $g".Split())
            cmd.Parameters.Add(new SqliteParameter(name, null));
        using var fts = Sql(
            db,
            "INSERT OR REPLACE INTO words(rowid,title,artist,albumArtist,album,genre) VALUES($r,$t,$p,$a,$b,$h)"
        );
        foreach (var name in "$r $t $p $a $b $h".Split())
            fts.Parameters.Add(new SqliteParameter(name, null));
        foreach (var t in rows)
        {
            object[] vals =
            [
                s.Key,
                t.Id,
                t.Title,
                t.ArtistDisplay,
                t.Artist,
                t.Album,
                t.Disc,
                t.Number,
                t.Year,
                t.Duration,
                JsonSerializer.Serialize(t),
                revisions?[t.Id] ?? stamp,
            ];
            for (int i = 0; i < vals.Length; i++)
                cmd.Parameters[i].Value = vals[i];
            object[] words =
            [
                cmd.ExecuteScalar()!,
                t.Title,
                t.ArtistDisplay,
                t.Artist,
                t.Album,
                t.Genre,
            ];
            for (int i = 0; i < words.Length; i++)
                fts.Parameters[i].Value = words[i];
            fts.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void InvalidateMovedPaths(Scope scope, IReadOnlyList<Track> moved)
    {
        using var db = Open(catalog);
        using var tx = db.BeginTransaction();
        using var cmd = Sql(db,
            "UPDATE tracks SET stamp='' WHERE scope=$s AND id=$i AND json_extract(data,'$.Parts[0].File')=$old",
            ("$s", scope.Key), ("$i", ""), ("$old", ""));
        foreach (var track in moved)
        {
            cmd.Parameters["$i"].Value = track.Id;
            cmd.Parameters["$old"].Value = track.FilePath;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public Dictionary<string,string> Revisions(Scope scope)
    {
        using var db = Open(catalog);
        using var cmd = Sql(db, "SELECT id,stamp FROM tracks WHERE scope=$s", ("$s", scope.Key));
        using var rows = cmd.ExecuteReader();
        Dictionary<string,string> revisions = [];
        while (rows.Read()) revisions.Add(rows.GetString(0), rows.GetString(1));
        return revisions;
    }

    public void RemoveMissing(Scope scope, Dictionary<string,string> missing, CancellationToken ct)
    {
        using var db = Open(catalog);
        using var tx = db.BeginTransaction();
        using var cmd = Sql(db, "DELETE FROM tracks WHERE scope=$s AND id=$i AND stamp=$v",
            ("$s", scope.Key), ("$i", ""), ("$v", ""));
        foreach (var (id, version) in missing)
        {
            ct.ThrowIfCancellationRequested();
            cmd.Parameters["$i"].Value = id;
            cmd.Parameters["$v"].Value = version;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public bool Complete(Scope s, string stamp, int expected)
    {
        using var db = Open(catalog);
        using var tx = db.BeginTransaction();
        if (
            Convert.ToInt64(
                Sql(
                        db,
                        "SELECT count(*) FROM tracks WHERE scope=$s AND stamp=$g",
                        ("$s", s.Key),
                        ("$g", stamp)
                    )
                    .ExecuteScalar()
            ) != expected
        )
            return false;
        Sql(db, "DELETE FROM tracks WHERE scope=$s AND stamp<>$g", ("$s", s.Key), ("$g", stamp))
            .ExecuteNonQuery();
        tx.Commit();
        return true;
    }

    public List<string> Facets(Scope scope, int level, string genre, string artist)
    {
        string genreColumn = "json_extract(data,'$.Genre')";
        string col = level switch
        {
            0 => genreColumn,
            1 => "artist",
            2 => "album",
            _ => throw new ArgumentException(),
        };
        using var db = Open(catalog);
        using var cmd = Sql(
            db,
            $"SELECT DISTINCT {col} FROM tracks WHERE scope=$s AND {col}<>'' AND ($g='' OR {genreColumn}=$g) AND ($a='' OR artist=$a) ORDER BY 1 LIMIT 256",
            ("$s", scope.Key),
            ("$g", genre),
            ("$a", artist)
        );
        using var reader = cmd.ExecuteReader();
        List<string> values = [];
        while (reader.Read())
            values.Add(reader.GetString(0));
        return values;
    }

    public List<(string Id, string Label)> Groups(Scope scope, bool albums, int page)
    {
        string id = albums ? "json_extract(data,'$.AlbumId')" : "json_extract(data,'$.ArtistId')";
        string label = albums ? "album || '   ·   ' || albumArtist" : "albumArtist";
        using var db = Open(catalog);
        using var cmd = Sql(
            db,
            $"SELECT {id}, {label} FROM tracks WHERE scope=$s AND {id}<>'' GROUP BY {id} ORDER BY 2 COLLATE NOCASE,1 LIMIT 129 OFFSET $offset",
            ("$s", scope.Key),
            ("$offset", page * 128)
        );
        using var reader = cmd.ExecuteReader();
        List<(string, string)> values = [];
        while (reader.Read())
            values.Add((reader.GetString(0), reader.GetString(1)));
        return values;
    }

    public List<Track> Search(
        Scope s,
        string search = "",
        string sort = "Title",
        bool descending = false,
        int page = 0,
        int limit = 160,
        string genre = "",
        string artist = "",
        string album = "",
        string albumId = "",
        string artistId = "",
        IReadOnlyDictionary<string, Track>? snapshot = null,
        CancellationToken ct = default
    )
    {
        using var db = Open(catalog);
        var terms = Regex
            .Matches(search, @"[\p{L}\p{M}\p{N}]+")
            .Take(16)
            .Select(m => "\"" + m.Value + "\"*")
            .ToArray();
        string match =
            terms.Length == 0 ? "" : "AND rowid IN (SELECT rowid FROM words WHERE words MATCH $q)";
        if (genre != "")
            match += " AND json_extract(data,'$.Genre')=$genre";
        if (artist != "")
            match += " AND artist=$artist";
        if (album != "")
            match += " AND album=$album";
        if (albumId != "")
            match += " AND json_extract(data,'$.AlbumId')=$albumId";
        if (artistId != "")
            match += " AND json_extract(data,'$.ArtistId')=$artistId";
        string column = Columns.GetValueOrDefault(sort, "title");
        using var cmd = Sql(
            db,
            $"SELECT {(snapshot == null ? "data" : "id")} FROM tracks WHERE scope=$s {match} ORDER BY {column} {(descending ? "DESC" : "ASC")},id LIMIT $l OFFSET $o",
            ("$s", s.Key),
            ("$l", limit),
            ("$o", checked(page * limit))
        );
        if (genre != "")
            cmd.Parameters.AddWithValue("$genre", genre);
        if (artist != "")
            cmd.Parameters.AddWithValue("$artist", artist);
        if (album != "")
            cmd.Parameters.AddWithValue("$album", album);
        if (albumId != "")
            cmd.Parameters.AddWithValue("$albumId", albumId);
        if (artistId != "")
            cmd.Parameters.AddWithValue("$artistId", artistId);
        if (terms.Length > 0)
            cmd.Parameters.AddWithValue("$q", string.Join(" AND ", terms));
        using var r = cmd.ExecuteReader();
        List<Track> result = [];
        while (r.Read())
        {
            ct.ThrowIfCancellationRequested();
            var value = r.GetString(0);
            if (snapshot == null)
                result.Add(JsonSerializer.Deserialize<Track>(value)!);
            else if (snapshot.TryGetValue(value, out var track))
                result.Add(track);
        }
        return result;
    }

    public List<Track> Sample(Scope scope)
    {
        using var db = Open(catalog);
        using var cmd = Sql(
            db,
            "SELECT data FROM tracks WHERE scope=$s ORDER BY random() LIMIT 100",
            ("$s", scope.Key)
        );
        using var reader = cmd.ExecuteReader();
        List<Track> result = [];
        while (reader.Read())
            result.Add(JsonSerializer.Deserialize<Track>(reader.GetString(0))!);
        return result;
    }

    static string SeedColumn(string kind) => kind switch
    {
        "artist" => "json_extract(data,'$.ArtistId')",
        "album" => "json_extract(data,'$.AlbumId')",
        _ => throw new ArgumentException("Choose an artist or album mix.")
    };

    public List<RadioChoice> MixSeeds(Scope scope, string kind)
    {
        var column = SeedColumn(kind);
        var label = kind == "artist" ? "albumArtist" : "album || ' — ' || albumArtist";
        using var db = Open(catalog);
        using var cmd = Sql(db, $"SELECT {column}, {label} FROM tracks WHERE scope=$s AND {column}<>'' GROUP BY {column} ORDER BY 2 COLLATE NOCASE", ("$s",scope.Key));
        using var reader = cmd.ExecuteReader();
        List<RadioChoice> choices = [];
        while (reader.Read()) choices.Add(new(reader.GetString(0), reader.GetString(1)));
        return choices;
    }

    public List<string> MixTrackIds(Scope scope, string kind, IReadOnlyList<string> seeds)
    {
        var column = SeedColumn(kind);
        var unique = seeds.Distinct().ToArray();
        if (unique.Length is 0 or > 100) throw new ArgumentException("Choose between 1 and 100 seeds.");
        using var db = Open(catalog);
        List<Queue<string>> groups = [];
        foreach (var seed in unique)
        {
            using var cmd = Sql(db, $"SELECT id FROM tracks WHERE scope=$s AND {column}=$seed ORDER BY random() LIMIT 100", ("$s",scope.Key), ("$seed",seed));
            using var reader = cmd.ExecuteReader();
            Queue<string> group = new();
            while (reader.Read()) group.Enqueue(reader.GetString(0));
            if (group.Count > 0) groups.Add(group);
        }
        return InterleaveMix(groups);
    }

    internal static List<string> InterleaveMix(List<Queue<string>> groups)
    {
        Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(groups));
        List<string> result = [];
        while (result.Count < 100 && groups.Count > 0)
        {
            foreach (var group in groups)
                if (result.Count < 100) result.Add(group.Dequeue());
            groups.RemoveAll(group => group.Count == 0);
        }
        return result;
    }

    public long Remember(Scope scope, string batch, string name, Change change)
    {
        using var db = Open(settings);
        using var transaction = db.BeginTransaction();
        using var clear = Sql(db, "DELETE FROM redo WHERE scope=$s", ("$s", scope.Key));
        clear.ExecuteNonQuery();
        using var cmd = Sql(db, "INSERT INTO undo(scope,batch,name,data) VALUES($s,$b,$n,$d) RETURNING id",
            ("$s",scope.Key), ("$b",batch), ("$n",name), ("$d",JsonSerializer.Serialize(change)));
        var id = Convert.ToInt64(cmd.ExecuteScalar());
        transaction.Commit();
        return id;
    }

    public string? HistoryName(Scope scope, bool redo)
    {
        using var db = Open(settings);
        var table = redo ? "redo" : "undo";
        using var cmd = Sql(db, $"SELECT name FROM {table} WHERE scope=$s ORDER BY id DESC LIMIT 1", ("$s",scope.Key));
        return cmd.ExecuteScalar() as string;
    }

    public List<UndoStep> LastChanges(Scope scope, bool redo = false)
    {
        using var db = Open(settings);
        var table = redo ? "redo" : "undo";
        using var cmd = Sql(db, $"SELECT id,name,data FROM {table} WHERE scope=$s AND batch=(SELECT batch FROM {table} WHERE scope=$s ORDER BY id DESC LIMIT 1) ORDER BY id DESC", ("$s", scope.Key));
        using var r = cmd.ExecuteReader();
        List<UndoStep> steps = [];
        while (r.Read()) steps.Add(new(r.GetInt64(0), r.GetString(1), JsonSerializer.Deserialize<Change>(r.GetString(2))!));
        return steps;
    }

    public void Forget(Scope scope, long id)
    {
        using var db = Open(settings);
        using var cmd = Sql(db, "DELETE FROM undo WHERE scope=$s AND id=$i", ("$s",scope.Key), ("$i",id));
        cmd.ExecuteNonQuery();
    }

    public void CompleteChange(Scope scope, UndoStep step, bool redo)
    {
        var (source, target) = redo ? ("redo", "undo") : ("undo", "redo");
        using var db = Open(settings);
        using var transaction = db.BeginTransaction();
        using var transfer = Sql(db, $"INSERT INTO {target}(scope,batch,name,data) SELECT scope,batch,name,$d FROM {source} WHERE scope=$s AND id=$i AND data=$old",
            ("$s", scope.Key), ("$i", step.Id), ("$old", JsonSerializer.Serialize(step.Change)), ("$d", JsonSerializer.Serialize(step.Change.Reverse())));
        if (transfer.ExecuteNonQuery() != 1) throw new IOException("Change history was updated. Reopen Edit and try again.");
        using var remove = Sql(db, $"DELETE FROM {source} WHERE scope=$s AND id=$i", ("$s", scope.Key), ("$i", step.Id));
        remove.ExecuteNonQuery();
        transaction.Commit();
    }

    public string? Get(string scope, string key)
    {
        using var db = Open(settings);
        return Sql(db, "SELECT v FROM prefs WHERE scope=$s AND k=$k", ("$s", scope), ("$k", key))
                .ExecuteScalar() as string;
    }

    public void Set(string scope, string key, string value)
    {
        using var db = Open(settings);
        Sql(
                db,
                "INSERT OR REPLACE INTO prefs VALUES($s,$k,$v)",
                ("$s", scope),
                ("$k", key),
                ("$v", value)
            )
            .ExecuteNonQuery();
    }

    // One replaceable row across all libraries and sorts. No tracks accumulate as the user scrolls.
    sealed record LaunchSongs(Scope Scope, string Column, bool Descending, SongPage Page);
    const int LaunchLimit = 256 * 1024;

    public SongPage? ReadLaunchSongs(Scope scope, string column, bool descending)
    {
        try
        {
            var json = Get("installation", "launchSongs");
            if (string.IsNullOrEmpty(json) || json.Length > LaunchLimit) return null;
            var saved = JsonSerializer.Deserialize<LaunchSongs>(json);
            return saved?.Scope == scope && saved.Descending == descending &&
                string.Equals(saved.Column, column, StringComparison.OrdinalIgnoreCase) &&
                saved.Page?.Tracks is { Count: <= SongPage.Size } tracks &&
                !(saved.Page.Total < tracks.Count) ? saved.Page : null;
        }
        catch (Exception ex) when (ex is JsonException or SqliteException) { return null; }
    }

    public void SaveLaunchSongs(Scope scope, string column, bool descending, SongPage page)
    {
        if (page.Tracks.Count > SongPage.Size) return;
        var json = JsonSerializer.Serialize(new LaunchSongs(scope,column,descending,page));
        if (System.Text.Encoding.UTF8.GetByteCount(json) > LaunchLimit) json = "";
        try
        {
            using var db = Open(settings);
            using var update = Sql(db, """
                INSERT INTO prefs(scope,k,v) VALUES('installation','launchSongs',$json)
                ON CONFLICT(scope,k) DO UPDATE SET v=excluded.v WHERE v<>excluded.v
                """, ("$json",json));
            update.ExecuteNonQuery();
        }
        catch (SqliteException) { /* Live browsing also works when the optional snapshot cannot be saved. */ }
    }

    public Cue FindCue(Scope s, Track t, out bool verifyIdentity)
    {
        verifyIdentity = false;
        string key = "cue/" + t.Id + "/" + t.Identity;
        if (Get(s.Key, key) is { } value)
            return JsonSerializer.Deserialize<Cue>(value)!;
        if (t.Guid == "" || t.Parts.Length != 1 || t.Parts[0].File == "" || t.Parts[0].Size <= 0)
            return new();
        using var settingsDb = Open(settings);
        using var lookup = Sql(
            settingsDb,
            "SELECT v FROM prefs WHERE scope=$s AND k LIKE $suffix LIMIT 2",
            ("$s", s.Key),
            ("$suffix", "cue/%/" + t.Identity)
        );
        using var r = lookup.ExecuteReader();
        if (!r.Read())
            return new();
        string previous = r.GetString(0);
        if (r.Read())
            return new();
        verifyIdentity = true;
        return JsonSerializer.Deserialize<Cue>(previous)!;
    }

    public Cue ReadCue(Scope s, Track t)
    {
        var cue = FindCue(s,t,out bool verifyIdentity);
        if (!verifyIdentity) return cue;
        using var db = Open(catalog);
        using var items = Sql(
            db,
            "SELECT data FROM tracks WHERE scope=$s AND json_extract(data,'$.Guid')=$guid",
            ("$s", s.Key),
            ("$guid", t.Guid)
        );
        using var matches = items.ExecuteReader();
        int count = 0;
        bool current = false;
        while (matches.Read())
        {
            var candidate = JsonSerializer.Deserialize<Track>(matches.GetString(0))!;
            if (candidate.Identity != t.Identity)
                continue;
            count++;
            current |= candidate.Id == t.Id;
        }
        if (count != 1 || !current)
            return new();
        return cue;
    }

    public void SaveCue(Scope s, Track t, Cue cue)
    {
        cue.Validate(t.Duration);
        Set(s.Key, "cue/" + t.Id + "/" + t.Identity, JsonSerializer.Serialize(cue));
    }
}
