using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace Mixamp;

// The Windows process-loopback endpoint limits analysis to this player.
sealed class PlaybackSpectrum : IDisposable
{
    public const int BandCount = 24;
    const int Size = 2048, Rate = 44100;
    readonly CancellationTokenSource stop = new();
    readonly Complex[] fft = new Complex[Size];
    readonly float[] window = Enumerable.Range(0, Size)
        .Select(i => (float)FastFourierTransform.HannWindow(i, Size)).ToArray();
    readonly float[] bands = new float[BandCount];
    int used;

    public PlaybackSpectrum() => _ = Task.Run(Capture);

    async Task Capture()
    {
        try
        {
            await using var capture = await new WasapiRecorderBuilder()
                .WithProcessLoopback((uint)Environment.ProcessId, ProcessLoopbackMode.IncludeTargetProcessTree)
                .WithFormat(new WaveFormat(Rate, 16, 2)).BuildAsync();
            stop.Token.ThrowIfCancellationRequested();
            await foreach (var packet in capture.CaptureAsync(stop.Token))
            {
                var samples = MemoryMarshal.Cast<byte, short>(packet.Data.Span);
                for (int i = 0; i + 1 < samples.Length; i += 2)
                    Add((samples[i] + samples[i + 1]) / 65536f);
            }
        }
        catch (Exception ex) when (ex is COMException or OperationCanceledException or InvalidOperationException)
        { /* Output can disappear when the audio device changes. */ }
    }

    void Add(float sample)
    {
        fft[used].X = sample * window[used];
        fft[used++].Y = 0;
        if (used < Size) return;
        used = 0;
        FastFourierTransform.FFT(true, 11, fft);
        lock (bands)
        {
            int start = 1;
            for (int band = 0; band < bands.Length; band++)
            {
                int end = Math.Max(start + 1, (int)(40 * Math.Pow(450, (band + 1.0) / bands.Length) * Size / Rate));
                double power = 0;
                for (int k = start; k < end; k++)
                    power = Math.Max(power, fft[k].X * fft[k].X + fft[k].Y * fft[k].Y);
                float level = (float)Math.Clamp((10 * Math.Log10(power + 1e-12) + 78) / 72, 0, 1);
                bands[band] = Math.Max(level, bands[band] * .80f);
                start = end;
            }
        }
    }
    public void Read(float[] destination) { lock (bands) bands.CopyTo(destination, 0); }
    public void Dispose() => stop.Cancel();
}
