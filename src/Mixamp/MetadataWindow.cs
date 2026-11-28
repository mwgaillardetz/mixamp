namespace Mixamp;

static class MetadataWindow
{
    public static bool ConfirmSelection(int count) => count == 1 || (count > 1 &&
        MessageBox.Show(Application.Current.MainWindow, $"Any field you change will apply to all {count:N0} tracks.",
            $"Edit {count:N0} tracks?", MessageBoxButton.OKCancel, MessageBoxImage.Question,
            MessageBoxResult.Cancel) == MessageBoxResult.OK);

    public static Dictionary<string, string>? Edit(TrackMetadata metadata, Session vm)
    {
        var form = new StackPanel();
        var panel = new DockPanel { Margin = new Thickness(14) };
        var apply = new Button { Content = "Save changes", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(4) };
        DockPanel.SetDock(apply, Dock.Bottom); panel.Children.Add(apply);
        var files = new Button { Content = "Audio file tags and album details…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4) };
        DockPanel.SetDock(files, Dock.Top); panel.Children.Add(files);
        panel.Children.Add(new ScrollViewer { Content = form, MaxHeight = SystemParameters.WorkArea.Height * .65, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        form.Children.Add(new TextBlock { Text = "Enter one tag per line.", TextWrapping = TextWrapping.Wrap });
        var controls = new List<(string Key, string Initial, TextBox Value)>();
        foreach (var field in metadata.Fields)
        {
            var input = new TextBox
            {
                Text = field.Value ?? "",
                AcceptsReturn = field.Tags,
                MinLines = field.Tags ? 2 : 1,
                ToolTip = field.Value == null ? "Mixed values" : field.Label
            };
            form.Children.Add(new Label { Content = field.Value == null ? field.Label + " (mixed values)" : field.Label, Target = input });
            form.Children.Add(input);
            controls.Add((field.Key, input.Text, input));
            input.TextChanged += (_, _) => apply.IsEnabled = vm.CanWrite && controls.Any(c => c.Value.Text != c.Initial);
        }
        var window = Dialogs.Window($"Edit metadata · {metadata.Count} track(s)", panel, 550);
        files.Click += async (_, _) => {
            try {
                var tracks=metadata.Tracks.ToArray();
                var cue=await vm.ReadCue(vm.Scope,tracks[0]);
                if(!window.IsVisible)return;
                new Editor(vm,tracks,cue,fileTags:true).ShowDialog();window.DialogResult=false;
            } catch(Exception ex){vm.Status=Session.Message(ex);}
        };
        apply.IsEnabled = false;
        apply.Click += (_, _) => window.DialogResult = true;
        return window.ShowDialog() == true ? controls.Where(c => c.Value.Text != c.Initial).ToDictionary(c => c.Key, c => c.Value.Text) : null;
    }
}
