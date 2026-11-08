using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace VivintDesktop;

/// <summary>
/// Small shared building blocks so every panel uses the same patterns:
/// list rows, filter chips, segmented controls, on/off switches and "⋯" action menus.
/// </summary>
public partial class MainWindow
{
    private FontFamily IconFont => (FontFamily)FindResource("Icons");

    /// <summary>A list row: optional leading element, title + subtitle, optional trailing element, hover, optional click.</summary>
    private Border ListRow(UIElement? lead, string title, string? sub, UIElement? trail, Action? click = null, Brush? subBrush = null)
    {
        var dock = new DockPanel { LastChildFill = true };
        if (trail is not null)
        {
            if (trail is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(trail, Dock.Right);
            dock.Children.Add(trail);
        }
        if (lead is not null)
        {
            if (lead is FrameworkElement fe) { fe.VerticalAlignment = VerticalAlignment.Center; fe.Margin = new Thickness(0, 0, 12, 0); }
            DockPanel.SetDock(lead, Dock.Left);
            dock.Children.Add(lead);
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Foreground = Res("Text"), TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(sub))
            text.Children.Add(new TextBlock { Text = sub, FontSize = 12, Foreground = subBrush ?? Res("Muted"), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0) });
        dock.Children.Add(text);
        var row = new Border
        {
            Child = dock, Padding = new Thickness(14, 10, 10, 10), Background = Brushes.Transparent,
            BorderBrush = Res("Border"), BorderThickness = new Thickness(0, 0, 0, 1),
        };
        row.MouseEnter += (_, _) => row.Background = Res("Surface2");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        if (click is not null)
        {
            row.Cursor = Cursors.Hand;
            row.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not ButtonBase) click(); };
        }
        return row;
    }

    /// <summary>Rows grouped in one rounded surface.</summary>
    private Border ListCard(IEnumerable<UIElement> rows, double? maxWidth = null)
    {
        var stack = new StackPanel();
        foreach (var r in rows) stack.Children.Add(r);
        if (stack.Children.Count > 0 && stack.Children[^1] is Border last) last.BorderThickness = new Thickness(0);
        return new Border
        {
            Background = Res("Surface"), BorderBrush = Res("Border"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
            Child = stack, ClipToBounds = true, Margin = new Thickness(0, 0, 0, 14), MaxWidth = maxWidth ?? double.PositiveInfinity,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
    }

    private TextBlock SectionTitle(string text, string? sub = null)
    {
        var tb = new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Res("Text"), Margin = new Thickness(2, 6, 0, sub is null ? 10 : 2) };
        return tb;
    }

    private TextBlock SectionNote(string text) => new()
    {
        Text = text, FontSize = 12, Foreground = Res("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 10),
    };

    private ToggleButton Chip(string text, bool on, Action<bool> changed)
    {
        var c = new ToggleButton { Content = text, IsChecked = on, Style = (Style)FindResource("Chip") };
        c.Click += (_, _) => changed(c.IsChecked == true);
        return c;
    }

    /// <summary>Segmented control; returns the host. <paramref name="selected"/> is the initial index.</summary>
    private Border Segmented(string group, int selected, params (string label, Action select)[] items)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < items.Length; i++)
        {
            var (label, select) = items[i];
            var rb = new RadioButton { Content = label, GroupName = group + GetHashCode(), IsChecked = i == selected, Style = (Style)FindResource("Segment") };
            rb.Checked += (_, _) => select();
            sp.Children.Add(rb);
        }
        return new Border { Background = Res("Surface2"), CornerRadius = new CornerRadius(9), Padding = new Thickness(2), Child = sp,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 14) };
    }

    /// <summary>On/off switch that runs <paramref name="changed"/> and reverts itself if the change throws.</summary>
    private CheckBox Switch(bool on, Func<bool, Task> changed, string? label = null, string? tooltip = null)
    {
        var cb = new CheckBox { IsChecked = on, Style = (Style)FindResource("Switch"), Content = label, ToolTip = tooltip };
        cb.Click += async (_, _) =>
        {
            var v = cb.IsChecked == true;
            cb.IsEnabled = false;
            try { await changed(v); }
            catch { cb.IsChecked = !v; }
            finally { cb.IsEnabled = true; }
        };
        return cb;
    }

    /// <summary>"⋯" button that opens a menu of actions. Danger items are shown in red.</summary>
    private Button MoreMenu(params (string label, Func<Task> act, bool danger)[] items)
    {
        var b = new Button { Content = "", Style = (Style)FindResource("IconButton"), ToolTip = "More actions" };
        System.Windows.Automation.AutomationProperties.SetName(b, "More actions");
        b.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Bottom };
            foreach (var (label, act, danger) in items)
            {
                var mi = new MenuItem { Header = label };
                if (danger) mi.Foreground = Res("Danger");
                mi.Click += async (_, _) => await act();
                menu.Items.Add(mi);
            }
            menu.IsOpen = true;
        };
        return b;
    }

    private Button MakeBtnLeft(string text, Func<Task> onClick, string? style = "GhostButton")
    {
        var b = MakeBtn(text, onClick, style);
        b.HorizontalAlignment = HorizontalAlignment.Left;
        b.Margin = new Thickness(0, 10, 0, 0);
        return b;
    }

    private Border Avatar(string name, double size = 36)
    {
        var initials = string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = Res("AccentSoft"),
            Child = new TextBlock { Text = initials, FontWeight = FontWeights.SemiBold, Foreground = Res("AccentText"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
    }

    private Border IconTile(string glyph, Brush? fg = null, double size = 34) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(9), Background = Res("Surface2"),
        Child = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 15, Foreground = fg ?? Res("Muted"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
    };

    private TextBlock Pill(string text, string bgKey, string fgKey) => new()
    {
        Text = text, FontSize = 11.5, Foreground = Res(fgKey), Background = Res(bgKey), Padding = new Thickness(8, 2, 8, 2),
        Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
    };
}
