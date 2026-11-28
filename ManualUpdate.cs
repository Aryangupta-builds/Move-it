using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace MoveIt;

// Spec 2G: manual update workflow for people who do not want the in-app download.
// 1) open the official releases page in the browser, 2) install a MOVE-IT-Setup.exe they downloaded themselves.
// If the matching MOVE-IT-Setup.exe.sha256 sits next to it, the file is verified; otherwise the user must confirm an unverified install explicitly.
public sealed partial class MainWindow
{
    static string? ReleasesPage(string updateUrl)
    {
        var m = Regex.Match(updateUrl ?? "", @"^https://api\.github\.com/repos/([^/]+)/([^/]+)/", RegexOptions.IgnoreCase);
        return m.Success ? $"https://github.com/{m.Groups[1].Value}/{m.Groups[2].Value}/releases" : null;
    }

    async void UpdateOpenPage_Click(object s, RoutedEventArgs e)
    {
        var url = ReleasesPage(cfg.UpdateUrl);
        if (url == null) { await Msg("No releases page", "The update source is not a GitHub repository, so MoveIt cannot guess the download page. Open your official download page yourself."); return; }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        UpdateText.Text = "Opened the releases page. Download MOVE-IT-Setup.exe (and MOVE-IT-Setup.exe.sha256 if you want it checked), then use \"Install from a downloaded file\".";
    }

    async void UpdateFromFile_Click(object s, RoutedEventArgs e)
    {
        if (rxReceiving || Sending) { await Msg("Transfer in progress", "Finish or cancel the transfer before updating."); return; }
        var p = Init(new FileOpenPicker()); p.FileTypeFilter.Add(".exe");
        var f = await p.PickSingleFileAsync(); if (f == null) return;
        try
        {
            string path = f.Path, note = "";
            var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            string shaFile = path + ".sha256";
            string actual = await Task.Run(() => { using var fs = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(fs)); });
            if (File.Exists(shaFile))
            {
                var want = (await File.ReadAllTextAsync(shaFile)).Trim().Split(' ', '\t', '\r', '\n')[0];
                if (!want.Equals(actual, StringComparison.OrdinalIgnoreCase))
                { await Msg("Checksum does not match", "This file is different from the published checksum. It may be damaged or changed. MoveIt will not run it."); UpdateText.Text = "Manual update stopped: checksum mismatch."; return; }
                note = "Checksum matches the .sha256 file next to it.";
            }
            else
            {
                note = "No .sha256 file was found next to it, so this file is NOT verified. Only continue if you downloaded it yourself from the official releases page.\nSHA-256: " + actual;
                if (!await Confirm("Install without verification?", note, "Install anyway", "Cancel")) return;
            }
            if (!await Confirm("Run this installer?", $"{Path.GetFileName(path)}\n{(string.IsNullOrWhiteSpace(vi.ProductVersion) ? "Version not shown in the file" : "File version: " + vi.ProductVersion)}\n{note}\n\nMoveIt closes while it updates. Settings and history are kept. Windows may show its own security prompt (the installer is not code-signed).", "Install now", "Not now")) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            closeConfirmed = true; Close();
        }
        catch (Exception ex) { UpdateText.Text = "Manual update failed: " + ex.Message; }
    }
}
