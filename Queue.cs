using System.Text.Json;

namespace MoveIt;

public enum QStatus { Pending, Sending, Done, Skipped, Failed, Cancelled }

public sealed class QueueItem
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Path { get; init; } = ""; public bool Folder { get; init; }
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Path;
    public string Type => Folder ? "Folder" : AppInfo.FormatOf(Path);
    public long Size { get; set; }
    public QStatus Status { get; set; } = QStatus.Pending;
    public int Progress { get; set; }
    public string Error { get; set; } = "";
}

// Each item is sent as its own session, so the queue can pause/resume/skip BETWEEN items. A file that is already being sent cannot be paused (the transfer engine cannot do that).
public sealed class TransferQueue
{
    public List<QueueItem> Items { get; } = new();
    readonly object gate = new(); TaskCompletionSource<bool>? paused;
    public bool IsPaused { get { lock (gate) return paused != null; } }

    public QueueItem Add(string path, bool folder)
    {
        long size = 0; if (!folder) { try { size = new FileInfo(path).Length; } catch { } }
        var q = new QueueItem { Path = path, Folder = folder, Size = size }; Items.Add(q); return q;
    }
    public bool Remove(Guid id) { var q = Items.FirstOrDefault(i => i.Id == id); return q != null && q.Status != QStatus.Sending && Items.Remove(q); }
    public void Move(Guid id, int delta)
    {
        int i = Items.FindIndex(x => x.Id == id), j = i + delta;
        if (i < 0 || j < 0 || j >= Items.Count || Items[i].Status != QStatus.Pending) return;
        (Items[i], Items[j]) = (Items[j], Items[i]);
    }
    public void Skip(Guid id) { var q = Items.FirstOrDefault(i => i.Id == id); if (q != null && q.Status == QStatus.Pending) q.Status = QStatus.Skipped; }
    public void Pause() { lock (gate) paused ??= new(TaskCreationOptions.RunContinuationsAsynchronously); }
    public void Resume() { lock (gate) { paused?.TrySetResult(true); paused = null; } }
    Task WaitIfPaused(CancellationToken ct) { Task? t; lock (gate) t = paused?.Task; return t == null ? Task.CompletedTask : t.WaitAsync(ct); }
    public int Completed => Items.Count(i => i.Status == QStatus.Done);
    public int Pending => Items.Count(i => i.Status == QStatus.Pending);

    // onChange is called after every status/progress change (use it to refresh the list; it runs on the caller's context).
    public async Task RunAsync(string host, string code, string deviceName, Action onChange, CancellationToken ct)
    {
        while (true)   // pick the next pending item each time, so reordering during a run takes effect
        {
            await WaitIfPaused(ct);
            if (ct.IsCancellationRequested) break;
            var q = Items.FirstOrDefault(i => i.Status == QStatus.Pending);
            if (q == null) break;
            q.Status = QStatus.Sending; onChange();
            try
            {
                var files = await Task.Run(() => q.Folder ? Sender.Walk(q.Path, q.Name).ToList() : new List<(string, string)> { (q.Path, q.Name) }, ct);   // off the UI thread
                var rep = await Sender.Send(host, code, deviceName, files, new Progress<Prog>(p => { q.Progress = p.Total == 0 ? 100 : (int)(100.0 * p.Done / p.Total); onChange(); }), ct);
                q.Status = rep.Clean ? QStatus.Done : QStatus.Failed;   // partly sent = not "Done"
                if (!rep.Clean) q.Error = string.Join("; ", rep.Problems.Take(3));
                if (rep.Clean) q.Progress = 100;
            }
            catch (OperationCanceledException) { q.Status = QStatus.Cancelled; break; }
            catch (RemoteCancelledException) { q.Status = QStatus.Cancelled; q.Error = "Cancelled by the other laptop"; break; }
            catch (Exception ex) { q.Status = QStatus.Failed; q.Error = ex.Message; }
            onChange();
        }
        foreach (var q in Items) if (q.Status == QStatus.Pending && ct.IsCancellationRequested) q.Status = QStatus.Cancelled;
        onChange();
    }
}
