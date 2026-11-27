using NAudio.CoreAudioApi;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Threading;

namespace Mixamp;

public sealed class Visualizer : FrameworkElement
{
    readonly float[] peaks = new float[96];
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    MMDevice? device;
    AudioSessionControl? session;
    DateTime nextLookup;
    PlaybackSpectrum? spectrum;
    readonly float[] bands = new float[32];
    int head;
    public bool BottomAligned { get; set; }

    public Visualizer()
    {
        timer.Tick += (_, _) => Sample();
        IsVisibleChanged += (_, _) => UpdateActivity();
        DataContextChanged += (_, args) =>
        {
            if (args.OldValue is Session previous) previous.PropertyChanged -= PlaybackChanged;
            if (args.NewValue is Session current) current.PropertyChanged += PlaybackChanged;
            UpdateActivity();
        };
    }

    void PlaybackChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Session.Playing)) UpdateActivity();
    }

    void UpdateActivity()
    {
        if (IsVisible && DataContext is Session { Playing: true })
        {
            if (BottomAligned) spectrum ??= new PlaybackSpectrum();
            timer.Start();
        }
        else
        {
            timer.Stop();
            spectrum?.Dispose();
            spectrum = null;
            Array.Clear(bands);
            Release();
            Array.Clear(peaks);
            nextLookup = DateTime.MinValue;
            InvalidateVisual();
        }
    }

    void Release()
    {
        session?.Dispose();
        session = null;
        device?.Dispose();
        device = null;
    }

    void Sample()
    {
        if (DataContext is not Session { Playing: true })
        {
            if (device != null) Release();
            return;
        }
        if (spectrum != null)
        {
            spectrum.Read(bands);
            InvalidateVisual();
            return;
        }
        float level = 0;
        try
        {
            if (session == null && DateTime.UtcNow >= nextLookup)
            {
                nextLookup = DateTime.UtcNow.AddSeconds(2);
                Release();
                using var devices = new MMDeviceEnumerator();
                device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var candidate = sessions[i];
                    if (candidate.GetProcessID == Environment.ProcessId &&
                        candidate.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive)
                    {
                        session = candidate;
                        break;
                    }
                    candidate.Dispose();
                }
            }
            if (session != null)
            {
                if (session.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive)
                    level = session.AudioMeterInformation.MasterPeakValue;
                else Release();
            }
        }
        catch (COMException) { Release(); }
        peaks[head] = Math.Clamp(level, 0, 1);
        head = (head + 1) % peaks.Length;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (BottomAligned && !timer.IsEnabled) return;
        var ink = (Brush)FindResource("Accent");
        int count = BottomAligned ? bands.Length : peaks.Length;
        double step = ActualWidth / count;
        for (int i = 0; i < count; i++)
        {
            if (BottomAligned)
            {
                double bar = ActualHeight * bands[i];
                dc.DrawRectangle(ink, null, new Rect(i * step, ActualHeight - bar, Math.Min(1.5, step), bar));
                continue;
            }
            double height = Math.Max(2, ActualHeight * Math.Sqrt(peaks[(head + i) % peaks.Length]));
            dc.DrawRoundedRectangle(ink, null, new Rect(i * step, (ActualHeight - height) / 2,
                Math.Max(1, step - 2), height), 2, 2);
        }
    }
}
