using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VivintDesktop.Views;

/// <summary>Small modal form: a few labelled text/password/checkbox fields.</summary>
public sealed class Prompt : Window
{
    private readonly List<(string key, Func<string> read)> _fields = new();
    private readonly StackPanel _body = new() { Margin = new Thickness(18, 16, 18, 8) };

    public Dictionary<string, string> Values { get; } = new();

    public Prompt(string title, string? intro = null)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Theme.Style(this);
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        if (intro is not null)
            _body.Children.Add(new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
    }

    public Prompt Text(string key, string label, string value = "", bool secret = false, int maxLength = 0)
    {
        _body.Children.Add(new TextBlock { Text = label, Foreground = Theme.Brush("Muted"), Margin = new Thickness(0, 4, 0, 2) });
        if (secret)
        {
            var pb = new PasswordBox { MaxLength = maxLength };
            _body.Children.Add(pb);
            _fields.Add((key, () => pb.Password));
        }
        else
        {
            var tb = new TextBox { Text = value, MaxLength = maxLength };
            _body.Children.Add(tb);
            _fields.Add((key, () => tb.Text.Trim()));
        }
        return this;
    }

    public Prompt Check(string key, string label, bool value = false)
    {
        var cb = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 8, 0, 0) };
        _body.Children.Add(cb);
        _fields.Add((key, () => cb.IsChecked == true ? "1" : ""));
        return this;
    }

    public Prompt Choice(string key, string label, IEnumerable<string> options, int selected = 0)
    {
        _body.Children.Add(new TextBlock { Text = label, Foreground = Theme.Brush("Muted"), Margin = new Thickness(0, 4, 0, 2) });
        var cb = new ComboBox { ItemsSource = options.ToList(), SelectedIndex = selected };
        _body.Children.Add(cb);
        _fields.Add((key, () => cb.SelectedIndex.ToString()));
        return this;
    }

    public Prompt Note(string text)
    {
        _body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("Muted"), FontSize = 11, Margin = new Thickness(0, 8, 0, 0) });
        return this;
    }

    /// <summary>Show the form; <paramref name="validate"/> returns an error message or null.</summary>
    public bool Ask(Window owner, string ok = "OK", Func<Dictionary<string, string>, string?>? validate = null)
    {
        Owner = owner;
        var okBtn = new Button { Content = ok, IsDefault = true, Style = (Style)Application.Current.Resources["PrimaryButton"], Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        okBtn.Click += (_, _) =>
        {
            Values.Clear();
            foreach (var (k, r) in _fields) Values[k] = r();
            if (validate?.Invoke(Values) is { } err) { Dialog.Info(this, Title, err); return; }
            DialogResult = true;
        };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 6) };
        bar.Children.Add(okBtn);
        bar.Children.Add(cancel);
        _body.Children.Add(bar);
        Content = _body;
        return ShowDialog() == true;
    }

    public static bool IsPin(string s) => s.Length == 4 && s.All(char.IsDigit);
}
