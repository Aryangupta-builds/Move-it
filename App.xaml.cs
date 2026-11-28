using Microsoft.UI.Xaml;
namespace MoveIt;
public partial class App : Application
{
    Window? w;
    public App() { InitializeComponent(); }
    protected override void OnLaunched(LaunchActivatedEventArgs args) { w = new MainWindow(); w.Activate(); }
}
