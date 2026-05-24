using System.Windows;
using System.Windows.Input;

namespace Hbpos.Client.Wpf.Views.Windows;

public partial class CustomerDisplayWindow : Window
{
    public CustomerDisplayWindow()
    {
        InitializeComponent();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    public void SetTitleBarVisible(bool isVisible)
    {
        TitleBar.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        TitleBarRow.Height = isVisible ? new GridLength(44) : new GridLength(0);
        ResizeMode = isVisible ? ResizeMode.CanResize : ResizeMode.NoResize;
    }

    private void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }
}
