using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace MoveIt;

// Spec 7: per-file selection with name/type search and size filter. The ListView virtualizes large lists.
public sealed partial class MainWindow
{
    sealed record FileRow(string Source, string Rel, long Size)
    {
        public override string ToString() => $"{Rel}  ({AppInfo.Fmt(Size)})";
    }

    async Task<(List<(string p, string rel)> Files, long Bytes)?> ChooseFiles(List<(string p, string rel)> files, List<string> emptyDirs)
    {
        if (dialogOpen) return null;
        var rows = await Task.Run(() => files.Select(f => { long n = 0; try { n = new FileInfo(f.p).Length; } catch { } return new FileRow(f.p, f.rel, n); }).ToList());
        var excluded = new HashSet<FileRow>(); bool loading = false;
        var search = new TextBox { PlaceholderText = "Search name, folder or type (.pdf)" };
        var minMb = new NumberBox { Header = "Show files of at least (MB)", Value = 0, Minimum = 0 };
        var list = new ListView { SelectionMode = ListViewSelectionMode.Multiple, Height = 260 };
        var total = new TextBlock();
        List<FileRow> shown = new();
        void Total() => total.Text = $"Selected: {rows.Count - excluded.Count} of {rows.Count}, {Fmt(rows.Where(r => !excluded.Contains(r)).Sum(r => r.Size))}";
        void Show()
        {
            loading = true;
            double min = double.IsNaN(minMb.Value) ? 0 : minMb.Value * 1024 * 1024;
            shown = rows.Where(r => r.Size >= min && r.Rel.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToList();
            list.ItemsSource = shown;
            if (shown.Count > 0) list.SelectRange(new ItemIndexRange(0, (uint)shown.Count));
            for (int i = 0; i < shown.Count; i++) if (excluded.Contains(shown[i])) list.DeselectRange(new ItemIndexRange(i, 1));
            loading = false; Total();
        }
        list.SelectionChanged += (_, e) =>
        {
            if (loading) return;
            foreach (FileRow r in e.AddedItems) excluded.Remove(r);
            foreach (FileRow r in e.RemovedItems) excluded.Add(r);
            Total();
        };
        search.TextChanged += (_, _) => Show(); minMb.ValueChanged += (_, _) => Show();
        var all = new Button { Content = "Select shown" }; all.Click += (_, _) => { excluded.ExceptWith(shown); Show(); };
        var none = new Button { Content = "Deselect shown" }; none.Click += (_, _) => { excluded.UnionWith(shown); Show(); };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 }; bar.Children.Add(all); bar.Children.Add(none);
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(search); panel.Children.Add(minMb); panel.Children.Add(bar); panel.Children.Add(total); panel.Children.Add(list);
        // Collapsible categories (first two destination folders) with their size and selection controls.
        static string Key(FileRow r) => string.Join('/', r.Rel.Split('/').SkipLast(1).Take(2));
        int at = 3;
        foreach (var g in rows.GroupBy(Key))
        {
            var members = g.ToList(); var prefix = g.Key;
            var pick = new Button { Content = "Select category" }; pick.Click += (_, _) => { excluded.ExceptWith(members); Show(); };
            var drop = new Button { Content = "Deselect category" }; drop.Click += (_, _) => { excluded.UnionWith(members); Show(); };
            var view = new Button { Content = "Show these files" }; view.Click += (_, _) => search.Text = prefix.Length == 0 ? "" : prefix + "/";
            var body = new StackPanel { Spacing = 6 }; body.Children.Add(pick); body.Children.Add(drop); body.Children.Add(view);
            panel.Children.Insert(at++, new Expander { Header = $"{(prefix.Length == 0 ? "Other" : prefix)} ({members.Count}, {Fmt(members.Sum(r => r.Size))})", Content = body, HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch });
        }
        if (emptyDirs.Count > 0)
            panel.Children.Add(new Expander { Header = $"EMPTY FOLDERS only ({emptyDirs.Count})", Content = new TextBlock { Text = Some(emptyDirs) + "\nYou choose whether to include them in the next step.", TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 239, 68, 68)) } });
        var dups = new Button { Content = "Find duplicate files" }; bar.Children.Add(dups);
        dups.Click += async (_, _) =>
        {
            dups.IsEnabled = false;
            var groups = await Task.Run(() => Duplicates.Find(rows.Select(r => r.Source), CancellationToken.None));
            dups.Content = $"{groups.Count} duplicate group(s)";
            int pos = panel.Children.IndexOf(list);
            foreach (var g in groups.Take(50))
            {
                var extra = Duplicates.Extras(new[] { g });
                var copies = rows.Where(r => extra.Contains(r.Source)).ToList();
                if (copies.Count == 0) continue;
                var skip = new Button { Content = "Skip these copies (keep one)" }; skip.Click += (_, _) => { excluded.UnionWith(copies); Show(); };
                var restore = new Button { Content = "Keep all" }; restore.Click += (_, _) => { excluded.ExceptWith(copies); Show(); };
                var box = new StackPanel { Spacing = 6 };
                box.Children.Add(new TextBlock { Text = Some(copies.Select(c => c.Rel)), TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap });
                box.Children.Add(skip); box.Children.Add(restore);
                panel.Children.Insert(pos++, new Expander { Header = $"Duplicate copies: {copies[0].Rel}", Content = box });
            }
        };
        var dlg = new ContentDialog { Title = "Choose files", Content = new ScrollViewer { Content = panel, MaxHeight = 460 }, PrimaryButtonText = "Use selection", CloseButtonText = "Cancel migration", XamlRoot = Content.XamlRoot, RequestedTheme = RootGrid.RequestedTheme };
        dlg.Opened += (_, _) => Show();
        dialogOpen = true;
        try { if (await dlg.ShowAsync() != ContentDialogResult.Primary) return null; }
        finally { dialogOpen = false; }
        var keep = rows.Where(r => !excluded.Contains(r)).ToList();
        return (keep.Select(r => (r.Source, r.Rel)).ToList(), keep.Sum(r => r.Size));
    }
}
