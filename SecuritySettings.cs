using Microsoft.UI.Xaml;

namespace MoveIt;

// Spec 26: user control over the plain-TCP fallback. Default stays ON so an older MoveIt still works; the result screen always says encrypted or NOT encrypted.
public sealed partial class MainWindow
{
    void Plain_Toggled(object s, RoutedEventArgs e)
    {
        if (loadingSettings) return;
        cfg.AllowPlain = PlainSwitch.IsOn; Net.AllowPlainFallback = cfg.AllowPlain; SaveSettings();
    }
}
