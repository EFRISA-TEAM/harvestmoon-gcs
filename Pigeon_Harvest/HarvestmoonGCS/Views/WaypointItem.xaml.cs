using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using HarvestmoonGCS.Core.Models;
using Serilog;

namespace HarvestmoonGCS.Views;

/// <summary>
/// Interaction logic for WaypointItem.xaml
/// </summary>
public sealed partial class WaypointItem : UserControl
{
    public WaypointItem()
    {
        InitializeComponent();
    }

    public WaypointItem(WaypointData waypoint) : this()
    {
        TheWaypointData = waypoint;
        UpdateUI();
    }

    private void UpdateUI()
    {
        if (TheWaypointData == null) return;

        wp_name.Text = $"#{TheWaypointData.Sequence} - {TheWaypointData.Command}";
        wp_lat.Text = TheWaypointData.Latitude.ToString("0.#########");
        wp_longt.Text = TheWaypointData.Longitude.ToString("0.#########");
        
        if (wp_alt != null) wp_alt.Text = TheWaypointData.Altitude.ToString("0.##");
        if (wp_param1 != null) wp_param1.Text = TheWaypointData.Param1.ToString("0.##");
        if (wp_param2 != null) wp_param2.Text = TheWaypointData.Param2.ToString("0.##");
        if (wp_param3 != null) wp_param3.Text = TheWaypointData.Param3.ToString("0.##");
        if (wp_param4 != null) wp_param4.Text = TheWaypointData.Param4.ToString("0.##");
        
        if (wp_command != null)
        {
            foreach (ComboBoxItem item in wp_command.Items)
            {
                if (item.Content?.ToString() == TheWaypointData.Command.ToString())
                {
                    wp_command.SelectedItem = item;
                    break;
                }
                if (TheWaypointData.Command == WaypointCommand.TakeOff && item.Content?.ToString() == "Takeoff")
                {
                    wp_command.SelectedItem = item;
                    break;
                }
            }
        }
    }

    public void SetProperties(double lat, double lng)
    {
        if (TheWaypointData != null)
        {
            TheWaypointData.Latitude = lat;
            TheWaypointData.Longitude = lng;
        }
        wp_lat.Text = lat.ToString("0.#########");
        wp_longt.Text = lng.ToString("0.#########");
    }

    /// <summary>
    /// Gets the waypoint data for this item
    /// </summary>
    public WaypointData TheWaypointData { get; private set; }

    private void wp_lat_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (double.TryParse(wp_lat.Text, out double lat) && TheWaypointData != null)
        {
            TheWaypointData.Latitude = lat;
        }
    }

    private void wp_longt_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (double.TryParse(wp_longt.Text, out double lng) && TheWaypointData != null)
        {
            TheWaypointData.Longitude = lng;
        }
    }

    private void Button_Click(object sender, RoutedEventArgs e)
    {
        wp_grid.Children.Clear();
        wp_grid.RowDefinitions.Clear();
        wp_grid.ColumnDefinitions.Clear();
    }

    private void wp_command_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (wp_command.SelectedItem is ComboBoxItem selectedItem && TheWaypointData != null)
        {
            string selectedContent = selectedItem.Content?.ToString() ?? "";
            
            // Update command based on selection
            TheWaypointData.Command = selectedContent switch
            {
                "Waypoint" => WaypointCommand.Waypoint,
                "Takeoff" => WaypointCommand.TakeOff,
                "Land" => WaypointCommand.Land,
                "SetHome" => WaypointCommand.SetHome,
                _ => WaypointCommand.Waypoint
            };
            
            UpdateUI();
        }
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (TheWaypointData == null) return;

            var dialog = new WaypointEditDialog(TheWaypointData);
            dialog.XamlRoot = this.XamlRoot;

            // ShowAsync throws if another ContentDialog is already open on this XamlRoot.
            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary && dialog.ResultWaypoint != null)
            {
                TheWaypointData.Latitude = dialog.ResultWaypoint.Latitude;
                TheWaypointData.Longitude = dialog.ResultWaypoint.Longitude;
                TheWaypointData.Altitude = dialog.ResultWaypoint.Altitude;
                TheWaypointData.Command = dialog.ResultWaypoint.Command;
                TheWaypointData.Param1 = dialog.ResultWaypoint.Param1;
                TheWaypointData.Param2 = dialog.ResultWaypoint.Param2;
                TheWaypointData.Param3 = dialog.ResultWaypoint.Param3;
                TheWaypointData.Param4 = dialog.ResultWaypoint.Param4;

                UpdateUI();
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[WaypointItem] Edit dialog failed");
        }
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        var parent = VisualTreeHelper.GetParent(this) as Panel;
        if (parent == null) return;

        int index = parent.Children.IndexOf(this);
        if (index > 0)
        {
            parent.Children.RemoveAt(index);
            parent.Children.Insert(index - 1, this);
            ReorderWaypoints(parent);
        }
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        var parent = VisualTreeHelper.GetParent(this) as Panel;
        if (parent == null) return;

        int index = parent.Children.IndexOf(this);
        if (index < parent.Children.Count - 1)
        {
            parent.Children.RemoveAt(index);
            parent.Children.Insert(index + 1, this);
            ReorderWaypoints(parent);
        }
    }

    private void ReorderWaypoints(Panel parent)
    {
        int sequence = 1;
        foreach (var child in parent.Children.OfType<WaypointItem>())
        {
            if (child.TheWaypointData != null)
            {
                child.TheWaypointData.Sequence = sequence++;
                child.UpdateUI();
            }
        }
    }
}

    
