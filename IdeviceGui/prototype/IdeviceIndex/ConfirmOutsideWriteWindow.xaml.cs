using System.Windows;

namespace IdeviceIndex;

public partial class ConfirmOutsideWriteWindow : Window
{
    public ConfirmOutsideWriteWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => CancelButton.Focus();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnEnableClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
