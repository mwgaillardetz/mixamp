namespace Mixamp;

public partial class MainWindow
{
    RadioOption? radioOption;
    IReadOnlyList<RadioChoice> radioChoices = [];
    readonly List<RadioChoice> mixSeeds = [];
    int radioViewVersion;
    bool IsMix => radioOption?.Kind is "artist" or "album";

    void DisplayRadio()
    {
        ++radioViewVersion;
        bool radio = vm.Section == "Radio";
        RadioPane.Visibility = radio ? Visibility.Visible : Visibility.Collapsed;
        TrackSearch.Visibility = Songs.Visibility = radio ? Visibility.Collapsed : Visibility.Visible;
        radioOption = null;
        radioChoices = [];
        mixSeeds.Clear();
        if (!radio) return;
        RadioHeading.Text = vm.RadioMessage;
        RadioHeading.Visibility = vm.RadioMessage == "" ? Visibility.Collapsed : Visibility.Visible;
        RadioBack.Visibility = RadioSearch.Visibility = RadioSeeds.Visibility = AddSeed.Visibility =
            PlayStation.Visibility = RadioChoicesList.Visibility = Visibility.Collapsed;
        StationScroll.Visibility = Visibility.Visible;
        StationList.Children.Clear();
        foreach (var option in vm.Stations)
        {
            var station = new Button { Content = option.Name, Tag = option.Kind == "station" ? "\uE768" : "\uE8FD",
                HorizontalContentAlignment = HorizontalAlignment.Left, Style = (Style)FindResource("Navigation") };
            station.Click += async (_, _) => await PlaylistAction(() => option.Kind == "station"
                ? vm.PlayRadio(option, []) : OpenRadioChoices(option));
            StationList.Children.Add(station);
        }
        if (StationList.Children.Count > 0) StationList.Children[0].Focus();
    }

    void BackToRadio(object sender, RoutedEventArgs e)
    {
        vm.CancelPendingStation();
        DisplayRadio();
    }

    async Task OpenRadioChoices(RadioOption option)
    {
        vm.CancelPendingStation();
        int version = ++radioViewVersion;
        radioOption = option;
        radioChoices = [];
        mixSeeds.Clear();
        RadioSeeds.Children.Clear();
        StationScroll.Visibility = Visibility.Collapsed;
        RadioHeading.Visibility = RadioBack.Visibility = Visibility.Visible;
        RadioHeading.Text = option.Name + " · Loading…";
        RadioChoicesList.ItemsSource = null;
        RadioSearch.Text = "";
        var choices = await vm.RadioChoices(option);
        if (version != radioViewVersion) return;
        radioChoices = choices;
        RadioHeading.Text = choices.Count == 0 ? option.Name + " · No choices in this library" : option.Name;
        RadioSearch.Visibility = RadioChoicesList.Visibility = PlayStation.Visibility = Visibility.Visible;
        AddSeed.Visibility = RadioSeeds.Visibility = IsMix ? Visibility.Visible : Visibility.Collapsed;
        PlayStation.Content = IsMix ? "Play mix" : "Play station";
        FilterRadio(this, null);
        RadioSearch.Focus();
    }

    void FilterRadio(object sender, TextChangedEventArgs? e)
    {
        if (RadioChoicesList == null || radioOption == null) return;
        var term = RadioSearch.Text.Trim();
        RadioChoicesList.ItemsSource = radioChoices.Where(x => x.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            && !mixSeeds.Contains(x)).ToList();
        UpdateRadioButtons();
    }

    void UpdateRadioButtons()
    {
        AddSeed.IsEnabled = RadioChoicesList.SelectedItem != null && mixSeeds.Count < 100;
        PlayStation.IsEnabled = IsMix ? mixSeeds.Count > 0 : RadioChoicesList.SelectedItem != null;
    }

    void RadioSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateRadioButtons();

    void AddRadioSeed(object sender, RoutedEventArgs e)
    {
        if (RadioChoicesList.SelectedItem is not RadioChoice seed || mixSeeds.Contains(seed)) return;
        mixSeeds.Add(seed);
        var remove = new Button { Content = seed.Name + "  ×", ToolTip = "Remove " + seed.Name, Style = (Style)FindResource("Key") };
        remove.Click += (_, _) =>
        {
            mixSeeds.Remove(seed);
            RadioSeeds.Children.Remove(remove);
            FilterRadio(this, null);
        };
        RadioSeeds.Children.Add(remove);
        FilterRadio(this, null);
    }

    async void PlayRadioSelection(object sender, RoutedEventArgs e)
    {
        if (radioOption == null) return;
        var ids = IsMix ? mixSeeds.Select(x => x.Id).ToList() : [((RadioChoice)RadioChoicesList.SelectedItem).Id];
        await PlaylistAction(() => vm.PlayRadio(radioOption, ids));
    }
}
