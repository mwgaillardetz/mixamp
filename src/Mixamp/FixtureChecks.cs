using System.Windows.Media;

namespace Mixamp;

public partial class MainWindow
{
    async Task Bench(string phase, string report)
    {
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var raw = new List<object>();
        try
        {
            if (phase == "seed")
            {
                await vm.Run("Demo");
                raw.Add(new { syncMs = clock.Elapsed.TotalMilliseconds });
            }
            else
            {
                await vm.Cached();
                await Dispatcher.InvokeAsync(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle
                );
                if (Songs.Items.Count == 0)
                    throw new IOException("Seed the disposable benchmark profile first.");
                Songs.SelectedIndex = 0;
                if (!Search.Focus() || Songs.SelectedItem == null)
                    throw new IOException("Library did not accept focus and selection.");
                raw.Add(new { launchMs = (DateTime.Now - process.StartTime).TotalMilliseconds });
                if (phase == "launch")
                {
                    Songs.ScrollIntoView(Songs.Items[^1]);
                    Songs.UpdateLayout();
                    var panel = FindChild<System.Windows.Controls.Primitives.DataGridRowsPresenter>(Songs);
                    if (Songs.ItemContainerGenerator.ContainerFromIndex(Songs.Items.Count - 1) == null)
                        throw new IOException("The last library track could not be displayed.");
                    raw.Add(new { tracks = Songs.Items.Count, realizedRows = panel?.Children.Count });
                }
                if (phase == "query")
                {
                    for (int i = 0; i < 120; i++)
                    {
                        string term = (i % 6) switch
                        {
                            0 => "Night Ferry " + (i + 1),
                            1 => "池田",
                            2 => "Room Tapes",
                            3 => "Session Band",
                            4 => "missing" + i,
                            _ => "Blue Lantern",
                        };
                        var visible = new TaskCompletionSource(
                            TaskCreationOptions.RunContinuationsAsynchronously
                        );
                        void RowsChanged(
                            object? sender,
                            System.ComponentModel.PropertyChangedEventArgs e
                        )
                        {
                            if (e.PropertyName == "Rows")
                                visible.TrySetResult();
                        }
                        vm.PropertyChanged += RowsChanged;
                        clock.Restart();
                        try
                        {
                            Search.Text = term;
                            await visible.Task.WaitAsync(TimeSpan.FromSeconds(10));
                            await Dispatcher.InvokeAsync(
                                () => Songs.UpdateLayout(),
                                System.Windows.Threading.DispatcherPriority.ApplicationIdle
                            );
                            raw.Add(
                                new
                                {
                                    term,
                                    visibleMs = clock.Elapsed.TotalMilliseconds,
                                    rows = Songs.Items.Count,
                                }
                            );
                        }
                        finally
                        {
                            vm.PropertyChanged -= RowsChanged;
                        }
                    }
                }
                if (phase.StartsWith("scroll-", StringComparison.Ordinal))
                {
                    Apply(phase == "scroll-amp" ? "Bitstream 98" : "Silver Halide");
                    var syncing = vm.Run("Demo");
                    while (vm.Status != "Syncing music library…" && !syncing.IsCompleted)
                        await Task.Delay(20);
                    await Task.Delay(250);
                    Songs.ApplyTemplate();
                    Songs.UpdateLayout();
                    var viewer = FindChild<ScrollViewer>(Songs)
                        ?? throw new IOException("Could not locate library scroll viewer.");
                    var frames = new List<double>();
                    double last = 0;
                    int maxRows = 0;
                    clock.Restart();
                    void Frame(object? sender, EventArgs e)
                    {
                        double elapsed = clock.Elapsed.TotalMilliseconds;
                        if (last > 0)
                            frames.Add(elapsed - last);
                        last = elapsed;
                        viewer.ScrollToVerticalOffset(
                            (elapsed / 18) % Math.Max(1, viewer.ScrollableHeight)
                        );
                        if (frames.Count % 30 == 0)
                            maxRows = Math.Max(
                                maxRows,
                                FindChild<System.Windows.Controls.Primitives.DataGridRowsPresenter>(Songs)?.Children.Count ?? 0
                            );
                    }
                    CompositionTarget.Rendering += Frame;
                    try
                    {
                        await Task.Delay(30000);
                    }
                    finally
                    {
                        CompositionTarget.Rendering -= Frame;
                    }
                    raw.Add(
                        new
                        {
                            frames,
                            maxRows,
                            stillSyncing = !syncing.IsCompleted,
                        }
                    );
                    await syncing;
                }
                if (phase == "idle")
                {
                    await Task.Delay(5000);
                    process.Refresh();
                    var cpu = process.TotalProcessorTime;
                    clock.Restart();
                    for (int i = 0; i < 60; i++)
                    {
                        await Task.Delay(1000);
                        process.Refresh();
                        raw.Add(
                            new
                            {
                                elapsedMs = clock.Elapsed.TotalMilliseconds,
                                cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                                workingSetBytes = process.WorkingSet64,
                                privateAllocatedBytes = process.PrivateMemorySize64,
                            }
                        );
                    }
                }
            }
            File.WriteAllText(
                report,
                System.Text.Json.JsonSerializer.Serialize(
                    new
                    {
                        phase,
                        os = Environment.OSVersion.VersionString,
                        cores = Environment.ProcessorCount,
                        raw,
                    }
                )
            );
            Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(report, Session.Message(ex));
            Application.Current.Shutdown(1);
        }
    }

