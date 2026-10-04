using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using PDWSLC.Core;

namespace PDWSLC.App;
internal sealed class PropertiesWindow : Window
{
    private readonly TextBox details = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 14) };
    private readonly TextBox logs = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new("Consolas"), FontSize = 12 };
    public PropertiesWindow(Window owner, DesktopInfo desktop, string folder)
    {
        Owner = owner; Title = desktop.Name + " Properties"; Width = 780; Height = 640;
        MinWidth = 580; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = owner.FontFamily; FontSize = 14;
        var grid = new Grid { Margin = new(20) };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        grid.Children.Add(details);
        var heading = new TextBlock { Text = "Logs (last 100 lines)", FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 8) };
        Grid.SetRow(heading, 1); grid.Children.Add(heading);
        Grid.SetRow(logs, 2); grid.Children.Add(logs);
        System.Windows.Automation.AutomationProperties.SetAutomationId(details, "DesktopProperties");
        System.Windows.Automation.AutomationProperties.SetAutomationId(logs, "DesktopLogs");
        Content = grid;
        Update(desktop, folder); logs.Text = "Loading logs…";
    }
    public void Update(DesktopInfo desktop, string folder) => details.Text =
        $"Name: {desktop.Name}\nFolder: {folder}\nState: {desktop.State}\nImage: {desktop.Image}\nStorage: {desktop.Storage}\nManagement: {desktop.Management}\nCreated: {Date(desktop.CreatedAt)}\nLast started: {Date(desktop.StartedAt)}\nLast stopped: {Date(desktop.FinishedAt)}\nURL: {desktop.Url ?? "Unavailable"}";
    public void SetLogs(string text) => logs.Text = text;
    private static string Date(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) && date.Year > 1
            ? date.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") : "Not recorded";
}
