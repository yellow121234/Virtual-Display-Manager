using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace VirtualDisplayManager.App;

public partial class PreviewWindow : Window
{
    private bool _isFullScreen;

    public PreviewWindow(string monitorName)
    {
        InitializeComponent();
        MonitorNameText.Text = monitorName;
        StatusText.Text = "미리보기 시작 중";
    }

    public void SetFrame(BitmapSource frame)
    {
        LargePreviewImage.Source = frame;
        PlaceholderText.Visibility = Visibility.Collapsed;
    }

    public void SetStatus(string status) => StatusText.Text = status;

    public void SetMonitorName(string monitorName) => MonitorNameText.Text = monitorName;

    public void ClearFrame(string status)
    {
        LargePreviewImage.Source = null;
        PlaceholderText.Text = status;
        PlaceholderText.Visibility = Visibility.Visible;
        StatusText.Text = status;
    }

    private void FullScreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void PreviewArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleFullScreen();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _isFullScreen)
        {
            ToggleFullScreen();
            e.Handled = true;
        }
    }

    private void ToggleFullScreen()
    {
        _isFullScreen = !_isFullScreen;
        if (_isFullScreen)
        {
            Toolbar.Visibility = Visibility.Collapsed;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            Toolbar.Visibility = Visibility.Visible;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
