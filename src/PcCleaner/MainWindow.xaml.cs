using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using PcCleaner.Core.Updates;

namespace PcCleaner;

public partial class MainWindow : Window
{
    private const double SplashBlurRadius = 18;
    private static readonly Duration SplashFade = new(TimeSpan.FromMilliseconds(250));

    private SplashViewModel? _splash;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Dark title bar (Windows 10 20H1+ / Windows 11).
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var enabled = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
        }
        catch
        {
            // cosmetic only
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_splash is not null)
            _splash.PropertyChanged -= OnSplashChanged;
        _splash = (e.NewValue as MainViewModel)?.Splash;
        if (_splash is not null)
            _splash.PropertyChanged += OnSplashChanged;
        ShowSplash(_splash?.IsVisible == true, animate: false);
    }

    private void OnSplashChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SplashViewModel.IsVisible))
            return;
        if (Dispatcher.CheckAccess())
            ShowSplash(_splash?.IsVisible == true, animate: true);
        else
            Dispatcher.BeginInvoke(() => ShowSplash(_splash?.IsVisible == true, animate: true));
    }

    /// <summary>
    /// The splash shows over a blurred <c>Root</c>; hiding it fades the overlay and the blur out together, then drops
    /// both. The blur exists only while the splash is up (an Effect would make the window render through an offscreen
    /// surface for good), and screenshot mode never gets here: it never starts the splash.
    /// </summary>
    private void ShowSplash(bool visible, bool animate)
    {
        SplashOverlay.BeginAnimation(OpacityProperty, null);
        (Root.Effect as BlurEffect)?.BeginAnimation(BlurEffect.RadiusProperty, null);

        if (visible)
        {
            Root.Effect = new BlurEffect { Radius = SplashBlurRadius, RenderingBias = RenderingBias.Performance };
            SplashOverlay.Opacity = 1;
            SplashOverlay.Visibility = Visibility.Visible;
            SplashOverlay.Focus(); // keep the keyboard off the controls behind the blur
            return;
        }

        if (!animate || SplashOverlay.Visibility != Visibility.Visible)
        {
            EndSplash();
            return;
        }
        var fade = new DoubleAnimation(0, SplashFade);
        fade.Completed += (_, _) =>
        {
            if (_splash?.IsVisible != true)
                EndSplash();
        };
        SplashOverlay.BeginAnimation(OpacityProperty, fade);
        (Root.Effect as BlurEffect)?.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(0, SplashFade));
    }

    private void EndSplash()
    {
        SplashOverlay.BeginAnimation(OpacityProperty, null);
        SplashOverlay.Visibility = Visibility.Collapsed;
        SplashOverlay.Opacity = 1;
        Root.Effect = null;
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
