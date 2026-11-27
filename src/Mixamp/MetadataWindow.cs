namespace Mixamp;

static class MetadataWindow
{
    public static Dictionary<string, string>? Edit(TrackMetadata metadata, Session vm)
    {
        var form = new StackPanel();
        var panel = new DockPanel { Margin = new Thickness(14) };
        var apply = new Button { Content = "Save changes", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(4) };
        DockPanel.SetDock(apply, Dock.Bottom); panel.Children.Add(apply);
        var files = new Button { Content = "Audio file tags and album details…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4) };
        DockPanel.SetDock(files, Dock.Top); panel.Children.Add(files);
        panel.Children.Add(new ScrollViewer { Content = form, MaxHeight = SystemParameters.WorkArea.Height * .65, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        form.Children.Add(new TextBlock { Text = "Check the fields to apply to all selected tracks. Enter one tag per line.", TextWrapping = TextWrapping.Wrap });
        var controls = new List<(string Key, CheckBox Apply, TextBox Value)>();
        foreach (var field in metadata.Fields)
        {
            var check = new CheckBox { Content = field.Label, Margin = new Thickness(0, 7, 0, 2) };
            var input = new TextBox
            {
                Text = field.Value ?? "",
                AcceptsReturn = field.Tags,
                MinLines = field.Tags ? 2 : 1,
                ToolTip = field.Value == null ? "Mixed values" : field.Label
            };
            input.TextChanged += (_, _) => check.IsChecked = true;
            form.Children.Add(check); form.Children.Add(input); controls.Add((field.Key, check, input));
        }
        var window = Dialogs.Window($"Edit metadata · {metadata.Count} track(s)", panel, 550);
        files.Click += (_, _) => { new Editor(vm, metadata.Tracks.ToArray(), fileTags: true).ShowDialog(); window.DialogResult = false; };
        apply.IsEnabled = vm.CanWrite;
        apply.Click += (_, _) => window.DialogResult = true;
        return window.ShowDialog() == true ? controls.Where(c => c.Apply.IsChecked == true).ToDictionary(c => c.Key, c => c.Value.Text) : null;
    }
}
