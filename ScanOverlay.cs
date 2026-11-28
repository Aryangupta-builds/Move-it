using Microsoft.UI.Xaml;

namespace MoveIt;

// Smart-Switch-style full-page feedback while this laptop scans its own data for a complete migration.
// The percentage is REAL: finished scan folders divided by all scan folders. Nothing is copied or changed.
public sealed partial class MainWindow
{
    CancellationTokenSource? migScanCts;

    // pct == null: no percentage box (the "Connected to ..." step, or a step without a measurable percentage)
    void ShowScanOverlay(string title, string status, int? pct, bool canStop)
    {
        ScanOvTitle.Text = title; ScanOvStatus.Text = status;
        ScanOvPctBox.Visibility = pct == null ? Visibility.Collapsed : Visibility.Visible;
        if (pct != null) ScanOvPct.Text = pct.Value.ToString();
        ScanOvStop.Visibility = canStop ? Visibility.Visible : Visibility.Collapsed;
        ScanOvHint.Visibility = canStop ? Visibility.Visible : Visibility.Collapsed;
        ScanOverlay.Visibility = Visibility.Visible;
    }

    void SetScanPercent(int pct)
    {
        if (ScanOverlay.Visibility != Visibility.Visible) return;
        ScanOvPctBox.Visibility = Visibility.Visible; ScanOvPct.Text = Math.Clamp(pct, 0, 100).ToString();
    }

    void HideScanOverlay() => ScanOverlay.Visibility = Visibility.Collapsed;

    void ScanStop_Click(object s, RoutedEventArgs e) { ScanOvStatus.Text = "Stopping..."; migScanCts?.Cancel(); }
}
