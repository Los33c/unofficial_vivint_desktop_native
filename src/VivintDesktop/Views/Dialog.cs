using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VivintDesktop.Views;

/// <summary>Themed replacement for MessageBox: confirmations and notices that follow light/dark.</summary>
public sealed class Dialog : Window
{
    private Dialog(Window? owner, string title, string message, string ok, string? cancel, bool danger, string glyph)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        if (owner is { IsLoaded: true }) Owner = owner;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        Theme.Style(this);

        var body = new StackPanel { Margin = new Thickness(22, 20, 22, 16) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var icon = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Margin = new Thickness(0, 0, 12, 0),
            Child = new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.Resources["Icons"], FontSize = 15,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        icon.SetResourceReference(Border.BackgroundProperty, danger ? "DangerSoft" : "AccentSoft");
        ((TextBlock)icon.Child).SetResourceReference(TextBlock.ForegroundProperty, danger ? "Danger" : "AccentText");
        head.Children.Add(icon);
        head.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap, MaxWidth = 340 });
        body.Children.Add(head);
        var msg = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 19, Margin = new Thickness(44, 0, 0, 18) };
        msg.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        body.Children.Add(msg);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var okBtn = new Button { Content = ok, IsDefault = true, MinWidth = 90, Style = (Style)Application.Current.Resources["PrimaryButton"] };
        if (danger)
        {
            okBtn.SetResourceReference(Button.BackgroundProperty, "Danger");
            okBtn.SetResourceReference(Button.BorderBrushProperty, "Danger");
        }
        okBtn.Click += (_, _) => DialogResult = true;
        if (cancel is not null)
        {
            var c = new Button { Content = cancel, IsCancel = true, MinWidth = 90 };
            bar.Children.Add(c);
        }
        bar.Children.Add(okBtn);
        body.Children.Add(bar);
        Content = body;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) DialogResult = false; };
        // Safe default: when there's a choice, keyboard focus starts on Cancel so a stray Enter/Space doesn't confirm.
        Loaded += (_, _) => (cancel is null ? okBtn : (UIElement)bar.Children[0]).Focus();
    }

    /// <summary>Ask a yes/no question. <paramref name="danger"/> styles the confirm button red (and focuses Cancel).</summary>
    public static bool Confirm(Window? owner, string title, string message, string ok = "OK", bool danger = false, string cancel = "Cancel")
        => new Dialog(owner, title, message, ok, cancel, danger, danger ? "" : "").ShowDialog() == true;

    /// <summary>Show a notice with a single OK button.</summary>
    public static void Info(Window? owner, string title, string message, bool warning = false)
        => new Dialog(owner, title, message, "OK", null, warning, warning ? "" : "").ShowDialog();
}
