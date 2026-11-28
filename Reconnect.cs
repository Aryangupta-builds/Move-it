namespace MoveIt;

// Spec 11/12: after a send failed because the connection dropped (Wi-Fi or wired), watch for the other laptop to answer again for up to 3 minutes.
// It only DETECTS and tells the user; it never restarts the transfer by itself. The user presses "Resume interrupted transfer" (verified files are not sent again).
public sealed partial class MainWindow
{
    CancellationTokenSource? reconCts;

    void StartReconnectWatch()
    {
        if (PendingStore.Load() == null || targetIp == "") return;
        reconCts?.Cancel(); reconCts = new(); var tok = reconCts.Token;
        string ip = targetIp, code = targetCode, name = targetName;
        _ = Task.Run(async () =>
        {
            var end = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < end && !tok.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(6000, tok);
                    using var to = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(to.Token, tok);
                    await Sender.Probe(ip, code, linked.Token);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (Sending) return;
                        TxResDetails.Text += $"\n\nThe connection to {name} is back ({ActiveLink(ip)}). Go to the Send page and press \"Resume interrupted transfer\". Files that were already verified are not sent again.";
                    });
                    return;
                }
                catch (OperationCanceledException) when (tok.IsCancellationRequested) { return; }
                catch { }
            }
        });
    }
}
