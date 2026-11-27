using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace CrateDeck;

public partial class MainWindow : Window
{
    readonly Session vm;
    static readonly string[] Themes =
    [
        "Silver Library",
        "Midnight Amp",
        "Fluent Light",
        "Studio Dark",
        "Terminal Green",
    ];
    string theme = "";
    double normalHeight;

    public MainWindow()
    {
        InitializeComponent();
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CrateDeck"
        );
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        bool smoke = args.FirstOrDefault() == "--smoke-demo" && args.Length == 2;
        bool probe = args.Length == 4 && args[0] == "--probe-media";
        bool benchmark = args.Length == 4 && args[0] == "--bench-demo";
        if (benchmark)
            dir = Path.Combine(Path.GetFullPath(args[1]), "demo-profile");
        if (smoke || probe)
            dir = Directory.CreateTempSubdirectory("cratedeck-smoke-").FullName;
        if (new Uri(dir).IsUnc)
            throw new IOException("CrateDeck requires a local user profile for its databases.");
        vm = new(new Store(dir), new Playback());
        DataContext = vm;
        vm.SelectedIndex = () => Songs.SelectedIndex;
        vm.Selection = () => Songs.SelectedItems.Cast<CrateDeck.Core.Track>().ToArray();
        MenuItem Item(string cmd) =>
            new()
            {
                Header = cmd,
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
            "Song Info",
            "New playlist",
            "Add to playlist",
            "Move up",
            "Remove from playlist",
            "Delete file from server"
        );
        Menu(
            "_Playback",
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
        var appearance = new MenuItem { Header = "_View" };
        Menus.Items.Add(appearance);
        foreach (var name in Themes)
        {
            var m = new MenuItem { Header = name };
            appearance.Items.Add(m);
            m.Click += (s, e) => Apply(name);
        }
        var columns = new MenuItem
        {
            Header = "Genre / Artist / Album browser",
            IsCheckable = true,
        };
        appearance.Items.Add(columns);
        columns.Click += async (s, e) =>
        {
            Browser.Visibility = columns.IsChecked ? Visibility.Visible : Visibility.Collapsed;
            if (columns.IsChecked)
                await Browse(0);
            else
                await vm.Filter("", "", "");
        };
        Genres.SelectionChanged += async (s, e) => await Browse(1);
        Artists.SelectionChanged += async (s, e) => await Browse(2);
        Albums.SelectionChanged += async (s, e) => await Browse(3);
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
        foreach (var n in new[] { "Songs", "Albums", "Artists", "Playlists", "Radio", "Up Next" })
        {
            var b = new Button
            {
                Content = n,
                Style = (Style)FindResource("Key"),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Command = vm.Action,
                CommandParameter = n,
            };
            Sources.Children.Add(b);
        }
        Songs.ContextMenu = new ContextMenu();
        foreach (
            var n in new[]
            {
                "Play",
                "Play Next",
                "Add to Queue",
                "Song Info",
                "Play full track once",
                "Remove from queue",
                "Remove from playlist",
                "Delete file from server",
            }
        )
            Songs.ContextMenu.Items.Add(Item(n));
        Apply(vm.Store.Get("installation", "theme") ?? Themes[0]);
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
                await vm.Cached();
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

    bool filtering;

    async Task Browse(int first)
    {
        if (filtering)
            return;
        filtering = true;
        try
        {
            string Value(ComboBox box) => box.SelectedIndex > 0 ? (string)box.SelectedItem : "";
            ComboBox[] boxes = [Genres, Artists, Albums];
            string[] labels = ["All genres", "All artists", "All albums"];
            for (int i = first; i < 3; i++)
            {
                string g = i > 0 ? Value(Genres) : "",
                    a = i > 1 ? Value(Artists) : "";
                var values = await Task.Run(() => vm.Store.Facets(vm.Scope, i, g, a));
                boxes[i].ItemsSource = values.Prepend(labels[i]);
                boxes[i].SelectedIndex = 0;
            }
            await vm.Filter(Value(Genres), Value(Artists), Value(Albums));
        }
        catch (Exception ex)
        {
            vm.Status = Session.Message(ex);
        }
        finally
        {
            filtering = false;
        }
    }

    void Apply(string name)
    {
        theme = Themes.Contains(name) ? name : Themes[0];
        var dict = new ResourceDictionary
        {
            Source = new Uri("Themes/" + theme.Replace(" ", "") + ".xaml", UriKind.Relative),
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
        Songs.Resources[SystemColors.HighlightTextBrushKey] = dict["Ink"];
        Resources.MergedDictionaries.Clear();
        Resources.MergedDictionaries.Add(dict);
        bool amp = theme == "Midnight Amp";
        RailWidth.Width = new GridLength(amp ? 0 : 166);
        Rail.Visibility = amp ? Visibility.Collapsed : Visibility.Visible;
        Inspector.Visibility = theme == "Studio Dark" ? Visibility.Visible : Visibility.Collapsed;
        DockedQueue.Visibility = amp ? Visibility.Visible : Visibility.Collapsed;
        Songs.RowHeight =
            theme == "Fluent Light" ? 34
            : amp ? 22
            : 26;
        FontFamily = new FontFamily(
            theme is "Terminal Green" or "Midnight Amp" ? "Consolas" : "Segoe UI"
        );
        vm.Store.Set("installation", "theme", theme);
        if (amp && !Menus.Items.OfType<MenuItem>().Any(m => m.Header as string == "_Browse"))
        {
            var browse = new MenuItem { Header = "_Browse" };
            foreach (Button b in Sources.Children)
                browse.Items.Add(
                    new MenuItem
                    {
                        Header = b.Content,
                        Command = b.Command,
                        CommandParameter = b.CommandParameter,
                    }
                );
            Menus.Items.Add(browse);
        }
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
            await vm.Sort(e.Column.SortMemberPath);
        }
        catch (Exception ex)
        {
            vm.Status = Session.Message(ex);
        }
    }

    void Keys(object s, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Search.Focus();
            Search.SelectAll();
            e.Handled = true;
            return;
        }
        if (e.OriginalSource is TextBoxBase)
            return;
        string? cmd = e.Key switch
        {
            Key.I when Keyboard.Modifiers == ModifierKeys.Control => "Song Info",
            Key.Delete when Songs.IsKeyboardFocusWithin => "Delete file from server",
            Key.Enter when Songs.IsKeyboardFocusWithin => "Play",
            Key.Space when Songs.IsKeyboardFocusWithin => "Toggle",
            _ => null,
        };
        if (cmd != null)
        {
            e.Handled = true;
            vm.Action.Execute(cmd);
        }
    }

    void SeekRelease(object s, MouseButtonEventArgs e) => vm.Seek((long)SeekBar.Value);

    void SeekKeys(object s, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Home or Key.End)
            vm.Seek((long)SeekBar.Value);
    }
}
