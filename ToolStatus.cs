namespace MoveIt;

// Spec 25: small shared progress strip (MainWindow.xaml "ToolStatusBar"). Safe to call from any thread.
public sealed partial class MainWindow
{
    void ShowToolStatus(string text, double? percent = null)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ToolStatusText.Text = text;
            ToolStatusProgress.IsIndeterminate = percent == null;
            if (percent != null) ToolStatusProgress.Value = Math.Clamp(percent.Value, 0, 100);
            ToolStatusBar.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        });
    }
    void HideToolStatus() => DispatcherQueue.TryEnqueue(() => ToolStatusBar.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed);

    // Runs long work off the UI thread and shows the strip meanwhile (indeterminate: these steps do not report progress).
    async Task<T> WithStatus<T>(string text, Func<T> work)
    {
        ShowToolStatus(text);
        try { return await Task.Run(work); } finally { HideToolStatus(); }
    }
}
