using System.Windows.Input;

namespace Mixamp;

static class Dialogs
{
    public static Window Window(string title, FrameworkElement body, double width = 440)
    {
        var w = new Window
        {
            Title = title,
            Owner = Application.Current.MainWindow,
            Width = width,
            MaxHeight = SystemParameters.WorkArea.Height * .9,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = body,
            ShowInTaskbar = false,
        };
        if (w.Owner is MainWindow { UsesUbuntuFont: true } owner) w.FontFamily = owner.FontFamily;
        w.PreviewKeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape)
                w.Close();
        };
        return w;
    }

    public static int Choose(string title, string[] labels)
    {
        if (labels.Length == 0)
            throw new IOException("No " + title.ToLowerInvariant() + " available.");
        var panel = new StackPanel { Margin = new Thickness(16) };
        var list = new ListBox
        {
            ItemsSource = labels,
            SelectedIndex = 0,
            MaxHeight = 350,
            MinHeight = 72,
        };
        panel.Children.Add(list);
        var ok = new Button
        {
            Content = "Choose",
            IsDefault = true,
            Margin = new Thickness(0, 12, 0, 0),
        };
        panel.Children.Add(ok);
        var w = Window(title, panel);
        ok.Click += (s, e) => w.DialogResult = true;
        list.MouseDoubleClick += (s, e) => w.DialogResult = true;
        if (w.ShowDialog() != true)
            throw new OperationCanceledException();
        return list.SelectedIndex;
    }

    public static bool Review(string title, string summary, IEnumerable<string> entries, string? action)
    {
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0,0,0,10) });
        panel.Children.Add(new ListBox { ItemsSource = entries.ToArray(), MaxHeight = 330, MinHeight = 80 });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,12,0,0) };
        panel.Children.Add(buttons);
        var window = Window(title, panel, 740);
        var cancel = new Button { Content = action == null ? "Close" : "Cancel", IsCancel = true,
            MinWidth = 82, Margin = new Thickness(4) };
        buttons.Children.Add(cancel);
        if (action != null)
        {
            var commit = new Button { Content = action, Padding = new Thickness(12,4,12,4), Margin = new Thickness(4) };
            commit.Click += (_, _) => window.DialogResult = true;
            buttons.Children.Add(commit);
        }
        return window.ShowDialog() == true;
    }

    public static string Input(string title, string current = "")
    {
        var p = new StackPanel { Margin = new Thickness(16) };
        var box = new TextBox { Text = current };
        var ok = new Button
        {
            Content = "Continue",
            IsDefault = true,
            Margin = new Thickness(0, 12, 0, 0),
        };
        p.Children.Add(box);
        p.Children.Add(ok);
        var w = Window(title, p);
        ok.Click += (s, e) => w.DialogResult = true;
        w.Loaded += (s, e) =>
        {
            box.Focus();
            box.SelectAll();
        };
        if (w.ShowDialog() != true)
            throw new OperationCanceledException();
        return box.Text.Trim();
    }
}
