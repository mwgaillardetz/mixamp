using System.Text.Json.Nodes;

namespace Mixamp;

sealed class Editor : Window
{
    readonly Session vm;
    readonly Track[] tracks;
    readonly ComboBox target = new()
    {
        ItemsSource = new[] { "Plex library", "Audio files", "Both" },
        SelectedIndex = 0,
    };
    readonly List<(string Tag, string? Plex, ComboBox Mode, TextBox Value)> fields = [];
    readonly CheckBox start = new() { Content = "Start" },
        stop = new() { Content = "Stop" };
    readonly TextBox from = new(),
        until = new();
    readonly TextBlock length = new(),
        result = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 8) };
    readonly TabControl tabs = new();
    readonly Button save = new() { Content = "Save changes", MinWidth = 110 };
    readonly HashSet<string> completed = [];
    bool saving;

    public Editor(Session vm, Track[] tracks)
    {
        System.Windows.Automation.AutomationProperties.SetName(from, "Start time");
        System.Windows.Automation.AutomationProperties.SetName(until, "Stop time");
        System.Windows.Automation.AutomationProperties.SetName(target, "Save target");
        this.vm = vm;
        this.tracks = tracks;
        Title = "Song Info";
        Width = 610;
        Height = 610;
        MinWidth = 420;
        MinHeight = 410;
        Owner = Application.Current.MainWindow;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(16) };
        Content = root;
        var header = new TextBlock
        {
            Text = tracks.Length == 1 ? tracks[0].Title : $"{tracks.Length} selected tracks",
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var footer = new StackPanel();
        footer.Children.Add(result);
        footer.Children.Add(save);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(tabs);
        var details = Page("Details");
        details.Children.Add(new Label { Content = "Save target", Target = target });
        details.Children.Add(target);
        details.Children.Add(
            new TextBlock
            {
                Text =
                    "Plex track edits retain field locks. An album artist change applies to the whole album.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 8),
            }
        );
        Field(details, "Title", "TITLE", "title", t => t.Title);
        Field(details, "Track artist", "ARTIST", "originalTitle", t => t.ArtistDisplay);
        Field(details, "Album artist", "ALBUMARTIST", "albumRelation", t => t.Artist);
        Field(details, "Album", "ALBUM", null, t => t.Album);
        Field(details, "Disc number", "DISCNUMBER", null, t => t.Disc.ToString());
        Field(details, "Track number", "TRACKNUMBER", "index", t => t.Number.ToString());
        Field(details, "Year", "DATE", null, t => t.Year.ToString());
        Field(details, "Genre", "GENRE", null, t => t.Genre);
        var playback = Page("Playback");
        playback.Children.Add(
            new TextBlock
            {
                Text = "Applies in Mixamp",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 6, 0, 8),
            }
        );
        var t = tracks[0];
        var cue = vm.Store.ReadCue(vm.Scope, t);
        start.IsChecked = cue.Start.HasValue;
        stop.IsChecked = cue.End.HasValue;
        SetTime(from, cue.Start ?? 0);
        SetTime(until, cue.End ?? t.Duration);
        foreach (var (check, box) in new[] { (start, from), (stop, until) })
        {
            playback.Children.Add(check);
            playback.Children.Add(box);
            var now = new Button
            {
                Content = "Use current position",
                IsEnabled = tracks.Length == 1 && vm.Current?.Id == t.Id,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            now.Click += (s, e) =>
            {
                check.IsChecked = true;
                SetTime(box, vm.Position);
                Describe();
            };
            playback.Children.Add(now);
            box.TextChanged += (s, e) => { box.Tag = null; Describe(); };
            check.Checked += (s, e) => Describe();
            check.Unchecked += (s, e) => Describe();
        }
        playback.Children.Add(length);
        Describe();
        var preview = new Button { Content = "Preview interval", IsEnabled = tracks.Length == 1 };
        playback.Children.Add(preview);
        preview.Click += async (s, e) =>
        {
            try
            {
                var c = Read();
                c.Validate(t.Duration);
                await vm.Preview(t, c);
            }
            catch (Exception ex)
            {
                result.Text = Session.Message(ex);
            }
        };
        var reset = new Button { Content = "Reset to full track" };
        playback.Children.Add(reset);
        reset.Click += (s, e) =>
        {
            start.IsChecked = stop.IsChecked = false;
        };
        if (tracks.Length > 1)
        {
            playback.IsEnabled = false;
            playback.ToolTip = "Select one track to edit its playback range.";
        }
        var files = Page("File");
        files.Children.Add(
            new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                Text = string.Join(
                    "\n\n",
                    tracks.Select(x =>
                        x.Title
                        + "\n"
                        + x.Format
                        + "  "
                        + x.Bitrate
                        + " kbps\n"
                        + string.Join(
                            "\n",
                            x.Parts.Select(p => p.File + "   (" + p.Size + " bytes)")
                        )
                    )
                ),
            }
        );
        var inspect = new Button
        {
            Content = "Read mapped file tags",
            IsEnabled = tracks.Length == 1 && vm.Store.Get(vm.Scope.Key, "mappings") != null,
        };
        var tags = new TextBlock { TextWrapping = TextWrapping.Wrap };
        files.Children.Add(inspect);
        files.Children.Add(tags);
        inspect.Click += async (s, e) =>
        {
            try
            {
                tags.Text = await Task.Run(() =>
                {
                    if (t.Parts.Length != 1)
                        throw new IOException("Choose a single-part track to inspect tags.");
                    var maps = System.Text.Json.JsonSerializer.Deserialize<Mapping[]>(
                        vm.Store.Get(vm.Scope.Key, "mappings")!
                    )!;
                    var path = MappedFiles.Resolve(t.Parts[0].File, maps);
                    using var media = TagLib.File.Create(path);
                    var tag = media.Tag;
                    return $"Title   {tag.Title}\nTrack artists   {string.Join(", ", tag.Performers)}\nAlbum artists   {string.Join(", ", tag.AlbumArtists)}\nAlbum   {tag.Album}\nDisc   {tag.Disc}/{tag.DiscCount}     Track   {tag.Track}/{tag.TrackCount}\nYear   {tag.Year}     Genre   {string.Join(", ", tag.Genres)}";
                });
            }
            catch (Exception ex)
            {
                tags.Text = Session.Message(ex);
            }
        };
        target.SelectionChanged += (s, e) => Capabilities();
        tabs.SelectionChanged += (s, e) =>
        {
            if (e.Source == tabs)
                save.IsEnabled =
                    tabs.SelectedIndex == 0 || tabs.SelectedIndex == 1 && tracks.Length == 1;
        };
        Capabilities();
        save.Click += async (s, e) => await Save();
        Closing += (s, e) => e.Cancel = saving;
    }

    StackPanel Page(string name)
    {
        var box = new StackPanel { Margin = new Thickness(12) };
        tabs.Items.Add(
            new TabItem
            {
                Header = name,
                Content = new ScrollViewer
                {
                    Content = box,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                },
            }
        );
        return box;
    }

    void Field(StackPanel page, string label, string tag, string? plex, Func<Track, string> read)
    {
        var values = tracks.Select(read).Distinct().ToArray();
        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        var mode = new ComboBox
        {
            ItemsSource = new[] { "Leave as is", "Set", "Clear" },
            SelectedIndex = 0,
            Width = 95,
        };
        var box = new TextBox
        {
            Text = values.Length == 1 ? values[0] : "",
            ToolTip = values.Length > 1 ? "Mixed values" : label,
        };
        System.Windows.Automation.AutomationProperties.SetName(box, label);
        System.Windows.Automation.AutomationProperties.SetName(mode, label + " change");
        var caption = new Label
        {
            Content = label,
            Target = box,
            Width = 105,
        };
        if (values.Length > 1)
            caption.ToolTip = "Mixed values";
        row.Children.Add(caption);
        row.Children.Add(mode);
        row.Children.Add(box);
        page.Children.Add(row);
        box.TextChanged += (s, e) =>
        {
            if (box.IsEnabled)
                mode.SelectedIndex = 1;
        };
        fields.Add((tag, plex, mode, box));
        if (values.Length > 1)
            box.Text = "— Mixed —";
        mode.SelectedIndex = 0;
    }

    void Capabilities()
    {
        bool file =
            tracks.All(t => t.Parts.Length == 1 && TagEditor.Supported(t.Parts[0].File))
            && vm.Store.Get(vm.Scope.Key, "mappings") != null;
        foreach (var f in fields)
        {
            bool allowed =
                (target.SelectedIndex == 1 || vm.CanWrite && f.Plex != null)
                && (target.SelectedIndex == 0 || file);
            f.Mode.IsEnabled = f.Value.IsEnabled = allowed;
            if (!allowed)
            {
                f.Mode.SelectedIndex = 0;
                f.Mode.ToolTip =
                    "Requires a supported Plex field and permission, or a mapped single-part MP3, FLAC or M4A for file writes.";
            }
        }
    }

    static void SetTime(TextBox box, long milliseconds)
    {
        box.Text = Cue.Format(milliseconds);
        box.Tag = milliseconds; // Retain the exact cue until the user edits its text.
    }

    static long ReadTime(TextBox box) => box.Tag is long exact ? exact : Cue.Parse(box.Text);

    Cue Read() =>
        new(
            start.IsChecked == true ? ReadTime(from) : null,
            stop.IsChecked == true ? ReadTime(until) : null
        );

    void Describe()
    {
        try
        {
            var c = Read();
            c.Validate(tracks[0].Duration);
            length.Text =
                $"Original {tracks[0].Length}     Playback {Cue.Format((c.End ?? tracks[0].Duration) - (c.Start ?? 0))}";
        }
        catch (Exception)
        {
            length.Text = "Enter a valid playback interval.";
        }
    }

    async Task AlbumArtist(string name)
    {
        await new Library(vm.Api!, vm.Scope).AlbumArtist(
            tracks,
            name,
            target.SelectedIndex == 2,
            children =>
            {
                string preview = string.Join(
                    "\n",
                    children.Select(c => $"{c.Disc}.{c.Number}   {c.Title}   ·   {c.ArtistDisplay}")
                );
                return MessageBox.Show(
                        $"Reassign {children[0].Album} ({children.Count} tracks) to {name}? Inherited performers will be saved as track artist overrides.\n\n{preview}",
                        "Review whole album",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question,
                        MessageBoxResult.No
                    ) == MessageBoxResult.Yes;
            }
        );
    }

    async Task Save()
    {
        if (saving)
            return;
        saving = true;
        save.IsEnabled = false;
        tabs.IsEnabled = false;
        string batch = Guid.NewGuid().ToString();
        List<string> outcomes = [];
        try
        {
            if (tabs.SelectedIndex == 1)
            {
                var cue = Read();
                var track = tracks.Single();
                cue.Validate(track.Duration);
                var before = vm.Store.ReadCue(vm.Scope, track);
                if (cue != before) vm.Store.Remember(vm.Scope, batch, "Edit playback range",
                    new(track, "Cue", "", "", System.Text.Json.JsonSerializer.Serialize(before), System.Text.Json.JsonSerializer.Serialize(cue)));
                vm.Store.SaveCue(vm.Scope, track, cue);
                result.Text = "Playback range saved. Applies in Mixamp.";
                return;
            }
            var edits = fields.Where(f => f.Mode.SelectedIndex > 0).ToList();
            if (edits.Count == 0)
            {
                result.Text = "No fields changed.";
                return;
            }
            string Value((string Tag, string? Plex, ComboBox Mode, TextBox Value) f) =>
                f.Mode.SelectedIndex == 2 ? "" : f.Value.Text;
            if (
                edits.Any(f =>
                    f.Mode.SelectedIndex == 1 && (Value(f) == "" || Value(f) == "— Mixed —")
                )
            )
                throw new IOException("Enter a value or choose Clear.");
            if (
                edits.Any(f =>
                    f.Mode.SelectedIndex == 1
                    && f.Plex == "index"
                    && (!int.TryParse(Value(f), out var n) || n < 0)
                )
            )
                throw new IOException("Enter a nonnegative track number.");
            if (target.SelectedIndex != 1 && edits.Any(f => f.Plex == "albumRelation"))
            {
                await AlbumArtist(Value(edits.Single(f => f.Plex == "albumRelation")));
                outcomes.Add("Plex album grouping verified.");
            }
            foreach (var t in tracks)
            {
                var files = edits.ToDictionary(f => f.Tag, Value);
                string key = t.Id + System.Text.Json.JsonSerializer.Serialize(files);
                if (target.SelectedIndex != 0 && !completed.Contains("f" + key))
                {
                    var writer = new FileWriter(vm.Store, vm.Scope, vm.Api!);
                    outcomes.Add(await Task.Run(() => writer.Write(t, files, batch)));
                    completed.Add("f" + key);
                }
                if (target.SelectedIndex != 1 && !completed.Contains("p" + key))
                {
                    var plex = edits
                        .Where(f => f.Plex is not null and not "albumRelation")
                        .ToDictionary(f => f.Plex!, Value);
                    if (plex.Count > 0)
                    {
                        var before = await vm.Api!.Fresh(t.Id);
                        var previous = plex.Keys.ToDictionary(k => k, k => Session.FieldValue(before, k));
                        vm.Store.Remember(vm.Scope, batch, "Edit song information", new(t, "Plex", "", "",
                            System.Text.Json.JsonSerializer.Serialize(previous), System.Text.Json.JsonSerializer.Serialize(plex)));
                        await vm.Api.SetFields(vm.Scope.Library, t, plex);
                    }
                    completed.Add("p" + key);
                    outcomes.Add("Verified Plex fields for " + t.Title + ".");
                }
                if (target.SelectedIndex != 0 && !completed.Contains("r" + key))
                {
                    await vm.Api!.Call(
                        "/library/metadata/" + Plex.E(t.Id) + "/refresh",
                        HttpMethod.Put
                    );
                    var refreshed = await vm.Api.Fresh(t.Id);
                    completed.Add("r" + key);
                    outcomes.Add(
                        "Refresh requested. Plex currently shows "
                            + refreshed.Title
                            + " / "
                            + refreshed.Artist
                            + "."
                    );
                }
            }
            if (target.SelectedIndex != 1)
                await vm.Run("Sync");
            result.Text = string.Join("\n", outcomes);
        }
        catch (OperationCanceledException)
        {
            result.Text = string.Join("\n", outcomes.Append("Save cancelled."));
        }
        catch (Exception ex)
        {
            result.Text = string.Join("\n", outcomes.Append(Session.Message(ex)));
            save.Content = "Retry unfinished changes";
        }
        finally
        {
            saving = false;
            save.IsEnabled = true;
            tabs.IsEnabled = true;
        }
    }
}
