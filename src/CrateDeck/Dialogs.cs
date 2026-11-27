using System.Windows.Input;

namespace CrateDeck;

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
