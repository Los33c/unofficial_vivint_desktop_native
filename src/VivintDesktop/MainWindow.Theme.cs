using System.Windows;
using System.Windows.Controls;

namespace VivintDesktop;

/// <summary>Theme switching for the main window.</summary>
public partial class MainWindow
{
    private void ThemeBtn_Click(object sender, RoutedEventArgs e) => Theme.Set(Theme.Next(Theme.Choice));

    private void UpdateThemeButton()
    {
        ThemeBtn.Content = Theme.Choice switch
        {
            ThemeChoice.Light => "",   // sun
            ThemeChoice.Dark => "",    // moon
            _ => "",                   // monitor = follow Windows
        };
        var label = Theme.Choice switch
        {
            ThemeChoice.Light => "Light theme",
            ThemeChoice.Dark => "Dark theme",
            _ => $"Matching Windows ({(Theme.IsDark ? "dark" : "light")})",
        };
        ThemeBtn.ToolTip = $"{label} — click to switch";
    }

    /// <summary>XAML follows the theme by itself; pages built in code are rebuilt with the new colors.</summary>
    private async void OnThemeChanged()
    {
        UpdateThemeButton();
        if (_state is not null) RenderHome();
        RenderFeed();
        RenderTimeline();
        BuildPanelButtons();
        _automationsLoaded = _settingsLoaded = _peopleLoaded = false;
        HistoryToolbar.Children.Clear();
        if (PanelOverlay.Visibility == Visibility.Visible)
        {
            if (Tabs.SelectedItem == HistoryTab) { BuildHistoryToolbar(); RenderHistoryList(); if (_histSelected is { } h) ShowHistoryDetail(h); }
            else await LoadCurrentTabAsync();
        }
    }
}
