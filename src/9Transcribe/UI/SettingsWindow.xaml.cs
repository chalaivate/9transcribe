using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using NineTranscribe.Diagnostics;

namespace NineTranscribe.UI;

public partial class SettingsWindow : Window
{
    public const int GeneralPageIndex = 0;
    public const int ApiPageIndex = 1;
    public const int TestPageIndex = 4;
    public const int AboutPageIndex = 5;
    private const int PageCount = 6;

    private const string RepositoryUrl = "https://github.com/chalaivate/9transcribe";

    private readonly SettingsViewModel _viewModel;
    private readonly FrameworkElement[]? _pages;
    private bool _syncingKeyBoxes;
    private int _currentPage = -1;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        _pages = new FrameworkElement[] { PageGeneral, PageApi, PageHotkeys, PageVocabulary, PageTest, PageAbout };

        string version = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        VersionText.Text = $"เวอร์ชัน {version}";
        AboutVersionText.Text = $"เวอร์ชัน {version}";

        // The .ico holds several sizes; BitmapImage would take the first (smallest) one.
        BitmapSource? icon = LoadLargestIconFrame();
        BrandIcon.Source = icon;
        AboutIcon.Source = icon;

        ShowPage(GeneralPageIndex);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyTitleBarTheme();
        TryApplyMica();
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
        if (NativeTitleBar.DwmSetWindowAttribute(handle, NativeTitleBar.UseImmersiveDarkMode, ref useDark, sizeof(int)) != 0)
        {
            NativeTitleBar.DwmSetWindowAttribute(handle, NativeTitleBar.UseImmersiveDarkModeLegacy, ref useDark, sizeof(int));
        }
    }

    /// <summary>
    /// Asks Windows 11 to paint the window with its Mica material — the tinted, subtly
    /// textured backdrop the Settings app uses. Everything WPF does not paint shows the
    /// material, which is why the cards are translucent. On Windows 10, or a Windows 11
    /// build older than 22H2, the attribute is refused and the solid theme background stays.
    /// </summary>
    private void TryApplyMica()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || Environment.OSVersion.Version.Build < NativeTitleBar.FirstBuildWithBackdrops)
        {
            return;
        }

        var margins = new NativeTitleBar.Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        if (NativeTitleBar.DwmExtendFrameIntoClientArea(handle, ref margins) != 0)
        {
            return;
        }

        int backdrop = NativeTitleBar.BackdropMica;
        if (NativeTitleBar.DwmSetWindowAttribute(handle, NativeTitleBar.SystemBackdropType, ref backdrop, sizeof(int)) != 0)
        {
            return;
        }

        if (HwndSource.FromHwnd(handle) is { } source)
        {
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
        }

        Background = Brushes.Transparent;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // The app lives in the tray; closing the window only hides it.
        e.Cancel = true;
        _viewModel.StopMonitoring();
        _viewModel.Flush();
        Hide();
    }

    /// <summary>Reopens the window on the page the caller cares about, e.g. API on first run.</summary>
    public void ShowOnTab(int pageIndex)
    {
        Nav.SelectedIndex = Math.Clamp(pageIndex, 0, PageCount - 1);
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        // Setting SelectedIndex in XAML can raise this before the constructor has built the
        // page table; the constructor shows the first page itself once everything exists.
        if (_pages is null)
        {
            return;
        }

        if (Nav.SelectedIndex < 0)
        {
            // Ctrl+clicking the selected item deselects it; the page must keep its highlight.
            Nav.SelectedIndex = Math.Max(_currentPage, 0);
            return;
        }

        ShowPage(Nav.SelectedIndex);
    }

    private void ShowPage(int index)
    {
        if (_pages is null || index == _currentPage)
        {
            return;
        }

        _currentPage = index;

        for (int i = 0; i < _pages.Length; i++)
        {
            _pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        }

        // A short fade so a page change reads as a change rather than a flicker.
        FrameworkElement page = _pages[index];
        page.BeginAnimation(OpacityProperty, null);
        page.Opacity = 0;
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });

        // The microphone is open only while the test page is on screen.
        if (index == TestPageIndex)
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

    private void OnOpenGitHub(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(RepositoryUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Repository page could not be opened: {ex.Message}");
        }
    }

    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Log.Directory);
            Process.Start(new ProcessStartInfo(Log.Directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Log folder could not be opened: {ex.Message}");
        }
    }

    private static BitmapSource? LoadLargestIconFrame()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            using Stream stream = Application.GetResourceStream(uri)!.Stream;
            var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

            BitmapFrame? largest = null;
            foreach (BitmapFrame frame in decoder.Frames)
            {
                if (largest is null || frame.PixelWidth > largest.PixelWidth)
                {
                    largest = frame;
                }
            }

            largest?.Freeze();
            return largest;
        }
        catch (Exception ex)
        {
            Log.Warn($"App icon could not be decoded for the settings window: {ex.Message}");
            return null;
        }
    }
}

internal static class NativeTitleBar
{
    internal const int UseImmersiveDarkModeLegacy = 19;
    internal const int UseImmersiveDarkMode = 20;
    internal const int SystemBackdropType = 38;

    /// <summary>DWMSBT_MAINWINDOW — the Mica material.</summary>
    internal const int BackdropMica = 2;

    /// <summary>Windows 11 22H2, the first build to honour DWMWA_SYSTEMBACKDROP_TYPE.</summary>
    internal const int FirstBuildWithBackdrops = 22621;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    internal static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
}
