using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using PcCleaner.Core;
using PcCleaner.Core.Updates;
using PcCleaner.Modules.Profile;

namespace PcCleaner;

public partial class MainWindow : Window
{
    private const double BlurRadius = 18;
    private static readonly Duration OverlayFade = new(TimeSpan.FromMilliseconds(250));

    private SplashViewModel? _splash;
    private WelcomeViewModel? _welcome;
    private AccountIntroViewModel? _intro;

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
        if (_welcome is not null)
            _welcome.PropertyChanged -= OnWelcomeChanged;
        if (_intro is not null)
            _intro.PropertyChanged -= OnIntroChanged;
        var main = e.NewValue as MainViewModel;
        _splash = main?.Splash;
        _welcome = main?.Welcome;
        _intro = main?.AccountIntro;
        if (_splash is not null)
            _splash.PropertyChanged += OnSplashChanged;
        if (_welcome is not null)
            _welcome.PropertyChanged += OnWelcomeChanged;
        if (_intro is not null)
            _intro.PropertyChanged += OnIntroChanged;
        ShowOverlay(SplashOverlay, _splash?.IsVisible == true, animate: false, () => _splash?.IsVisible == true);
        ShowOverlay(AccountIntroOverlay, _intro?.IsVisible == true, animate: false, () => _intro?.IsVisible == true, fadeIn: true);
        ShowOverlay(WelcomeOverlay, _welcome?.IsVisible == true, animate: false, () => _welcome?.IsVisible == true, fadeIn: true);
    }

    private void OnSplashChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SplashViewModel.IsVisible))
            OnUi(() => ShowOverlay(SplashOverlay, _splash?.IsVisible == true, animate: true, () => _splash?.IsVisible == true));
    }

    private void OnIntroChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AccountIntroViewModel.IsVisible))
            OnUi(() => ShowOverlay(AccountIntroOverlay, _intro?.IsVisible == true, animate: true, () => _intro?.IsVisible == true, fadeIn: true));
    }

    private void OnWelcomeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WelcomeViewModel.IsVisible))
            OnUi(() => ShowOverlay(WelcomeOverlay, _welcome?.IsVisible == true, animate: true, () => _welcome?.IsVisible == true, fadeIn: true));
    }

    private void OnUi(Action action)
    {
        if (Dispatcher.CheckAccess())
            action();
        else
            Dispatcher.BeginInvoke(action);
    }

    /// <summary>
    /// The splash and the welcome screen show over a blurred <c>Root</c>; hiding one fades it and the blur out together, then
    /// drops both. The blur exists only while an overlay is up (an Effect would make the window render through an offscreen
    /// surface for good): it stays while the other overlay is still up — after the splash the welcome screen takes over and
    /// the two cross-fade over one blur. Screenshot mode never shows the splash, and the welcome screen only with
    /// <c>--welcome</c>. <paramref name="wanted"/> is the state of the view model when a fade ends; a screen shown again
    /// meanwhile is not dropped.
    /// </summary>
    private void ShowOverlay(FrameworkElement overlay, bool visible, bool animate, Func<bool> wanted, bool fadeIn = false)
    {
        overlay.BeginAnimation(OpacityProperty, null);

        if (visible)
        {
            RaiseBlur(animate);
            overlay.Visibility = Visibility.Visible;
            if (animate && fadeIn)
                overlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, OverlayFade));
            else
                overlay.Opacity = 1;
            overlay.Focus(); // keep the keyboard off the controls behind the blur; Tab then goes to the first card
            return;
        }

        if (!animate || overlay.Visibility != Visibility.Visible)
        {
            EndOverlay(overlay);
            return;
        }
        var fade = new DoubleAnimation(0, OverlayFade);
        fade.Completed += (_, _) =>
        {
            if (!wanted())
                EndOverlay(overlay);
        };
        overlay.BeginAnimation(OpacityProperty, fade);
        if (!AnotherOverlayIsUp(overlay))
            (Root.Effect as BlurEffect)?.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(0, OverlayFade));
    }

    private void EndOverlay(FrameworkElement overlay)
    {
        overlay.BeginAnimation(OpacityProperty, null);
        overlay.Visibility = Visibility.Collapsed;
        overlay.Opacity = 1;
        if (!AnotherOverlayIsUp(overlay))
        {
            (Root.Effect as BlurEffect)?.BeginAnimation(BlurEffect.RadiusProperty, null);
            Root.Effect = null;
        }
    }

    /// <summary>The blur of <c>Root</c> at full strength: created, or brought back from a fade-out that is still running.</summary>
    private void RaiseBlur(bool animate)
    {
        if (Root.Effect is BlurEffect blur)
        {
            if (animate)
            {
                blur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(BlurRadius, OverlayFade));
            }
            else
            {
                blur.BeginAnimation(BlurEffect.RadiusProperty, null);
                blur.Radius = BlurRadius;
            }
            return;
        }
        Root.Effect = new BlurEffect { Radius = BlurRadius, RenderingBias = RenderingBias.Performance };
    }

    private bool AnotherOverlayIsUp(FrameworkElement overlay)
        => (!ReferenceEquals(overlay, SplashOverlay) && SplashOverlay.Visibility == Visibility.Visible) ||
           (!ReferenceEquals(overlay, AccountIntroOverlay) && AccountIntroOverlay.Visibility == Visibility.Visible) ||
           (!ReferenceEquals(overlay, WelcomeOverlay) && WelcomeOverlay.Visibility == Visibility.Visible);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
