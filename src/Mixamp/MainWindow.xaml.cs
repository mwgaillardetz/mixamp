using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Mixamp;

public partial class MainWindow : Window
{
    readonly Session vm;
    readonly MenuItem themeMenu = new() { Header = "Themes", FontWeight = FontWeights.Bold };
    string theme = "";
    double normalHeight;

    public MainWindow()
    {
        InitializeComponent();
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Mixamp"
        );
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        bool smoke = args.FirstOrDefault() == "--smoke-demo" && args.Length == 2;
        bool probe = args.Length == 4 && args[0] == "--probe-media";
        bool benchmark = args.Length == 4 && args[0] == "--bench-demo";
        if (benchmark)
            dir = Path.Combine(Path.GetFullPath(args[1]), "demo-profile");
        if (smoke || probe)
            dir = Directory.CreateTempSubdirectory("mixamp-smoke-").FullName;
        if (new Uri(dir).IsUnc)
            throw new IOException("Mixamp requires a local user profile for its databases.");
        if (!benchmark && !smoke && !probe && !Directory.Exists(dir))
        {
            // Preserve the previous preview's sign-in, catalog and saved cues on upgrade.
            var previous = Path.Combine(Path.GetDirectoryName(dir)!, "CrateDeck");
            if (Directory.Exists(previous))
                Directory.Move(previous, dir);
        }
        vm = new(new Store(dir), new Playback());
        DataContext = vm;
        vm.SelectedIndex = () => Songs.SelectedIndex;
        vm.Selection = () => Songs.SelectedItems.Cast<Mixamp.Core.Track>().ToArray();
        MenuItem Item(string cmd) =>
            new()
            {
                Header = cmd == "Toggle" ? "Play / Pause" : cmd,
                InputGestureText = cmd switch {
                    "Toggle" => "Space", "Next" => "Ctrl+Right", "Previous" => "Ctrl+Left", _ => ""
                },
                Command = vm.Action,
                CommandParameter = cmd,
            };
        void Menu(string name, params string[] cmds)
        {
            var m = new MenuItem { Header = name };
            Menus.Items.Add(m);
            foreach (var c in cmds)
                m.Items.Add(Item(c));
        }
        Menu("_Library", "Connect", "Demo", "Cancel", "Sync", "Share mapping", "Local clip");
        Menu(
            "_Edit",
            "Undo",
            "Redo",
            "Rename file",
            "Song Info",
            "New playlist",
            "Add to playlist",
            "Move up",
            "Remove from playlist",
            "Organize files by artist",
            "Delete tracks from server"
        );
        var editMenu = (MenuItem)Menus.Items[1];
        editMenu.SubmenuOpened += (_, _) =>
        {
            foreach (var item in editMenu.Items.OfType<MenuItem>().Take(2))
            {
                var action = (string)item.CommandParameter;
                var name = vm.Store.HistoryName(vm.Scope, action == "Redo");
                item.Header = name == null ? action : action + " " + name;
                item.IsEnabled = name != null;
                item.InputGestureText = action == "Redo" ? "Ctrl+Y" : "Ctrl+Z";
            }
        };
        Menu(
            "_Playback",
            "Toggle",
            "Previous",
            "Next",
            "Play",
            "Play full track once",
            "Play Next",
            "Add to Queue",
            "Remove from queue",
            "Library Shuffle",
            "Radio",
            "Artist radio",
            "Internet radio"
        );
        var repeat = new MenuItem { Header = "Repeat track", IsCheckable = true };
        repeat.Click += (s, e) => vm.Repeat = repeat.IsChecked;
        ((MenuItem)Menus.Items[2]).Items.Add(repeat);
        var eqMenu = new MenuItem { Header = "Equalizer" };
        ((MenuItem)Menus.Items[2]).Items.Add(eqMenu);
        foreach (var mode in ToneProfile.Modes)
        {
            var option = new MenuItem { Header = mode, IsCheckable = true, IsChecked = vm.EqMode == mode };
            eqMenu.Items.Add(option);
            option.Click += (_, _) =>
            {
                vm.EqMode = mode;
                foreach (MenuItem entry in eqMenu.Items) entry.IsChecked = entry == option;
            };
        }
        var custom = new MenuItem { Header = "Custom…", IsChecked = vm.EqMode.StartsWith("Custom|") };
        eqMenu.Items.Add(custom);
        custom.Click += (_, _) =>
        {
            var original = vm.EqMode;
            var preset = ToneProfile.Choose(original.StartsWith("Custom|") ? original :
                vm.Store.Get("installation", "customEq") ?? "Flat", "");
            var panel = new StackPanel { Margin = new Thickness(16) };
            double[] values = [preset.Low,preset.Mid,preset.High];
            string[] names = ["Bass · 120 Hz","Midrange · 1 kHz","Treble · 6.5 kHz"];
            string Mode() => ToneProfile.Custom(values[0],values[1],values[2]);
            for (int i=0;i<values.Length;i++)
            {
                int band=i;
                var label=new TextBlock();
                void Label() => label.Text = $"{names[band]}    {values[band]:+0.0;-0.0;0} dB";
                Label();panel.Children.Add(label);
                var slider = new Slider { Minimum=-6,Maximum=6,TickFrequency=.5,IsSnapToTickEnabled=true,
                    Value=values[i],Margin=new Thickness(0,4,0,10) };
                slider.ValueChanged += (_,_)=>{values[band]=slider.Value;Label();vm.PreviewEq(Mode());};
                panel.Children.Add(slider);
            }
            var save=new Button { Content="Apply", IsDefault=true };
            panel.Children.Add(save);
            var window=Dialogs.Window("Custom equalizer",panel);
            save.Click+=(_,_)=>window.DialogResult=true;
            vm.PreviewEq(Mode());
            if(window.ShowDialog()==true){vm.EqMode=Mode();vm.Store.Set("installation","customEq",Mode());}
            else vm.PreviewEq(original);
            foreach(MenuItem entry in eqMenu.Items)
                entry.IsChecked=entry==custom ? vm.EqMode.StartsWith("Custom|") : Equals(entry.Header,vm.EqMode);
        };
        var appearance = new MenuItem { Header = "_View" };
        Menus.Items.Add(appearance);
        appearance.Items.Add(themeMenu);
        foreach (var entry in ThemeCatalog.All)
        {
            var option = new MenuItem { Header = entry.Name, FontWeight = FontWeights.Normal,
                IsCheckable = true, ToolTip = entry.Description, Padding = new Thickness(9,3,8,3) };
            themeMenu.Items.Add(option);
            option.Click += (_, _) => Apply(entry.Name);
        }
        ConfigureColumns();
        var visualizer = new MenuItem { Header = "Visualizer", IsCheckable = true,
            IsChecked = vm.Store.Get("installation", "visualizer") == "on" };
        ((MenuItem)Menus.Items[0]).Items.Add(visualizer);
        void DisplayVisualizer() => VisualizerPanel.Visibility =
            visualizer.IsChecked ? Visibility.Visible : Visibility.Collapsed;
        DisplayVisualizer();
        visualizer.Click += (_, _) =>
        {
            DisplayVisualizer();
            vm.Store.Set("installation", "visualizer", visualizer.IsChecked ? "on" : "off");
        };
        var bars = new MenuItem { Header = "Now Playing bars", IsCheckable = true,
            IsChecked = vm.Store.Get("installation", "nowBars") != "off" };
        ((MenuItem)Menus.Items[0]).Items.Add(bars);
        NowBars.Visibility = bars.IsChecked ? Visibility.Visible : Visibility.Collapsed;
        bars.Click += (_, _) =>
        {
            NowBars.Visibility = bars.IsChecked ? Visibility.Visible : Visibility.Collapsed;
            vm.Store.Set("installation", "nowBars", bars.IsChecked ? "on" : "off");
        };
        var strip = new MenuItem { Header = "Toggle player strip" };
        appearance.Items.Add(strip);
        strip.Click += (s, e) =>
        {
            if (Body.IsVisible)
            {
                normalHeight = ActualHeight;
                Body.Visibility = Visibility.Collapsed;
                MinHeight = 170;
                Height = 170;
            }
            else
            {
                MinHeight = 260;
                Height = Math.Max(400, normalHeight);
                Body.Visibility = Visibility.Visible;
            }
        };
        var dock = new MenuItem { Header = "Toggle queue panel" };
        appearance.Items.Add(dock);
        dock.Click += (s, e) =>
            DockedQueue.Visibility = DockedQueue.IsVisible
                ? Visibility.Collapsed
                : Visibility.Visible;
        foreach (var n in new[] { "Songs", "Radio", "Up Next" })
        {
            var b = new Button
            {
                Content = n,
                Style = (Style)FindResource("Navigation"),
                Tag = n switch { "Songs" => "\uE8D6", "Radio" => "\uE93C", _ => "\uE8FD" },
                FontWeight = n == vm.Section ? FontWeights.SemiBold : FontWeights.Normal,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Command = vm.Action,
                CommandParameter = n,
            };
            Sources.Children.Add(b);
        }
        if (int.TryParse(vm.Store.Get("installation", "sidebarWidth"), out int savedWidth))
            RailWidth.Width = new GridLength(Math.Clamp(savedWidth, 120, 460));
        RailSplitter.DragCompleted += (_, _) => vm.Store.Set("installation", "sidebarWidth",
            ((int)RailWidth.ActualWidth).ToString());
        PlaylistLinks.SelectionChanged += async (_, _) =>
        {
            if (PlaylistLinks.SelectedItem is ListBoxItem { Tag: string id })
                await PlaylistAction(() => vm.OpenPlaylist(id));
        };
        PlaylistFolder.ContextMenu = new ContextMenu();
        PlaylistFolder.ContextMenu.Items.Add(Item("New playlist"));
        PlaylistFolder.Expanded += async (_, _) => await PlaylistAction(vm.ReloadPlaylists);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.Section))
                foreach (Button link in Sources.Children)
                    link.FontWeight = Equals(link.Content, vm.Section) ? FontWeights.SemiBold : FontWeights.Normal;
            if (e.PropertyName == nameof(vm.Section) && vm.Section != "Playlists")
                PlaylistLinks.SelectedIndex = -1;
            if (e.PropertyName is nameof(vm.Section) or nameof(vm.Stations)) DisplayRadio();
            if (e.PropertyName == nameof(vm.Section) && vm.IsStationQueue)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                {
                    if (!vm.IsStationQueue || Songs.Items.Count == 0) return;
                    Songs.SelectedIndex = 0;
                    Songs.CurrentCell = new DataGridCellInfo(Songs.Items[0], Songs.Columns.First(c => c.Visibility == Visibility.Visible));
                    Songs.Focus();
                }));
            if (e.PropertyName == nameof(vm.Playlists))
                DisplayPlaylists();
        };
        Songs.ContextMenu = new ContextMenu();
        foreach (
            var n in new[]
            {
                "Play",
                "Add to playlist",
                "Play Next",
                "Add to Queue",
                "Song Info",
                "Play full track once",
                "Remove from queue",
                "Remove from playlist",
                "Rename file",
                "Organize files by artist",
                "Delete tracks from server",
            }
        )
            Songs.ContextMenu.Items.Add(Item(n));
        Apply(vm.Store.Get("installation", "theme"));
        try
        {
            FontSize = 12 * new Windows.UI.ViewManagement.UISettings().TextScaleFactor;
        }
        catch (Exception)
        {
            FontSize = SystemFonts.MessageFontSize;
        }
        Closed += (s, e) => vm.Dispose();
        Loaded += async (s, e) =>
        {
            try
            {
                if (benchmark)
                {
                    await Bench(args[2], args[3]);
                    return;
                }
                if (smoke)
                {
                    await Smoke(args[1]);
                    return;
                }
                if (probe)
                {
                    await Probe(args[1], args[2], args[3]);
                    return;
                }
                await vm.Cached(reconnect: true);
            }
            catch (Exception ex)
            {
                vm.Status = Session.Message(ex);
            }
        };
        SystemParameters.StaticPropertyChanged += (s, e) =>
        {
            if (e.PropertyName == "HighContrast")
                Apply(theme);
        };
    }

    void ConfigureColumns()
    {
        var menu = new ContextMenu();
        var settings = vm.Store.Get("installation", "songColumns");
        var visible = settings?.Split('|').ToHashSet();
        foreach (var column in Songs.Columns)
        {
            string key = column.SortMemberPath;
            if (visible != null)
                column.Visibility = visible.Contains(key) ? Visibility.Visible : Visibility.Collapsed;
            var toggle = new MenuItem { Header = column.Header, IsCheckable = true,
                IsChecked = column.Visibility == Visibility.Visible, StaysOpenOnClick = true };
            menu.Items.Add(toggle);
            toggle.Click += (_, _) =>
            {
                column.Visibility = toggle.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                var shown = Songs.Columns.Where(c => c.Visibility == Visibility.Visible);
                vm.Store.Set("installation", "songColumns", string.Join("|", shown.Select(c => c.SortMemberPath)));
                EnableToggles();
            };
        }
        void EnableToggles()
        {
            var items = menu.Items.Cast<MenuItem>();
            int count = items.Count(i => i.IsChecked);
            foreach (var item in items)
                item.IsEnabled = !item.IsChecked || count > 1;
        }
        EnableToggles();
        var headers = new Style(typeof(DataGridColumnHeader), (Style)FindResource(typeof(DataGridColumnHeader)));
        headers.Setters.Add(new Setter(ContextMenuProperty, menu));
        Songs.ColumnHeaderStyle = headers;
    }

    async Task PlaylistAction(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { vm.Status = Session.Message(ex); }
    }

    void DisplayPlaylists()
    {
        PlaylistLinks.Items.Clear();
        foreach (var entry in vm.Playlists)
        {
            var item = new ListBoxItem { Content = entry.Name, ToolTip = entry.Name,
                Tag = entry.Id, Padding = new Thickness(6, 3, 2, 3), ContextMenu = new ContextMenu() };
            item.ContextMenu.Items.Add(new MenuItem { Header = "New playlist",
                Command = vm.Action, CommandParameter = "New playlist" });
            var remove = new MenuItem { Header = "Remove playlist" };
            remove.Click += async (_, _) => await PlaylistAction(() => vm.DeletePlaylist(entry));
            item.ContextMenu.Items.Add(remove);
            PlaylistLinks.Items.Add(item);
        }
    }

    static SolidColorBrush Paint(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    void Apply(string? name)
    {
        var selected = ThemeCatalog.Resolve(name);
        theme = selected.Name;
        foreach (MenuItem item in themeMenu.Items) item.IsChecked = Equals(item.Header, theme);
        var p = selected.Palette;
        var dict = new ResourceDictionary
        {
            ["Canvas"] = Paint(p.Canvas), ["Side"] = Paint(p.Side), ["Paper"] = Paint(p.Paper),
            ["Stripe"] = Paint(p.Stripe), ["Ink"] = Paint(p.Ink), ["Muted"] = Paint(p.Muted),
            ["Accent"] = Paint(p.Accent), ["Tint"] = Paint(p.Tint), ["Line"] = Paint(p.Line),
            ["Deck"] = new LinearGradientBrush(Paint(p.DeckTop).Color, Paint(p.DeckBottom).Color, 90)
        };
        if (SystemParameters.HighContrast)
        {
            foreach (var k in new[] { "Deck", "Canvas", "Side", "Paper", "Stripe", "Tint" })
                dict[k] = SystemColors.WindowBrush;
            foreach (var k in new[] { "Ink", "Muted", "Line" })
                dict[k] = SystemColors.WindowTextBrush;
            dict["Accent"] = SystemColors.HighlightBrush;
        }
        Songs.Resources[SystemColors.HighlightBrushKey] = dict["Tint"];
        Application.Current.Resources["ScrollRail"] = dict["Side"];
        Application.Current.Resources["ScrollGrip"] = dict["Muted"];
        Application.Current.Resources["ScrollHighlight"] = dict["Accent"];
        Songs.Resources[SystemColors.HighlightTextBrushKey] = dict["Ink"];
        Resources.MergedDictionaries.Clear();
        Resources.MergedDictionaries.Add(dict);
        bool amp = theme == "Bitstream 98" && !SystemParameters.HighContrast;
        ClassicTitle.Visibility = amp ? Visibility.Visible : Visibility.Collapsed;
        SidebarBrand.Visibility = amp ? Visibility.Collapsed : Visibility.Visible;
        FontFamily = new FontFamily(theme == "Phosphor FM" ? "Consolas" : amp ? "Tahoma" : "Segoe UI");
        Songs.RowHeight = amp ? 22 : 26;
        Songs.SetResourceReference(ForegroundProperty, amp ? "Accent" : "Ink");
        Grid.SetColumn(PlayerScreen, amp ? 0 : 1);
        Grid.SetColumnSpan(PlayerScreen, amp ? 3 : 1);
        Grid.SetRow(TransportKeys, amp ? 1 : 0);
        Grid.SetRow(VolumePanel, amp ? 1 : 0);
        PlayerScreen.Margin = amp ? new Thickness(0,0,0,4) : new Thickness(18,0,18,0);
        if (amp && SeekBar.Parent == TrackDetails)
        {
            TrackDetails.Children.Remove(SeekBar);
            DeckLayout.Children.Add(SeekBar);
            Grid.SetRow(SeekBar, 1);
            Grid.SetColumn(SeekBar, 1);
            SeekBar.Margin = new Thickness(14,0,18,0);
        }
        else if (!amp && SeekBar.Parent == DeckLayout)
        {
            DeckLayout.Children.Remove(SeekBar);
            TrackDetails.Children.Add(SeekBar);
            SeekBar.Margin = new Thickness(0);
        }
        PlayerScreen.CornerRadius = new CornerRadius(amp ? 0 : 4);
        PlayerScreen.BorderThickness = new Thickness(amp ? 2 : 1);
        PlayerScreen.SetResourceReference(Border.BackgroundProperty, amp ? "Paper" : "Tint");
        PlayerScreen.SetResourceReference(Border.BorderBrushProperty, amp ? "Canvas" : "Line");
        PlayerDeck.Padding = amp ? new Thickness(8,4,8,6) : new Thickness(12,9,12,9);
        TrackDisplay.SetResourceReference(TextBlock.ForegroundProperty, amp ? "Accent" : "Ink");
        TrackDisplay.FontFamily = amp ? new FontFamily("Consolas") : FontFamily;
        TimeDisplay.FontFamily = amp
            ? new FontFamily(new Uri("pack://application:,,,/"), "./Fonts/#Mixamp Meter")
            : new FontFamily("Consolas");
        NowBars.Margin = amp ? new Thickness(0,0,4,0) : new Thickness(0,0,56,4);
        TimeDisplay.FontSize = amp ? 32 : 18;
        DockPanel.SetDock(TimeDisplay, amp ? Dock.Left : Dock.Right);
        TimeDisplay.Margin = amp ? new Thickness(0,0,12,0) : new Thickness(0);
        foreach (Button button in TransportKeys.Children)
        {
            button.Style = (Style)FindResource(amp ? "MetalKey" : "Key");
            var icon = (System.Windows.Shapes.Path)button.Content;
            if (amp) icon.Fill = Paint("#212331");
            else icon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, Equals(button.CommandParameter,"Toggle") ? "Accent" : "Ink");
        }
        foreach (var slider in new[] { SeekBar, VolumeBar })
            if (amp) slider.Style = (Style)FindResource("MetalFader");
            else slider.ClearValue(StyleProperty);
        vm.Store.Set("installation", "theme", theme);
    }

    void DoubleClick(object s, MouseButtonEventArgs e)
    {
        if (
            ItemsControl.ContainerFromElement(Songs, e.OriginalSource as DependencyObject)
            is DataGridRow
        )
            vm.Action.Execute("Play");
    }

    async void SortGrid(object s, DataGridSortingEventArgs e)
    {
        if (vm.Section != "Songs")
            return;
        e.Handled = true;
        try
        {
            var task = vm.Sort(e.Column.SortMemberPath);
            foreach (var col in Songs.Columns)
                col.SortDirection = col == e.Column
                    ? vm.Descending ? System.ComponentModel.ListSortDirection.Descending : System.ComponentModel.ListSortDirection.Ascending
                    : null;
            await task;
        }
        catch (Exception ex)
        {
            vm.Status = Session.Message(ex);
        }
    }

    void Keys(object s, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Menus.IsKeyboardFocusWithin) return;
        if ((key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None ||
            key == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt) &&
            (vm.IsStationQueue || vm.Section == "Radio" && radioOption != null))
        {
            e.Handled = true;
            if (vm.IsStationQueue) vm.Action.Execute("Radio"); else BackToRadio(s, e);
            return;
        }
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            var search = vm.Section == "Radio" ? RadioSearch : Search;
            search.Focus();
            search.SelectAll();
            e.Handled = true;
            return;
        }
        if (e.OriginalSource is TextBoxBase)
            return;
        string? cmd = key switch
        {
            Key.D1 when Keyboard.Modifiers == ModifierKeys.Control => "Songs",
            Key.D2 when Keyboard.Modifiers == ModifierKeys.Control => "Radio",
            Key.D3 when Keyboard.Modifiers == ModifierKeys.Control => "Up Next",
            Key.Left when Keyboard.Modifiers == ModifierKeys.Control => "Previous",
            Key.Right when Keyboard.Modifiers == ModifierKeys.Control => "Next",
            Key.MediaPreviousTrack => "Previous",
            Key.MediaNextTrack => "Next",
            Key.MediaPlayPause => "Toggle",
            Key.Z when Keyboard.Modifiers == ModifierKeys.Control => "Undo",
            Key.Y when Keyboard.Modifiers == ModifierKeys.Control => "Redo",
            Key.Z when Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) => "Redo",
            Key.I when Keyboard.Modifiers == ModifierKeys.Control => "Song Info",
            Key.Delete when Songs.IsKeyboardFocusWithin => "Delete tracks from server",
            Key.Enter when Songs.IsKeyboardFocusWithin => "Play",
            Key.Space when Keyboard.Modifiers == ModifierKeys.None => "Toggle",
            _ => null,
        };
        if (cmd != null)
        {
            e.Handled = true;
            if (!e.IsRepeat) vm.Action.Execute(cmd);
        }
    }

    void SeekRelease(object s, MouseButtonEventArgs e) => vm.Seek((long)SeekBar.Value);

    void SeekKeys(object s, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Home or Key.End)
            vm.Seek((long)SeekBar.Value);
    }
}
