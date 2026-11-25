using System.Windows;
using IdeviceGui.Resources;

namespace IdeviceGui.Views;

internal sealed partial class GroupNameWindow : Window
{
    public GroupNameWindow(string title, string initial)
    {
        InitializeComponent();
        Title = title;
        NameBox.Text = initial;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    public string GroupName => NameBox.Text.Trim();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (GroupName.Length == 0)
        {
            MessageBox.Show(this, Strings.Group_Name_EmptyWarning, Strings.Group_Name_EmptyWarningTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
