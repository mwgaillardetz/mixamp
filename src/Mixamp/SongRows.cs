using System.Collections;
using System.Collections.Specialized;
using Mixamp.Core;

namespace Mixamp;

// WPF can navigate to any index without enumerating tracks or requesting intervening ranges.
sealed class SongRows : IList, IReadOnlyList<Track>, INotifyCollectionChanged
{
    internal sealed record Placeholder(int Position) : Track("","","","","","","",0,0,0,0,"","",0,"",[]);
    readonly Dictionary<int,Track> loaded = [];
    readonly HashSet<int> ranges = [];
    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    public int Count { get; private set; }
    public Track this[int index] => index>=0 && index<Count
        ? loaded.TryGetValue(index,out var track) ? track : new Placeholder(index)
        : throw new ArgumentOutOfRangeException(nameof(index));

    public bool HasPage(int offset) => ranges.Contains(offset);

    public void Apply(int offset, SongPage page, int firstVisible, Func<Track,bool> selected)
    {
        int end=checked(offset+page.Tracks.Count);
        int count=page.Total ?? (page.More ? Math.Max(Count,end+1) : end);
        bool resized=count!=Count;
        Count=count;
        foreach(int index in loaded.Keys.Where(i=>i>=count).ToArray())loaded.Remove(index);
        foreach(var (index,track) in loaded.ToArray())
        {
            if(Math.Abs((long)index-firstVisible)<=2*SongPage.Size || index>=offset && index<end || selected(track))continue;
            loaded.Remove(index);
            ranges.Remove(index-index%SongPage.Size);
            if(!resized)NotifyReplace(index,track,new Placeholder(index));
        }
        ranges.RemoveWhere(start=>start>=count || resized && start+SongPage.Size>=count);
        for(int i=offset;i<end && i<count;i++)
        {
            var before=this[i];
            loaded[i]=page.Tracks[i-offset];
            if(!resized)NotifyReplace(i,before,loaded[i]);
        }
        ranges.Add(offset);
        if(resized)CollectionChanged?.Invoke(this,new(NotifyCollectionChangedAction.Reset));
    }

    void NotifyReplace(int index,Track before,Track after) =>
        CollectionChanged?.Invoke(this,new(NotifyCollectionChangedAction.Replace,after,before,index));

    public int IndexOf(object? value)
    {
        if(value is Placeholder p)return p.Position>=0 && p.Position<Count && !loaded.ContainsKey(p.Position)?p.Position:-1;
        foreach(var (index,t) in loaded)if(ReferenceEquals(t,value))return index;
        return -1;
    }
    public IEnumerator<Track> GetEnumerator(){for(int i=0;i<Count;i++)yield return this[i];}
    IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
    object? IList.this[int i]{get=>this[i];set=>throw new NotSupportedException();}
    public bool Contains(object? value)=>IndexOf(value)>=0;
    void ICollection.CopyTo(Array array,int index){foreach(var row in this)array.SetValue(row,index++);}
    bool IList.IsReadOnly=>true;
    bool IList.IsFixedSize=>true;
    bool ICollection.IsSynchronized=>false;
    object ICollection.SyncRoot=>this;
    int IList.Add(object? value)=>throw new NotSupportedException();
    void IList.Insert(int index,object? value)=>throw new NotSupportedException();
    void IList.Remove(object? value)=>throw new NotSupportedException();
    void IList.RemoveAt(int index)=>throw new NotSupportedException();
    void IList.Clear()=>throw new NotSupportedException();
}
