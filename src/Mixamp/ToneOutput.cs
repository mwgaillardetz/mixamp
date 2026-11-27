using Windows.Media.Audio;
using Windows.Media.Core;
using Windows.Media.Render;

namespace Mixamp;

sealed class ToneOutput : IDisposable
{
    readonly AudioGraph graph;
    readonly MediaSourceAudioInputNode source;
    readonly IAudioNode output;
    readonly EqualizerEffectDefinition eq;
    ToneProfile? current;
    public bool Playing { get; private set; }
    public long Position => (long)source.Position.TotalMilliseconds;
    double volume = 1;
    public double Volume { set { volume = value; output.OutgoingGain = value * (current?.Headroom ?? 1); } }
    public event Action? Ended;
    public event Action? Failed;

    ToneOutput(AudioGraph graph, MediaSourceAudioInputNode source, IAudioNode output)
    {
        this.graph = graph;
        this.source = source;
        this.output = output;
        eq = new EqualizerEffectDefinition(graph);
        double[] centers = [120, 1000, 6500, 12000];
        for (int i = 0; i < centers.Length; i++)
        {
            eq.Bands[i].FrequencyCenter = centers[i];
            eq.Bands[i].Bandwidth = 1.5;
        }
        source.EffectDefinitions.Add(eq);
        source.AddOutgoingConnection(output);
        source.MediaSourceCompleted += (_, _) => Ended?.Invoke();
        graph.UnrecoverableErrorOccurred += (_, _) => Failed?.Invoke();
    }

    public static async Task<ToneOutput> Open(MediaSource media, Cue cue, CancellationToken token)
    {
        var result = await AudioGraph.CreateAsync(new AudioGraphSettings(AudioRenderCategory.Media)).AsTask(token);
        var graph = result.Graph;
        if (graph == null) { media.Dispose(); throw new IOException("Audio processing unavailable."); }
        try
        {
            var output = (await graph.CreateDeviceOutputNodeAsync().AsTask(token)).DeviceOutputNode
                ?? throw new IOException("Audio output unavailable.");
            var node = (await graph.CreateMediaSourceAudioInputNodeAsync(media).AsTask(token)).Node
                ?? throw new IOException("Decoder unavailable for EQ.");
            if (cue.Start.HasValue) node.StartTime = TimeSpan.FromMilliseconds(cue.Start.Value);
            if (cue.End.HasValue) node.EndTime = TimeSpan.FromMilliseconds(cue.End.Value);
            return new(graph, node, output);
        }
        catch { graph.Dispose(); media.Dispose(); throw; }
    }

    public void Apply(ToneProfile profile)
    {
        if (profile == current) return;
        current = profile;
        output.OutgoingGain = volume * profile.Headroom;
        if (profile == ToneProfile.Flat) { source.DisableEffectsByDefinition(eq); return; }
        source.EnableEffectsByDefinition(eq);
        eq.Bands[0].Gain = Math.Pow(10, profile.Low / 20);
        eq.Bands[1].Gain = Math.Pow(10, profile.Mid / 20);
        eq.Bands[2].Gain = Math.Pow(10, profile.High / 20);
    }
    public void Seek(long ms) => source.Seek(TimeSpan.FromMilliseconds(ms));
    public void Toggle()
    {
        if (Playing) graph.Stop(); else graph.Start();
        Playing = !Playing;
    }
    public void Dispose() { graph.Stop(); graph.Dispose(); }
}
