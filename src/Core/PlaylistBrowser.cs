namespace Mixamp.Core;

public static class MusicSearch
{
    public static string[] Words(string query) => query.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static bool Matches(Track track,string[] words) => words.All(word =>
        Has(track.Title,word) || Has(track.Artist,word) || Has(track.Performer,word) ||
        Has(track.Album,word) || Has(track.Genre,word) || Has(track.FilePath,word));

    static bool Has(string field,string word) => field.Contains(word,StringComparison.OrdinalIgnoreCase);
}

// One browser belongs to an open playlist. Ordinary scrolling seeks directly by Plex entry offset.
// A search advances only far enough to supply the requested results. Explicit sorting needs the whole result.
public sealed class PlaylistBrowser(Library library,string id)
{
    static readonly Track Unavailable=new("","Unavailable playlist entry","","","","","",0,0,0,0,"","",0,"",[]);
    readonly SemaphoreSlim gate=new(1);
    List<Track>? unpaged;
    (string Query,string Sort,bool Descending) filter;
    List<Track> matches=[];
    int scanned;
    bool finished;

    public async Task<SongPage> Read(int offset,string query="",string sort="",bool descending=false,CancellationToken ct=default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            query=query.Trim();
            if(filter!=(query,sort,descending)){filter=(query,sort,descending);matches=[];scanned=0;finished=false;}
            if(query=="" && sort=="")return await Range(offset,SongPage.Size,ct).ConfigureAwait(false);
            var words=MusicSearch.Words(query);
            while(!finished && (sort!="" || matches.Count<(long)offset+SongPage.Size))
            {
                var page=await Range(scanned,512,ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                matches.AddRange(page.Tracks.Where(t=>t.Id!="" && MusicSearch.Matches(t,words)));
                scanned+=page.Tracks.Count;
                if(!page.More)
                {
                    if(sort!="")matches=Sort(matches,sort,descending);
                    finished=true;
                }
            }
            return Slice(matches,offset,finished);
        }
        finally{gate.Release();}
    }

    async Task<SongPage> Range(int offset,int size,CancellationToken ct)
    {
        if(unpaged!=null)return Slice(unpaged,offset,true,size);
        var tracks=new List<Track>(size);
        int? total;
        bool more;
        do
        {
            var page=await library.PlaylistTracks(id,checked(offset+tracks.Count),ct,size-tracks.Count).ConfigureAwait(false);
            int valid=0,missing=0;
            for(int i=0;i<page.Tracks.Count+page.Skipped;i++)
            {
                if(missing<page.Skipped && page.Missing[missing]==i){tracks.Add(Unavailable);missing++;}
                else tracks.Add(page.Tracks[valid++]);
            }
            total=page.Total;more=page.More;
        }while(more && tracks.Count<size);
        // Some servers ignore ranges entirely. Keep their single response for this view and send only the window to the UI.
        if(offset==0 && !more && tracks.Count>size){unpaged=tracks;return Slice(tracks,0,true,size);}
        if(tracks.Count>size)return new(tracks.GetRange(0,size),total,true);
        return new(tracks,total,more);
    }

    static SongPage Slice(List<Track> list,int offset,bool complete,int size=SongPage.Size)
    {
        int count=Math.Clamp(list.Count-offset,0,size);
        return new(count==0 ? [] : list.GetRange(offset,count),complete ? list.Count : null,!complete || offset+count<list.Count);
    }

    static List<Track> Sort(List<Track> tracks,string column,bool descending)
    {
        Func<Track,IComparable> key=column.ToLowerInvariant() switch {
            "title"=>t=>t.Title,"artistdisplay"=>t=>t.ArtistDisplay,"artist"=>t=>t.Artist,"album"=>t=>t.Album,
            "disc"=>t=>t.Disc,"number"=>t=>t.Number,"year"=>t=>t.Year,"length"=>t=>t.Duration,
            "genre"=>t=>t.Genre,"filepath"=>t=>t.FilePath,"format"=>t=>t.Format,"bitrate"=>t=>t.Bitrate,
            _=>throw new ArgumentException("Unknown playlist sort.")
        };
        var comparer=Comparer<IComparable>.Create((a,b)=>a is string s && b is string t
            ? StringComparer.CurrentCultureIgnoreCase.Compare(s,t) : a.CompareTo(b));
        return (descending?tracks.OrderByDescending(key,comparer):tracks.OrderBy(key,comparer)).ToList();
    }
}
