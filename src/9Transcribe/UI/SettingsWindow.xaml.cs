using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using NineTranscribe.Settings;

namespace NineTranscribe.UI;

public partial class SettingsWindow : Window
{
    private const int TestTabIndex = 4;

    private readonly SettingsViewModel _viewModel;
    private bool _syncingKeyBoxes;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyTitleBarTheme();
    }

    /// <summary>
    /// Matches the title bar to the app theme. WPF does not style the non-client area, so
    /// without this a dark window keeps a white caption bar.
    /// </summary>
    public void ApplyTitleBarTheme()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        int useDark = ThemeManager.IsDarkActive ? 1 : 0;
        // Attribute 20 on 20H1 and later; 19 on the builds before it.
        if (NativeTitleBar.DwmSetWindowAttribute(handle, 20, ref useDark, sizeof(int)) != 0)
        {
            NativeTitleBar.DwmSetWindowAttribute(handle, 19, ref useDark, sizeof(int));
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // The app lives in the tray; closing the window only hides it.
        e.Cancel = true;
        _viewModel.StopMonitoring();
        _viewModel.Flush();
        Hide();
    }

    /// <summary>Reopens the window on the tab the caller cares about, e.g. API on first run.</summary>
    public void ShowOnTab(int tabIndex)
    {
        Tabs.SelectedIndex = Math.Clamp(tabIndex, 0, Tabs.Items.Count - 1);
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs))
        {
            // Ignore selection changes bubbling up from combo boxes inside a tab.
            return;
        }

        if (Tabs.SelectedIndex == TestTabIndex)
        {
            _viewModel.RefreshDevices();
            _viewModel.StartMonitoring();
        }
        else
        {
            _viewModel.StopMonitoring();
        }
    }

    private void OnApiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingKeyBoxes)
        {
            return;
        }

        _syncingKeyBoxes = true;
        ApiKeyPlainBox.Text = ApiKeyBox.Password;
        _syncingKeyBoxes = false;

        _viewModel.SetApiKey(ApiKeyBox.Password);
    }

    private void OnApiKeyPlainChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingKeyBoxes)
        {
            return;
        }

        _syncingKeyBoxes = true;
        ApiKeyBox.Password = ApiKeyPlainBox.Text;
        _syncingKeyBoxes = false;

        _viewModel.SetApiKey(ApiKeyPlainBox.Text);
    }

    private void OnRevealKey(object sender, RoutedEventArgs e)
    {
        ApiKeyPlainBox.Visibility = Visibility.Visible;
        ApiKeyBox.Visibility = Visibility.Collapsed;
        RevealKeyButton.Content = "ซ่อน";
    }

    private void OnHideKey(object sender, RoutedEventArgs e)
    {
        ApiKeyPlainBox.Visibility = Visibility.Collapsed;
        ApiKeyBox.Visibility = Visibility.Visible;
        RevealKeyButton.Content = "แสดง";
    }
}

internal static class NativeTitleBar
{
    [DllImport("dwmapi.dll", ExactSpelling = true)]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
