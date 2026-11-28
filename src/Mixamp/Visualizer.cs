using NAudio.CoreAudioApi;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Threading;

namespace Mixamp;

public sealed class Visualizer : FrameworkElement
{
    readonly float[] peaks = new float[48];
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    MMDevice? device;
    AudioSessionControl? session;
    DateTime nextLookup;
    PlaybackSpectrum? spectrum;
    readonly float[] bands = new float[PlaybackSpectrum.BandCount];
    int head;
    public string Backdrop { get; set; } = VisualizerStyles.Off;
    public bool ShowMeter { get; set; } = true;
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
        if (!timer.IsEnabled || ActualWidth <= 0 || ActualHeight <= 0) return;
        var ink = (Brush)FindResource("Accent");
        if (BottomAligned && Backdrop != VisualizerStyles.Off && !SystemParameters.HighContrast)
        {
            dc.PushOpacity(.18);
            DrawBands(dc, ink, new Rect(RenderSize), Backdrop);
            dc.Pop();
        }
        if (!ShowMeter) return;
        double left = BottomAligned ? ActualWidth / 2 + 8 : 0;
        double height = BottomAligned ? Math.Min(26, ActualHeight) : ActualHeight;
        if (left >= ActualWidth) return;
        DrawBands(dc, ink, new Rect(left, (ActualHeight - height) / 2, ActualWidth - left, height),
            BottomAligned ? VisualizerStyles.Pixels : VisualizerStyles.Mirror);
    }

    void DrawBands(DrawingContext dc, Brush ink, Rect bounds, string style)
    {
        const int levels = 8;
        bool mirror = style == VisualizerStyles.Mirror;
        bool dots = style == VisualizerStyles.Dots;
        int count = BottomAligned ? bands.Length : peaks.Length;
        double slot = bounds.Width / count;
        double step = bounds.Height / (mirror ? levels * 2 : levels);
        for (int level = 0; level < levels; level++)
        {
            dc.PushOpacity(.54 + .42 * (level + 1) / levels);
            double y = (mirror ? bounds.Top + bounds.Height / 2 : bounds.Bottom) - (level + 1) * step;
            for (int i = 0; i < count; i++)
            {
                double amplitude = BottomAligned ? bands[i] : Math.Sqrt(peaks[(head + i) % count]);
                if (Math.Round(amplitude * levels) <= level) continue;
                double x = bounds.Left + i * slot;
                if (dots)
                {
                    double radius = Math.Min(slot, step) * .35;
                    dc.DrawEllipse(ink, null, new Point(x + slot / 2, y + step / 2), radius, radius);
                    continue;
                }
                var block = new Rect(x, y, slot * .78, step * .8);
                dc.DrawRectangle(ink, null, block);
                if (mirror)
                {
                    block.Y = bounds.Top + bounds.Bottom - block.Bottom;
                    dc.DrawRectangle(ink, null, block);
                }
            }
            dc.Pop();
        }
    }
}