    async Task Probe(string route, string file, string report)
    {
        // A separately launched fixture probe owns the only active media player in this process.
        using var engine = new Playback();
        using var api = new Plex(
            new Uri("http://127.0.0.1:32491"),
            "fixture-probe",
            "fixture-only"
        );
        var times = new List<long>();
        int ends = 0;
        string? error = null;
        try
        {
            if ((await api.Call("/identity")).Str("machineIdentifier") != "mixamp-demo")
                throw new IOException("Expected the loopback fixture.");
            var t = await api.Fresh("1");
            if (route is not ("http" or "local" or "direct-check"))
                throw new ArgumentException("Use http or local.");
            var completed = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            engine.Ended += () =>
            {
                ends++;
                completed.TrySetResult();
            };
            engine.Failed += m => completed.TrySetException(new IOException(m));
            var scope = new Scope("demo", "mixamp-demo", "1");
            vm.Store.SaveCue(scope, t, route == "direct-check" ? new Cue() : new Cue(42000, 248000));
            await engine.Start(
                t,
                vm.Store.ReadCue(scope, t),
                api,
                route == "local" ? new Uri(Path.GetFullPath(file)) : null,
                default
            );
            var timeout = Task.Delay(TimeSpan.FromSeconds(route == "direct-check" ? 10 : 235));
            while (!completed.Task.IsCompleted)
            {
                if (timeout.IsCompleted)
                    throw new TimeoutException("Media completion timed out.");
                times.Add(engine.Position);
                if (route == "direct-check" && engine.Playing && engine.Position >= 750) break;
                await Task.Delay(100);
            }
            if (route != "direct-check" || completed.Task.IsCompleted) await completed.Task;
            await Task.Delay(300);
            if (route != "direct-check" && ends != 1)
                throw new IOException("Unexpected completion count.");
        }
        catch (Exception ex)
        {
            error = Session.Message(ex);
        }
        File.WriteAllText(
            report,
            System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    route,
                    playbackRoute = engine.Route,
                    ends,
                    error,
                    positionSamplesMs = times,
                }
            )
        );
        Application.Current.Shutdown(error == null ? 0 : 1);
    }

    async Task Smoke(string destination)
    {
        try
        {
            Directory.CreateDirectory(destination);
            await vm.Run("Demo");
            if (Songs.Items.Count == 0)
                throw new IOException("No demo rows realized.");
            await vm.Run("Radio");
            if (!RadioPane.IsVisible || Songs.IsVisible || !StationList.Children.OfType<Button>().Any(b => Equals(b.Content, "Artist Mix Builder")))
                throw new IOException("Radio navigation did not open the station browser.");
            await OpenRadioChoices(vm.Stations.Single(s => s.Kind == "artist"));
            RadioChoicesList.SelectedIndex = 0;
            AddSeed.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!PlayStation.IsEnabled || mixSeeds.Count != 1) throw new IOException("Artist selection failed.");
            BackToRadio(this, new RoutedEventArgs());
            await vm.Run("Songs");
            Songs.UpdateLayout();
            var header = FindChild<System.Windows.Controls.Primitives.DataGridColumnHeader>(Songs);
            var picker = header?.ContextMenu ?? throw new IOException("Column picker is missing.");
            var path = Songs.Columns.Single(c => c.SortMemberPath == nameof(Track.FilePath));
            var toggle = picker.Items.Cast<MenuItem>().Single(m => Equals(m.Header, path.Header));
            toggle.IsChecked = true;
            toggle.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            if (path.Visibility != Visibility.Visible)
                throw new IOException("File path column did not appear.");
            await vm.Sort(nameof(Track.FilePath));
            if (!vm.Rows.Select(t => t.FilePath).SequenceEqual(vm.Rows.Select(t => t.FilePath)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)))
                throw new IOException("Library file path ordering failed.");
            toggle.IsChecked = false;
            toggle.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await vm.Sort(nameof(Track.Title));
            var libraryMenu = (MenuItem)Menus.Items[0];
            var visualizer = libraryMenu.Items.Cast<MenuItem>().Single(m => Equals(m.Header, "Visualizer"));
            foreach (bool enabled in new[] { true, false })
            {
                visualizer.IsChecked = enabled;
                visualizer.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                UpdateLayout();
                if (Levels.IsVisible != enabled)
                    throw new IOException("Visualizer visibility did not follow the Library menu.");
            }
            Songs.SelectedIndex = 0;
            Songs.Focus();
            await vm.Run("Add to Queue");
            var selected = Songs.SelectedItem;
            foreach (MenuItem option in themeMenu.Items)
            {
                var name = (string)option.Header;
                option.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                if (theme != name || !option.IsChecked || themeMenu.Items.OfType<MenuItem>().Count(m => m.IsChecked) != 1 ||
                    vm.Store.Get("installation", "theme") != name)
                    throw new IOException("Theme menu failed to apply and save " + name);
                UpdateLayout();
                await Dispatcher.InvokeAsync(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle
                );
                if (!ReferenceEquals(selected, Songs.SelectedItem))
                    throw new IOException("Theme lost the selected track.");
                var visual = (FrameworkElement)Content;
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)visual.ActualWidth,
                    (int)visual.ActualHeight,
                    96,
                    96,
                    PixelFormats.Pbgra32
                );
                bitmap.Render(visual);
                var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(destination, name + ".png"));
                png.Save(stream);
            }
            var selectedTrack = (Mixamp.Core.Track)selected;
            await ExerciseEditor(selectedTrack);
            var scroll = VisualChildren<System.Windows.Controls.Primitives.ScrollBar>(Songs)
                .First(bar => bar.IsVisible && bar.Orientation == Orientation.Vertical);
            if (scroll.Template.FindName("grip",scroll) is not System.Windows.Controls.Primitives.Thumb)
                throw new IOException("The grid did not use the theme scrollbar.");
            await vm.Run("Radio");
            await vm.PlayRadio(vm.Stations.First(s => s.Kind == "station"), []);
            UpdateLayout();
            if (!QueueBack.IsVisible || !Songs.IsVisible || Songs.Items.Count == 0 || !ReferenceEquals(vm.Rows,vm.Queue))
                throw new IOException("Starting a station did not open the live queue.");
            await vm.Run("Radio");
            if (!RadioPane.IsVisible || QueueBack.IsVisible || vm.Queue.Count == 0)
                throw new IOException("Returning to stations lost the queue.");
            Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(destination, "failure.txt"), Session.Message(ex));
            Application.Current.Shutdown(1);
        }
    }

    static T? FindChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;
            if (FindChild<T>(child) is { } nested)
                return nested;
        }
        return null;
    }

    async Task ExerciseEditor(Mixamp.Core.Track track)
    {
        Exception? failure = null;
        var dispatch = Dispatcher.InvokeAsync(
            async () =>
            {
                var editor = Application.Current.Windows.OfType<Editor>().Single();
                try
                {
                    var tabs = FindChild<TabControl>(editor)!;
                    tabs.SelectedIndex = 1;
                    editor.UpdateLayout();
                    foreach (var control in VisualChildren<CheckBox>(editor))
                        control.IsChecked = true;
                    foreach (var box in VisualChildren<TextBox>(editor))
                        box.Text =
                            System.Windows.Automation.AutomationProperties.GetName(box)
                            == "Start time"
                                ? "0:42"
                                : "4:08";
                    var save = VisualChildren<Button>(editor)
                        .Single(b => b.Content as string == "Save changes");
                    save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (vm.Store.ReadCue(vm.Scope, track) != new Cue(42000, 248000))
                        throw new IOException("Song Info did not save the range.");
                    tabs.SelectedIndex = 0;
                    editor.UpdateLayout();
                    var title = VisualChildren<TextBox>(editor)
                        .Single(b =>
                            System.Windows.Automation.AutomationProperties.GetName(b) == "Title"
                        );
                    title.Text = "Demo · verified title";
                    save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(70));
                    while (!save.IsEnabled)
                        await Task.Delay(50, timeout.Token);
                    if ((await vm.Api!.Fresh(track.Id)).Title != title.Text)
                        throw new IOException("Song Info did not verify the new title.");
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    editor.Close();
                }
            },
            System.Windows.Threading.DispatcherPriority.ApplicationIdle
        );
        await vm.Run("Song Info");
        await (await dispatch.Task);
        if (failure != null)
            throw failure;
    }

    static IEnumerable<T> VisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
                yield return typed;
            foreach (var descendant in VisualChildren<T>(child))
                yield return descendant;
        }
    }
}
