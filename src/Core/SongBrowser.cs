using System.Text.Json.Nodes;

namespace Mixamp.Core;

public sealed record SongPage(List<Track> Tracks,int? Total,bool More)
{
    public const int Size = 128;
}

public sealed partial class Library
{
    public async Task<SongPage> Songs(int offset,string column,bool descending,string search,CancellationToken ct=default)
    {
        if (offset<0) throw new ArgumentOutOfRangeException(nameof(offset));
        string field=column.ToLowerInvariant() switch {
            "title"=>"track.titleSort", "artistdisplay"=>"track.originalTitle", "artist"=>"artist.titleSort",
            "album"=>"album.titleSort", "disc"=>"track.parentIndex", "number"=>"track.index", "year"=>"album.year",
            "length"=>"track.duration", "genre"=>"track.genre", "filepath"=>"track.file",
            "format"=>"track.audioCodec", "bitrate"=>"track.bitrate", _=>throw new ArgumentException("Unknown sort column.")
        };
        var path=$"/library/sections/{E(scope.Library)}/all?type=10&sort={field}:{(descending?"desc":"asc")}";
        var terms=MusicSearch.Words(search);
        if(terms.Length>0)
        {
            var groups=terms.Select(E).Select(term=>
                $"push=1&track.title={term}&or=1&album.title={term}&or=1&artist.title={term}&pop=1");
            path+="&"+string.Join("&and=1&",groups);
        }
        List<Track> tracks=[];
        int? total=null;
        while(tracks.Count<SongPage.Size)
        {
            int start=checked(offset+tracks.Count), size=SongPage.Size-tracks.Count;
            JsonNode page;
            try { page=await api.Call($"{path}&X-Plex-Container-Start={start}&X-Plex-Container-Size={size}",ct:ct); }
            catch(HttpRequestException ex) when(ex.StatusCode==System.Net.HttpStatusCode.BadRequest)
            { throw new IOException($"Plex could not apply this search or the {column} sort. Try another column or search.",ex); }
            var rows=page.Items("Metadata").ToArray();
            if (page["offset"]!=null && page.Num("offset")!=start || rows.Length>size)
                throw new IOException("Plex did not return the requested range.");
            if (page["totalSize"]!=null) total=checked((int)page.Num("totalSize"));
            if(total<0 || rows.Length>0 && (long)start+rows.Length>total)
                throw new IOException("The library changed during browsing. Refresh Songs.");
            tracks.AddRange(rows.Select(Track.Read));
            if(rows.Length==0 || start+rows.Length>=total || total==null && rows.Length<size)break;
        }
        bool more=total is int count ? offset+tracks.Count<count : tracks.Count==SongPage.Size;
        if(more && tracks.Count<SongPage.Size)throw new IOException("Plex returned an incomplete range of tracks.");
        return new(tracks,total,more);
    }

    public async Task<Cue> PlaybackCue(Store store,Track track,CancellationToken ct=default)
    {
        var cue=store.FindCue(scope,track,out bool verifyIdentity);
        if (!verifyIdentity) return cue;
        var matches=await api.Call($"/library/sections/{E(scope.Library)}/all?type=10&guid={E(track.Guid)}&X-Plex-Container-Size={SongPage.Size}",ct:ct);
        var rows=matches.Items("Metadata").ToArray();
        if (matches["totalSize"]==null || matches.Num("totalSize")!=rows.Length) return new();
        var same=rows.Select(Track.Read).Where(t=>t.Identity==track.Identity).ToArray();
        return same.Length==1 && same[0].Id==track.Id ? cue : new();
    }
}
