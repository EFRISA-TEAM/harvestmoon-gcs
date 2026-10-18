using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using HarvestmoonGCS.Core.Models;

namespace HarvestmoonGCS.Controls;

/// <summary>
/// Aeronautical Mission Waypoint row control conforming to SINTA 2 precision agriculture standards
/// </summary>
public sealed partial class WaypointItem : UserControl
{
    public WaypointData Waypoint { get; }
    public event EventHandler<WaypointData>? DeleteRequested;
    public event EventHandler<WaypointData>? CenterRequested;

    public WaypointItem(WaypointData waypoint)
    {
        Waypoint = waypoint;
        DataContext = waypoint;
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        var rootBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x1E, 0x24, 0x30)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x33, 0x41, 0x55)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 2, 0, 2),
            Padding = new Thickness(8, 5, 8, 5)
        };

        var grid = new Grid
        {
            Height = 36,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Col definitions
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) }); // WP#
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(95) }); // Command
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(115) }); // Lat
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(115) }); // Lon
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });  // Alt
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });  // Speed
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(65) });  // Actions

        // 1. WP Sequence Badge
        Color badgeColor = Waypoint.Command switch
        {
            WaypointCommand.TakeOff => Color.FromArgb(0xFF, 0x10, 0xB9, 0x81), // Emerald
            WaypointCommand.Land or WaypointCommand.ReturnToLaunch => Color.FromArgb(0xFF, 0xEF, 0x44, 0x44), // Red
            _ => Color.FromArgb(0xFF, 0x0E, 0xA5, 0xE9) // Sky Blue
        };

        var seqBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x33, badgeColor.R, badgeColor.G, badgeColor.B)),
            BorderBrush = new SolidColorBrush(badgeColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Width = 36,
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        var seqText = new TextBlock
        {
            Text = $"#{Waypoint.Sequence}",
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new SolidColorBrush(badgeColor),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        seqBadge.Child = seqText;
        Grid.SetColumn(seqBadge, 0);

        // 2. Command Badge
        string cmdLabel = Waypoint.Command switch
        {
            WaypointCommand.TakeOff => "TAKEOFF",
            WaypointCommand.Land => "LAND",
            WaypointCommand.ReturnToLaunch => "RTL",
            WaypointCommand.Loiter => "LOITER",
            _ => "NAV_WP"
        };
        var cmdBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0x94, 0xA3, 0xB8)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, 0x94, 0xA3, 0xB8)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        var cmdText = new TextBlock
        {
            Text = cmdLabel,
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xCB, 0xD5, 0xE1))
        };
        cmdBorder.Child = cmdText;
        Grid.SetColumn(cmdBorder, 1);

        // 3. Latitude
        var latText = new TextBlock
        {
            Text = $"{Waypoint.Latitude:F6}°",
            FontSize = 11,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xF1, 0xF5, 0xF9)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(latText, 2);

        // 4. Longitude
        var lonText = new TextBlock
        {
            Text = $"{Waypoint.Longitude:F6}°",
            FontSize = 11,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xF1, 0xF5, 0xF9)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(lonText, 3);

        // 5. Altitude AGL
        var altText = new TextBlock
        {
            Text = $"{Waypoint.Altitude:F0} m AGL",
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x34, 0xD3, 0x99)), // Green
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(altText, 4);

        // 6. Speed
        var speedText = new TextBlock
        {
            Text = "8.0 m/s",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x94, 0xA3, 0xB8)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(speedText, 5);

        // 7. Actions (Center and Delete)
        var actionsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };

        var btnCenter = new Button
        {
            Padding = new Thickness(6, 3, 6, 3),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0x38, 0xBD, 0xF8)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, 0x38, 0xBD, 0xF8)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Content = new TextBlock { Text = "🎯", FontSize = 10 }
        };
        ToolTipService.SetToolTip(btnCenter, "Pusatkan peta ke Waypoint ini");
        btnCenter.Click += (s, e) => CenterRequested?.Invoke(this, Waypoint);

        var btnDel = new Button
        {
            Padding = new Thickness(6, 3, 6, 3),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xEF, 0x44, 0x44)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, 0xEF, 0x44, 0x44)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Content = new TextBlock { Text = "🗑", FontSize = 10 }
        };
        ToolTipService.SetToolTip(btnDel, "Hapus Waypoint");
        btnDel.Click += (s, e) => DeleteRequested?.Invoke(this, Waypoint);

        actionsPanel.Children.Add(btnCenter);
        actionsPanel.Children.Add(btnDel);
        Grid.SetColumn(actionsPanel, 6);

        grid.Children.Add(seqBadge);
        grid.Children.Add(cmdBorder);
        grid.Children.Add(latText);
        grid.Children.Add(lonText);
        grid.Children.Add(altText);
        grid.Children.Add(speedText);
        grid.Children.Add(actionsPanel);

        rootBorder.Child = grid;
        Content = rootBorder;
    }
}
