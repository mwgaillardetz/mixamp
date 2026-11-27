using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CrateDeck.Core;

public sealed class Store
{
    readonly string catalog,
        settings;

    public Store(string directory)
    {
        Directory.CreateDirectory(directory);
        catalog = Path.Combine(directory, "catalog.db");
        settings = Path.Combine(directory, "preferences.db");
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
        foreach (var col in Columns.Values.Distinct())
            Sql(db, $"CREATE INDEX IF NOT EXISTS idx_{col} ON tracks(scope,{col},id)")
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
        using var prefs = Open(settings);
        Sql(
                prefs,
                "CREATE TABLE IF NOT EXISTS prefs(scope TEXT,k TEXT,v TEXT, PRIMARY KEY(scope,k));"
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

    public void Page(Scope s, IReadOnlyList<Track> rows, string stamp)
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
                stamp,
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
        string artistId = ""
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
            $"SELECT data FROM tracks WHERE scope=$s {match} ORDER BY {column} {(descending ? "DESC" : "ASC")},id LIMIT $l OFFSET $o",
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
            result.Add(JsonSerializer.Deserialize<Track>(r.GetString(0))!);
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

    public Cue ReadCue(Scope s, Track t)
    {
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
        return JsonSerializer.Deserialize<Cue>(previous)!;
    }

    public void SaveCue(Scope s, Track t, Cue cue)
    {
        cue.Validate(t.Duration);
        Set(s.Key, "cue/" + t.Id + "/" + t.Identity, JsonSerializer.Serialize(cue));
    }
}
