using System.Windows;
using System.Windows.Controls;

namespace PDWSLC.App;

// Small task windows keep the main explorer focused on the folder tree.
internal sealed class EditorWindow : Window
{
    private readonly StackPanel fields = new() { Margin = new Thickness(22) };
    public EditorWindow(Window owner, string title, double width = 510)
    {
        Owner = owner; Title = title; Width = width; SizeToContent = SizeToContent.Height;
        MaxHeight = 700; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = owner.FontFamily; FontSize = 14; ResizeMode = ResizeMode.NoResize;
        Content = new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    public void Text(string text) => fields.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) });
    public TextBox Field(string label, string value, string id)
    {
        Text(label);
        var box = new TextBox { Text = value, Margin = new(0, 0, 0, 12) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(box, id);
        fields.Children.Add(box); return box;
    }
    public CheckBox Check(string text, bool value = false)
    {
        var box = new CheckBox { Content = text, IsChecked = value, Margin = new(0, 0, 0, 12) };
        fields.Children.Add(box); return box;
    }
    public void Submit(string label, Func<bool> action)
    {
        var error = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
        var submit = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Left, IsDefault = true };
        submit.Click += (_, _) => { try { if (action()) DialogResult = true; } catch (Exception ex) { error.Text = ex.Message; } };
        fields.Children.Add(submit); fields.Children.Add(error);
    }
}
