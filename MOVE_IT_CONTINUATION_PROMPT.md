# MOVE IT continuation prompt (WIP64)

## STANDING DECISIONS FROM THE USER (already decided, NEVER ask again, never suggest otherwise)
1. WHY: the user's laptop is old and weak. MOVE IT exists to move ALL data from the old laptop to the new one, completely. No existing app does a complete migration, so the user builds it himself. Keep that goal in every decision.
2. NO COMPILE / NO GITHUB PUSH until ALL coding for the 31 spec sections is finished. Do not ask "shall we push/compile now?", do not remind him, do not offer it as a next step until every section is at least "code written". Only when the user himself says the coding is complete: then he pushes to GitHub, Actions builds, errors get fixed, then testing (QA_SPEC29.md, plus the in-app self-check). The USER does the testing himself; Claude does not test.
3. ORDER: finish all PARTIAL sections first (2, 5, 9, 11, 12, 20, 22, 26, 27), THEN bugs/compile errors.
4. UPDATES: the GitHub repo link (Aryangupta-builds/Move-it releases) is already set as the update source. Do not ask for an update URL. Two ways exist: automatic check + verified download (Updates.cs), and MANUAL: "Open releases page" + "Install from a downloaded file" (ManualUpdate.cs). Release publishing in build.yml is approved (runs only on a v* tag or a manual run with publish ticked).
6. (2026-11-28) All sections are now coded. Next phase = code OPTIMIZATION (see section 60), then compile/fix errors when the user says so.
5. Replies in simple Hinglish, show the section 16 table at the end of every reply, do not claim "done" unless compiled AND tested, ask at most one question per reply and only if truly blocking.
Continue this project; do not rebuild it. Read this file, CONTINUE_PROMPT.md (history) and all source. Reply in simple Hinglish; code/UI text in English. The full spec (31 sections) is a separate document: ask the user to attach it again.
**Nothing from WIP20-WIP22 has been compiled or run.** User's decision (2026-11-28): finish all spec sections first, then push to GitHub and fix Actions errors. This overrides step 1 in section 12.

**LATEST STATE (WIP65): read sections 59-62 at the bottom first.** All 29 coded sections are now "code written, test pending"; the user tests himself. Next phase (user decision 2026-11-28): CODE OPTIMIZATION, then compile via GitHub Actions and fix errors. Version stays 1.4.0.

## 1-3. Overview / architecture / version
Windows laptop-to-laptop transfer. WinUI 3, .NET 8, C#, unpackaged, Inno Setup installer. Version 1.3.0 (unchanged). TCP 50505 (transfer + probe), UDP 50506 discovery, WebShare.cs phone upload on 50507. User builds only via GitHub Actions (.github/workflows/build.yml; artifact MOVE-IT-Setup). Release publishing: approved by the user (see STANDING DECISIONS and section 43). Pairing = 6-digit code, plain TCP (no encryption yet).

## 4. Done in WIP20 (written, not compiled): logo/installer/build.yml, Startup.cs toggle, single instance mutex, close guard, Tray.cs (optional), Updates.cs (manifest + SHA-256), ParticleFlow Send/Receive/Cancel, phone-page particles, reduced motion. See CONTINUE_PROMPT.md.

## 4b. Done in WIP21 (written, not compiled)
**Transfer.cs (protocol v2, backward compatible: both sides advertise "v":2; old peers fall back)**
- Per-file SHA-256: sender sends {t:"sum"} after each file; receiver verifies, deletes the file on mismatch.
- Sender.Send now returns SendReport (Sent, Unreadable, Changed, PeerSkipped, PeerFailed, EmptyFolders, Checked, PeerBattery, PeerFree, Problems). Unreadable files are reported, no longer silently skipped. Receiver's final ack carries saved/skipped/failed.
- File shrinking mid-send: chunk length -2 = "abandon this file", receiver drops it and continues (was zero-padded success).
- Empty folders: {t:"dir"} messages (only when user chose to include them).
- Probe connection (mode:"probe"): returns name, OS, battery %, charging, hasBattery, free space, busy. Also ping (latency) and up/down speed test (payload capped 64 MB, nothing written to disk). Sender.Probe / Sender.SpeedTest. Receiver reports free space of the staging drive (system drive), not the final folder.
**New files**: Battery.cs (GetSystemPowerStatus, MigrationGate: block <20 % on either laptop, unknown battery => user must confirm, no battery => OK), Migration.cs (Sensitive blocklist, ScanRoot/Scanner with empty-folder detection, MigrationCatalog: personal folders, Chrome/Edge Bookmarks only, My Games/Saved Games, VS Code + Windows Terminal settings; SoftwareInventory + Compare), Duplicates.cs (size then SHA-256), Backup.cs (manifest, verify, restore), Queue.cs (TransferQueue, pause/skip/reorder BETWEEN items), Schedule.cs (store + preflight), FolderSync.cs (plan/apply/conflict/state engine), Connections.cs (adapter detection, Recommend, Diagnostics.Classify), Report.cs (text report in LocalAppData\MoveIt\Reports).
**MainWindow wiring (Send flow, "Complete migration" mode)**: new checkboxes CbBrowser, CbGames, CbDev, CbSkipDup. Send_Click -> PlanMigration: Probe (also validates code) -> battery gate -> free-space check -> read-only scan -> question "migrate empty folders?" -> review dialog (EMPTY FOLDER lines in red, excluded categories, totals) -> send. Result screen is honest: "Completed with problems" + list when anything was not sent; history status "Partial"; report file saved. InstallApps_Click now asks first and shows an installed-app comparison.
Modified: Transfer.cs, MainWindow.xaml, MainWindow.xaml.cs, Models.cs (Partial colour). New: the 9 files above. Old Sender.Walk is still used for single files/folders mode.

## 5. Partially done
- Migration Center: logic + review dialog only; no dedicated page, no side-by-side old/new laptop dashboard (OS, storage shown only in dialog), no per-file selection/search/filters/expand-collapse.
- Duplicates: source-side only. Destination conflicts: receiver always keeps both ("name (1).ext"), never overwrites. Replace / Review manually not built.
- Software: compare only (Already installed / Older version here / Reinstall required / System). Incompatible, Manual setup, Update-to-latest need winget lookup: not built.
- Receive overlay: still lacks completed/remaining file counts and error list.

## 6. Engine-only (NO UI, NOT wired, untested): Queue, Schedule, FolderSync, Backup/Rollback, Connections/Diagnostics/Smart selection, SpeedTest.
## 7. Not started: Clipboard Sharing, Account Setup page (only the never-copy blocklist exists), Battery monitoring DURING transfer, Wi-Fi reconnect/resume, interrupted-transfer manifest/resume, encryption, history/reports UI, Folder Sync consent screen + transport + sync history, scheduled-job timer/notification, UI redesign, start-hidden-to-tray at startup, official update URL + update.json in build.yml.

## 8. Decisions
- Never overwrite on receive. Never delete source data. Closing window exits app unless tray enabled. No fake Pause (engine cannot pause mid-file); queue pauses only between items.
- Wired USB works only if Windows exposes a USB network adapter; charging cable never listed. Bluetooth detected only.
- Scheduled jobs run only while app is open, never wake PC, user must type the (per-session) pairing code.
- Sync deletions go to review queue and then to .moveit-sync-trash, never erased.
- OneDrive online-only files (reparse points) are skipped, same as before.

## 9. Known bugs / risks
- Unverified compile: watch Transfer.cs (Receiver.Handle / Probe), MainWindow.xaml.cs PlanMigration, XAML checkboxes.
- Receiver counts `skipped` for files it could not write; sender cannot tell which. Old peers: no checksums, no failure report.
- Probe uses 8 s timeout; speed test not exposed in UI.
- Per-user install across admin accounts untested. Installer not signed. No encryption: plain TCP + code.

## 10. Build/installer status: never built since WIP19. Installer (installer.iss) written, untested.
## 11. Folder Sync status: engine only, default-off by design (no UI exists, so cannot activate).
## 12. Next steps (in order)
1. Push, fix Actions build errors. 2. Test on 2 laptops: normal send, cancel, migration review, battery gate (try mock by lowering MigrationGate.Minimum), empty folders, checksum (flip a byte to test), shrink-mid-send. 3. Installer tests. 4. Migration Center page + selection UI. 5. Queue UI, Diagnostics/Speed-test UI (use Sender.Probe/SpeedTest, Connections, Diagnostics). 6. Duplicate conflict actions on destination, backup before replace. 7. Folder Sync UI with consent (default No), then transport. 8. Clipboard, Schedule UI, accounts page, history/report UI. 9. Resume/manifest, encryption.
## 13. Limits: no mobile expansion; never copy passwords/tokens; do not claim features done until compiled AND tested.


## 14. WIP22 (written, not compiled)
New NetCheck.cs (partial MainWindow): "Test connection speed" button on Send page (latency, jitter, timeouts, up/down, active adapter, 1 GB estimate; 60 s timeout, no cancel button) and "Open reports" button on History page. Modified: MainWindow.xaml.

## 15. WIP23 (written, not compiled)
New Tools.cs + "Migration tools" card in Settings: Account setup (8; statuses Ready/Setup Required + self-ticked, no Unsupported state), manual Backup/Restore of chosen files (15; not auto-run before migration), connection overview (12, 21; no manual method selection).
User decision: no testing until all 31 sections are done.

## 16. Spec status (31 sections) - keep this table up to date
RULE for every AI session: update this table before handoff and show the same table (in simple Hindi/Hinglish) at the end of every reply. Keep this exact format. Every section appears in exactly one row, row counts add up to 31. "Complete" means compiled AND tested.

| Status | Count | Sections |
|---|---|---|
| Tested and complete | 0 | none |
| Code written, test pending | 29 | 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29 |
| Partial | 0 | none |
| Logic only, no UI | 0 | none |
| Not started | 0 | none |
| Handoff / rules | 2 | 30, 31 |

## 17. WIP24 (written, not compiled)
QueueUi.cs: queue dialog (13). Queue.cs now picks the next pending item each loop. Receive screen shows file index/count (4; error list still missing).
Queue gaps: no history/report entry, no app-close guard, folder enumeration runs on UI thread, Send page items not cleared. Verify the receiver accepts back-to-back sessions with the same code.

WIP25: ScheduleUi.cs (19): add/run/reschedule/cancel, 30 s due timer. Gaps: pickers offer no file selection of their own (uses Send list); jobs end at "Loaded to Send list" (no Done state); dialog reachable only from the Send page.

## 18. Next
Folder Sync dashboard/transport (20); receive error list (4); selection UI (5, 7); queue/schedule gaps; resume/encryption (22, 26); UI pass (25).

## 19. WIP26 (written, not compiled)
ClipboardUi.cs (18, partial): manual send of clipboard text as a .txt through Sender.Send; receiver copies it via Settings > Copy received text. Gaps: no direct clipboard delivery, session toggle/Stop Sharing, dedicated notification; no encryption; received file stays on disk.

## 20. WIP27 (written, not compiled)
SyncUi.cs (20, partial): consent, folder pair, mode, preview, approved deletions to trash; works on folders this PC can reach (Windows share), no MoveIt network transport. Fixed: unchanged files are now saved in sync state. Gaps: dashboard, saved pairs, auto sync, pause/stop, history, conflict resolution UI (FolderSync.Resolve unused), per-file deletion choice.

## 21. WIP28 (written, not compiled)
Version bumped to 1.4.0 (csproj, installer.iss, Models.cs; overrides section 1-3). Close guard blocks exit while the queue runs. QA_CHECKLIST.md extended (29: checklist only, no tests run). Section 16 status unchanged. Next: compile via Actions, then receive error list (4), selection UI (5, 7), history for queue, UI pass (25).

## 22. WIP29 (written, not compiled)
SelectUi.cs (7, partial): optional per-file selection in complete migration, with name/type search, minimum size filter, select/deselect shown. Gaps: no category expand/collapse or empty-folder filter. Verify ListView.SelectRange behaviour and large lists. Section 16 table unchanged.

## 23. WIP30 (written, not compiled)
Receive result lists files not saved; history is "Partial" instead of "Completed" (4, 23, 24). Static check passed: XAML handlers exist, no duplicate x:Name or identical MainWindow method signatures. This is not a compile. Check icon/colour on a partial receive. Section 16 table unchanged.

## 24. WIP31 (written, not compiled)
Queue now writes a history entry (13, 24; counts items, folder sizes excluded, failed items with partial data count as not done). Scheduled jobs become "Done" after a clean send of all their paths (19). Remaining queue gap: detailed report, folder enumeration on UI thread. Section 16 table unchanged.

## 25. WIP32 (written, not compiled)
BatteryWatch.cs (10): inline low-battery warning on Send and Receive screens, local battery only, never cancels; queue dialog not covered. Queue folder enumeration moved off the UI thread (27). Section 16 table unchanged.

## 26. WIP33 (written, not compiled)
User priority: finish all partial sections first, then fix bugs. Closed code gaps for 4 (file size, remaining counts, details kept after cancel/failure), 8 (statuses), 10 (battery recheck before start), 16 (reachability, packet loss shown as Not available), 28 (version 1.4.0). All still need compile and test. Section 2 needs the official update URL from the user.

## 27. WIP34 (written, not compiled)
19: review + confirm step, folder sizes and read errors in preflight, no rerun of Done/Cancelled. 13: Down button (large-queue virtualization still missing). 24: folder sync history text file. 23: checklist text on migration result.

## 28. WIP35 (written, not compiled)
13: 50-item pages, folder sizes. 24: reports for receive, queue and cancelled/failed sends (ReportExtra.cs). Next: 7 (empty-folder filter, category expand/collapse), 23, 11, 12.

## 29. WIP36 (written, not compiled)
7: category expanders, empty-folder view, Clear all. 11/12/21: active adapter shown during send and in the report. Still missing: 11 reconnect/resume (depends on 22); 12 USB detection statuses and disconnect recovery.

## 30. WIP37 (written, not compiled)
18: off-by-default session switch, receive notice with sender name (delivery stays file-based, unencrypted). 23: migration summary and checklist from sender-side data. Next: 9, 15, 14.

## 31. WIP38 (written, not compiled)
15: verified backup offered before Folder Sync replaces files. 20: conflict choices (first 20; shown after state save, so verify the next run). 9: winget update list and confirmed single-app update; incompatible status still missing.

## 32. WIP39 (written, not compiled)
21: manual adapter selection by binding the outgoing source address (verify routing on real hardware). 5: dashboard dialog (dedicated Migration Center page still missing). Next: 14, 12, 22.

## 33. WIP40 (written, not compiled)
14: destination conflict choice on save (keep both / skip identical / replace with verified backup); duplicate groups in the selection dialog. Cancelled transfers still keep both. Next: 12, 5, 9, then 22 and 26.

## 34. WIP41 (written, not compiled)
User priority (2026-11-28): finish all PARTIAL sections first (2, 5, 9, 11, 12, 20, 22, 26, 27), bugs after.
12 (partial): WiredStatus.cs = USB Device Detected / Compatible Transfer Interface Found / Driver Required (pnputil /problem, no WMI) / Connected (only after real probe + same subnet) / Connection Failed / "No supported wired link" (Windows cannot tell if a plain cable is plugged in). Shown in Settings > Connections. Inline "Wired connection lost" warning during a wired send (BatteryWatch.cs). Still missing: auto-resume after cable reconnect (needs 22), UI step "Establishing Connection". Section 16 table unchanged (12 stays Partial).
Next in order: 5 (Migration Center page), 9 (Incompatible status), 11 (reconnect/resume), then 22, 26, 2, 20, 27.

## 35. WIP42 (written, not compiled)
5 (partial): dedicated Migration Center page (MigrationPage in MainWindow.xaml, logic in DashboardUi.cs): readiness, this/other laptop side by side (OS, free space, battery), per-folder detected data, active connection. Opened from the Send page button "Migration Center"; Back returns to Send. Connection methods list added to the page. Still missing in 5: software/game/dev category sizes, estimated size of the current selection. Section 16 table unchanged (5 stays Partial). Check: MigrationPage added to the page lists in ShowPage and Fx.Fade (static check only).

## 36. WIP43 (written, not compiled)
9 (partial): AppStatus now has Incompatible (only rule decided offline: "x64"/"64-bit" app on 32-bit Windows) and ManualSetup. New AppSourceUi.cs + Settings button "Check apps in official source": winget search --exact per not-installed app (max 20, 90 s total, 15 s each): found => official installer available; not found => Manual setup required; winget silent => Not available. Verify the "No package found" text and exit code 0x8A150014 on a real PC (winget output wording can differ by version/language). Still missing in 9: the status table is shown as a text dialog, not a dedicated Software page; per-app Incompatible beyond the 32-bit rule needs the old laptop's architecture (not sent). Section 16 table unchanged (9 stays Partial).

## 37. WIP44 (written, not compiled)
22 (partial) + 11 (partial): resume after an interruption. New Pending.cs: manifest (paths + sizes only, never contents) in LocalAppData\MoveIt\pending_transfer.json, written when a send starts, deleted only after a CLEAN send; kept after cancel/failure/partial. Button "Resume interrupted transfer" on the Send page: Sender.FindVerified asks the receiver (new mode "have" in Receiver.Handle/Have: same-size files in the save folder, SHA-256 on both sides, read-only), verified files are not sent again, the rest goes through the normal Send_Click (resumeFiles/resumeDirs fields). Needs: receiver on the Receive screen with the same code, same save folder. Limits: no auto-reconnect (user presses Resume), no mid-file resume (the unfinished file restarts), manifest is one slot (a new send replaces it), old peers (no "have" mode) get "does not support resume". Check: out-var scope edit in Receiver.Handle, Send_Click edits (resuming flag), receiver's Safe(rel, exact) overload. Section 16 table unchanged (11 and 22 stay Partial).

## 38. WIP45 (written, not compiled)
26 (partial): encryption. New Secure.cs: SecureStream (ephemeral ECDH P-256 -> HKDF-SHA256 with the pairing code mixed in -> AES-256-GCM records <= 64 KB, one key per direction, counter nonces) and PrefixStream. Net.OpenAsync (all 4 sender paths: Send, Probe, SpeedTest, FindVerified) tries secure first; an older receiver closes the connection, then it falls back to plain TCP (Net.AllowPlainFallback = true, no UI switch yet). Receiver (Handle) detects the 4-byte magic "MIE1", otherwise serves plain. Wrong code = cannot decrypt = "Wrong pairing code" (Net.RecvFirst). The result screen says "encrypted" or "NOT encrypted". Receiver cancel now uses the stored secure stream (activeStream).
Limits (must be shown honestly): a 6-digit code is not a strong secret: a LAN attacker who actively relays one handshake could guess the code offline; passive sniffing is covered. Fallback can be forced by an attacker (downgrade) while AllowPlainFallback is true. Phone upload (WebShare, HTTP 50507) is NOT encrypted. Not audited. Clipboard/queue/schedule reuse Sender.Send so they are covered. Unknown until compiled: stream use in Receiver.Handle (var (s, secure) deconstruction, scope of `secure`), SendReport.Encrypted initialiser, ECDiffieHellman.DeriveRawSecretAgreement (.NET 7+). Test: normal send, wrong code, old build as peer (fallback), cancel from receiver, resume (mode "have"), speed test. Section 16 table unchanged (26 stays Partial).

## 39. WIP46 (written, not compiled)
20 (partial): Folder Sync dashboard. SyncUi.cs refactored: Sync_Click = consent + pickers + mode, then RunSync(a, b, two) (the old apply flow; returns false when the user backed out) and OfferSavePair. New SyncDash.cs: SyncPairStore (LocalAppData\MoveIt\sync_pairs.json; a pair is saved ONLY if the user says yes after a finished sync), dashboard dialog (Settings > "Folder Sync dashboard"): source/destination, mode, last sync, pending changes (new/modified/renamed/conflicts/deletions), last errors, status Ready/Paused/Disconnected/Error, buttons Sync now / Pause / Resume / Stop sync (removes the pair, deletes nothing) / History (opens folder-sync-history.txt). Optional per-pair 15-minute CHECK (InitSyncWatch, called from ScheduleUi init): it only counts pending changes while MoveIt is open; it NEVER copies, replaces or deletes. So: no automatic sync, no background sync, not real-time. Still missing in 20: "Change folders" (remove and set up again), automatic apply, MoveIt network transport between laptops (only reachable/shared folders), per-file deletion choice, conflict UI only from Sync now. Verify: the Sync_Click text surgery compiles (return false inside RunSync), dialog Hide() from inside content buttons. Section 16 table unchanged (20 stays Partial).

## 40. WIP47 (written, not compiled)
27 (partial): audit + small fixes, NO measurements done. Checked: scans, duplicate search, queue folder sizes, schedule preflight, MoveStaged, reports all already run in Task.Run. Changed: Duplicates.Find now pre-checks first+last 64 KB (Quick) and only fully hashes files that still match; SecureStream uses ArrayPool buffers (no allocation per 64 KB record); sender file stream opened with SequentialScan + Asynchronous and no extra buffer. Not done / unknown: no benchmark of CPU, memory or GB/s with big files or 100k small files; per-file protocol overhead (one JSON msg + one sum msg per file, each its own record) will slow many-small-files transfers; Scanner builds full in-memory lists (memory for millions of files); no parallel hashing; ListView virtualization for very large selection lists untested; Folder Sync Plan hashes on every check. Section 16 table unchanged (27 stays Partial).

## 41. Next steps after WIP47 (in order)
1. Ask the user for the official update URL (section 2) or leave it unset. 2. User pushes to GitHub, Actions builds, fix compile errors (risk spots listed in sections 34-40). 3. Test on 2 laptops: send, cancel, wrong code, resume, encryption fallback with an old build, migration review, battery gate, empty folders, checksum, Folder Sync dashboard, Migration Center page. 4. Then the remaining partial gaps listed at the top. Always show the section 16 table at the end of each reply (simple Hinglish).


## 42. WIP48 (written, not compiled; no dotnet in the sandbox, only a static check: handlers exist, no duplicate x:Name, braces balanced)
User priority again (2026-11-28): finish PARTIAL sections first, bugs after.
26 (partial): Settings toggle "Allow unencrypted connection to older MoveIt versions" (AppSettings.AllowPlain, default ON; SecuritySettings.cs). When OFF the sender refuses plain fallback AND the receiver rejects unencrypted connections (Receiver.Handle). Caption in Settings says phone/tablet uploads (WebShare, HTTP) are NOT encrypted. Still missing: WebShare stays unencrypted (architecture), no audit, 6-digit code is weak against an active LAN attacker.
20 (partial): "Change folders" button in the Folder Sync dashboard (SyncDash.cs): confirm, pick new source + destination, same folder-safety rules as setup, replaces the pair, resets history for it. The new pair has no state, so files that differ on both sides show up as conflicts (FolderSync.Plan: no history = conflict). Still missing: automatic apply, MoveIt network transport between laptops, per-file deletion choice.
5 (partial): Migration Center page now groups detected data by category (Personal files, Browser data, Games, Development data) with sizes, shows the installed-software count, and shows the estimated size of the CURRENT Send-page selection plus whether it fits on the other laptop (estimate: duplicates and empty folders not subtracted). Still missing: per-category list of software sizes (program files are never copied, so no size exists), selection estimate for per-file selection (SelectUi) is not included.
12 (partial): Send flow shows "Establishing Connection..." then "Connected (adapter)" ONLY after a real probe succeeds; if the probe fails the text says no answer yet and the send is still tried (it reports its own error). Still missing: auto-resume after cable reconnect.
2: UpdateUrl already defaults to the GitHub releases API of Aryangupta-builds/Move-it; section 2 still lacks update.json in build.yml (release publishing is not allowed until the user says so). Needs the user's decision.
Check when compiling: DashboardUi.cs (new locals sb/sel/chosen, CheckBox needs Microsoft.UI.Xaml.Controls), MainWindow.xaml.cs Send_Click probe block (cto/link names), Receiver.Handle new early return, SyncDash.cs "change" branch inside the while loop (continue).
Section 16 table unchanged (all five touched sections stay Partial). Next: 9 (dedicated Software page), 11 (auto-reconnect), 22 (mid-file resume), 27 measurements; then compile via Actions.

## 43. WIP48b (written, not compiled): section 2 release publishing
User said yes (2026-11-28) to release publishing. Updates.cs already reads the GitHub "latest release" JSON (assets MOVE-IT-Setup.exe + .sha256), so NO update.json is needed. build.yml now has `permissions: contents: write`, a tag trigger `v*`, a manual input `publish`, and a last step "Publish GitHub Release" (gh release create v<csproj Version>, tag = csproj version). It runs ONLY on a v* tag push or a manual run with publish ticked; normal pushes to main do not release. Not run yet: check the YAML parses and that gh create works with the repo's default token permissions (Settings > Actions > Workflow permissions may need "Read and write"). Installer is unsigned; the release notes say so. Section 2 stays Partial until the installer and update flow are tested.

## 44. WIP48c (written, not compiled): manual update
Settings > Updates has two more buttons (ManualUpdate.cs): "Open releases page (manual download)" (opens https://github.com/<owner>/<repo>/releases derived from the GitHub API update URL) and "Install from a downloaded file" (file picker for .exe; if MOVE-IT-Setup.exe.sha256 is next to it the SHA-256 must match, otherwise the user must explicitly confirm an UNVERIFIED install; blocked during a transfer; closes MoveIt then runs the installer). Section 2 stays Partial (not tested). Section 16 table unchanged.

## 45. WIP49 (written, not compiled; static check only: handlers exist, no duplicate x:Name, brackets balanced except WebShare.cs HTML strings, unchanged from WIP47)
Section 16 table CHANGED (the single source of truth is the table in section 16, now updated): 2, 5, 9, 11, 12, 26 moved from Partial to "Code written, test pending". Reasons and honest limits:
9: SoftwareUi.cs + SoftwarePage (Settings > "Software migration"): list from AppList/installed_apps.txt vs this laptop, filter, search, statuses Already installed / Reinstall required / Update available / Incompatible (only 32-bit Windows rule) / Manual setup required (after the winget check) / System component. "Unsupported" is not a separate state. "Update to latest" stays in AppUpdatesUi (winget). Save list writes a txt in the Reports folder.
11/12: Reconnect.cs: after a failed send, MoveIt probes the other laptop every 6 s for 3 min and tells the user when it answers again. It does NOT restart the transfer: the user presses "Resume interrupted transfer" (WIP44). Bluetooth is detection only. Wired cable disconnect uses the same watcher.
26: plain-TCP switch (WIP48); WebShare (phone) is HTTP and not encrypted by design; the 6-digit code is weak against an active LAN attacker; not audited.
2: update flow = automatic check (GitHub releases) + manual (releases page, install from file) + release step in build.yml.
5: dashboard has per-category sizes and the selection estimate (per-file selection from SelectUi is not included in the estimate; software has no size because programs are never copied).
STILL PARTIAL (do these next, in this order): 27 (needs real measurements, cannot be done without running the app; code ideas: batch small files into one record, stream the Scanner), 22 (mid-file resume: protocol v3 with offset + partial hash; keep backward compatibility), 20 (MoveIt network transport for Folder Sync + optional automatic apply only after consent).
Check when compiling: SoftwareUi.cs (record `with`, Msg/Confirm usage), Reconnect.cs (Probe off the UI thread), XAML SoftwarePage (x:String items in ComboBox), ShowPage lists now include SoftwarePage, GoBackAsync SoftwarePage branch.

## 46. WIP50 (written, not compiled): section 22 mid-file resume
User chose to write the remaining partials even without compile/test. Section 22 moved to "code written, test pending" (table in section 16 updated; sections 20 and 27 stay Partial).
Protocol (backward compatible, both sides must announce it): the receiver's first reply now has "rs":1. A sender that sees it sends big files (>= Net.ResumeMin = 64 MB) with header {t:"file", path, size, mt (last write ticks), rs:1}; the receiver answers {off:N} (bytes it already holds) BEFORE any data; the sender hashes the first N bytes locally (so the final SHA-256 covers the whole file), seeks to N and sends the rest. Old receivers never send rs, old senders never send rs, so nothing changes for them.
Receiver: partial copy is kept in LocalAppData\MoveIt\Partial\<sha256(peer|path|size|mtime)>.part (outside Incoming, so RecoverIncoming does not touch it); kept when the connection drops (IOException) or the app is interrupted; DELETED on a deliberate cancel (either side), on checksum mismatch and when the sender abandons the file; partials older than 7 days are removed at start (Net.CleanPartials). On completion the file is moved to the staging path as before.
The key includes size and mtime: if the file changed on the old laptop the partial is not used. Resume works through the existing "Resume interrupted transfer" button (verified whole files are skipped by mode "have"; the big unfinished file continues from its partial). The device name must be the same (peer is part of the key).
Check when compiling: Receiver.Handle (variables offset/wantRs/resumable, `await Net.SendMsg(... off ...)` placement), Sender.Send reader loop (offWait TaskCompletionSource, `return` inside while in lambda), Net.HashPrefix, Net.PartialPath ([..40] on string).
Test: send a >64 MB file, kill Wi-Fi at ~50%, reconnect, Resume: final file has the right SHA-256 and the sender log does not restart from 0; cancel must delete the partial; an old build as receiver/sender must still work (no rs); partial older than 7 days is removed; two different files with the same name and size do not mix (mtime in the key).
Known limits: partial copy is trusted by length only; the whole-file SHA-256 at the end is the safety net (a bad partial gives "checksum mismatch" and the file is discarded, then must be sent again). Only files >= 64 MB resume mid-file; smaller files restart. Progress after resume counts the already-held bytes as done.
Next (in order): 20 (network transport for Folder Sync), 27 (measurements; ideas: batch small files, stream the Scanner).

## 47. WIP51 (written, not compiled): section 20 network transport (session stopped early on the user's request to hand over)
Done (code only): SyncSide.cs = ISyncSide (LocalSide, RemoteSide), HashingStream, SyncScan (shared snapshot + safe path Resolve), SyncHost (receiver side). Receiver.Handle has a new mode "sync" (after "have") that calls SyncHost.Serve; it answers ONLY inside SyncHost.Root, which is null until the user on THAT laptop taps Settings > "Allow Folder Sync on this laptop" (one folder, this app session only). Protocol (inside the normal encrypted MoveIt connection + pairing code): list (paged 2000), stat, hash, get (bytes + sha trailer), put (bytes + sha trailer, verified, tmp then move, mtime set), rename, archive, trash (never erase), bye. Paths are validated (no .., no drive, no sensitive files, no trash dir).
FolderSync.cs: new PlanAsync / ApplyAsync (keepOldVersions copies the old file into .moveit-sync-trash before replacing; deletions only for approvedDeletes) / ResolveAsync (source | destination | both = destination version kept as "name (conflict copy yyyyMMdd-HHmmss).ext"). Old Plan/Apply (local only) still exist and are still used by the dashboard and RunSync.
SyncNetUi.cs: Settings buttons "Folder Sync with the other laptop" (scan, code, local folder, one-way default; manual only; session only, remote pairs are NOT saved yet) and "Allow Folder Sync on this laptop"; PickDeletions (per-file tick list, nothing ticked by default, used by local AND remote sync); ConflictChoice (Keep source / destination / both / Review manually + apply to remaining). Sync state is saved AFTER conflicts, so resolved files are remembered (fixes the old "verify next run" note).
NOT done in 20: (a) saved remote pairs on the dashboard (needs SyncPair.Remote fields; the pairing code must never be stored, ask each session); (b) optional automatic apply with explicit consent (idea: SyncPair.AutoApply + consent dialog; timer in InitSyncWatch applies only New/Modified with keepOldVersions=true, never deletions or conflicts, local pairs, or remote only if the code was entered this session); (c) local RunSync (SyncUi.cs) still uses the old 2-button conflict dialog: switch it to ConflictChoice + ResolveAsync with LocalSide; (d) progress UI during a network sync (ApplyAsync has a progress callback, unused); (e) mid-file resume for sync.
Check when compiling: SyncSide.cs (RemoteSide.Write catch filters `when (ex is not IOException io || ...)` may need simplifying; SyncRead/Func<Task<string?>> lambdas; LimitedReadStream; SyncHost.Serve switch scoping and the `Path_()` local function), FolderSync.cs new methods (Replace('\\') escapes), SyncNetUi.cs (CheckBox/ScrollViewer namespaces, ContentDialog.Hide inside Btn), Transfer.cs line with mode "sync".
Test: allow folder on laptop B, Receive screen with code, run sync from A: new file, modified file (old version appears in .moveit-sync-trash), deletion (only ticked files move to trash), conflict (4 choices), wrong code, sync not allowed (clear message), cable/Wi-Fi drop mid-file (no half file left, rerun continues), path tricks refused, sensitive files (.env, .ssh) never listed.

## 48. Still NOT done after WIP51 (honest list, in order)
1. Section 20 gaps (a)-(e) above. 2. Section 27: no measurements; code ideas not written: batch many small files into one record, stream the Scanner instead of full lists, parallel hashing, ListView virtualization check. 3. Section 25 UI/UX redesign (not started: consistent typography/icons/spacing, empty/loading states pass). 4. Section 29 is only a checklist (QA_CHECKLIST.md). 5. Nothing from WIP20-WIP51 was compiled or tested. Section 16 table unchanged (20 and 27 Partial; 25 and 29 Not started).
Reminder of standing decisions: no compile/push until the user says all coding is done; Hinglish replies; show the section 16 table at the end of each reply; at most one question per reply.

## 49. WIP52 (written, not compiled): closes 20 (b), (c); small 27 step
20(c): local RunSync (SyncUi.cs) now uses ConflictChoice + FolderSync.ResolveAsync (LocalSide) and saves state AFTER conflicts.
20(b): SyncPair.AutoApply (dashboard checkbox, asks consent first). The 15-min timer (InitSyncWatch) now uses PlanAsync/ApplyAsync: only NEW/MODIFIED copies, keepOldVersions=true (old file copied to .moveit-sync-trash), plan.Deletions cleared (never automatic), conflicts untouched, skipped if not enough space, local/shared-folder pairs only, history line "AUTO". Not real-time; only while MoveIt is open.
27: Duplicates.Find hashes candidates in parallel (max 4 threads).
Remaining in 20: (a) saved remote pairs (never store the code), (d) progress UI for network sync, (e) mid-file resume for sync. Remaining in 27: small-file batching, streamed Scanner, measurements. 25 and 29 unchanged. Check when compiling: SyncDash.cs timer lambda (async inside Task.Run, `using var` inside), dashboard autoApply Click (act="autoapply" then Hide), SyncUi.cs RunSync edit.

## 50. WIP53 (written, not compiled): section 20 (a) and (d)
(a) Remote pairs: SyncPair.Remote/RemoteName/RemoteRoot (B = "moveit://name/root"); offered to save after a network sync; the pairing code is NEVER stored. Dashboard "Sync now" -> RunRemotePair (SyncNetUi.cs): scan by laptop name, ask for the code, refuse if the other laptop now allows a different folder, then the same safe flow (RunSyncSides). Remote pairs are skipped by the 15-min check/auto-apply timer, "Change folders" and auto-apply are blocked for them (message says why); PairStatus explains it.
(d) Progress: ApplyAsync progress callback shows "MoveIt · syncing n/m: file" in the WINDOW TITLE (throttled 300 ms). The app uses a custom title bar, so the title may not be visible: replace with a real progress control in the UI pass (25).
Section 20 still open: (e) mid-file resume for sync, and everything is untested. Still Partial in the table (kept honest until compiled and tested). Next: 27 (streamed Scanner, small-file batching), 25 (UI pass).

## 51. WIP54 (written, not compiled): section 27 Scanner
Scanner.Scan (Migration.cs) now uses DirectoryInfo.EnumerateFileSystemInfos: name, attributes and size from one enumeration, no per-file GetAttributes/FileInfo call, no per-folder ToList. Behaviour kept: reparse points skipped, blocked dirs/files counted, empty-folder detection (a folder whose enumeration FAILED is no longer reported as empty). Check when compiling: `fsi is DirectoryInfo`, cast to FileInfo. Still open in 27: small-file batching (protocol change, needs both sides to announce it like "rs"), measurements with big files / 100k small files, Scanner still keeps the full file list in memory (r.Files). 25 (UI pass incl. a real sync progress bar instead of the window-title text) and 29 untouched.

## 52. WIP55 (written, not compiled): section 25 first step
MainWindow.xaml has a shared progress strip "ToolStatusBar" (text + ProgressBar, bottom centre, hidden unless used) and ToolStatus.cs (ShowToolStatus(text, percent?) / HideToolStatus, thread-safe). Network Folder Sync now uses it (files n/m, byte percentage) instead of the window title (section 50 (d) text is superseded). Check when compiling: ThemeResource names CardBackgroundFillColorDefaultBrush / CardStrokeColorDefaultBrush (standard WinUI 3 resources), Canvas.ZIndex on the Border. Local RunSync, queue, backup and migration tools do NOT use the strip yet. Rest of 25 not started: consistent icons/typography/spacing pass over all pages, empty/loading states, accessibility check. 29 unchanged.

## 53. WIP56 (written, not compiled): section 25 step 2
ToolStatus.cs got WithStatus<T>(text, Func<T>): runs work off the UI thread with the shared strip shown (indeterminate). Used in: backup create/verify, backup check, restore, local Folder Sync (compare, apply, pre-sync backup). Not yet used by: queue, schedule preflight, software/app checks, network diagnostics, migration scan (these have their own text or none). Check when compiling: generic inference of WithStatus with tuple results (BackupCreate), FolderSync.Plan/Apply old sync wrappers still exist. Rest of 25 (icons/typography/spacing pass, empty/loading states, accessibility) and 29 unchanged; 20(e) and 27 batching/measurements unchanged.

## 54. WIP57 (written, not compiled): section 25 step 3
Shared strip now also used by: winget official-source check (AppSourceUi.cs, SoftwareUi.cs: "n/m: app" with percentage), speed test (NetCheck.cs, indeterminate), schedule preflight (ScheduleUi.cs). Check when compiling: NetCheck.cs `SpeedResult r; try { r = await ... } finally` (definite assignment), SoftwareUi.cs/AppSourceUi.cs new `n` variable names do not clash with existing locals. Not covered yet: queue dialog, network diagnostics beyond the speed test, migration scan/plan (PlanMigration has its own status text). Remaining in 25: icons/typography/spacing pass, empty/loading states, accessibility. 20(e), 27 batching/measurements, 29 unchanged.

## 55. WIP58 (written, not compiled): section 27 protocol overhead (no format change)
Wire format is IDENTICAL, so old and new MoveIt still talk to each other. Net.SendMsg now builds length+JSON in one buffer and writes once (was two writes = two encrypted records). Sender.Send body loop reads each chunk into wbuf at offset 4, writes the 4-byte length into the gap and sends header+data in ONE write (was two). Effect: about half the records per chunk and per control message; most visible with many small files and on the encrypted path. NOT measured. Receiver unchanged. Real small-file batching (several files in one record) is still not written and needs a protocol flag both sides announce. Check when compiling: `wbuf` declared next to `buf`; the older `fh` array is still used for the -2 abandon marker. Test: normal send, many small files, shrink-mid-send (-2 path), resume of a >64 MB file, old build as peer.

## 56. WIP59 (written, not compiled): section 25 accessibility
Audit of MainWindow.xaml: all text buttons already have names; the three icon-only header buttons (Back, Transfer history, Settings) now have AutomationProperties.Name (before only a tooltip). Cards and the remove button already had names. Empty states exist for Send items, scan results and History. Not audited: dialogs built in code (ContentDialog content, CheckBoxes), colour contrast, keyboard order, screen-reader test. Rest of 25 (icons/typography/spacing consistency over all pages) still open. 20(e), 27 batching/measurements, 29 unchanged.

## 57. WIP60 (written, not compiled): section 27 small-file packing (replaces the "batching" idea of section 55)
Secure.cs: CoalescingStream wraps the sender's stream AFTER the handshake (Hold = true) and packs tiny writes (file header JSON, small chunks, checksum messages) into 64 KB writes. Wire bytes are IDENTICAL, so the receiver and old peers need no change and no flag. Hold is set false (and buffer flushed) before: waiting for the receiver's offset answer of a big resumable file (FlushNow), the final "end" message (before the ack wait), and the cancel marker. The handshake itself runs with Hold = false. Check when compiling: `var (c, s0, secure)` + `Stream s = co` in Sender.Send (only Send uses it; Probe/SpeedTest/FindVerified unchanged), CoalescingStream overrides (WriteAsync(ReadOnlyMemory) is `override async ValueTask`). Risk to test first: a hang at the end of a send with many tiny files (would mean a missing flush), cancel from sender while buffered, resume of a >64 MB file, shrink-mid-send (-2 marker is buffered, flushed with the next write).

## 58. WIP61 (written, not compiled): startup + clipboard
Section 2: the Windows startup entry now runs `"MoveIt.exe" --startup`. At launch with --startup, if the tray icon is enabled and visible (MainWindow.StartHiddenOk) the window is NOT shown (stays in the tray); without the tray the window opens normally (never an invisible app). Existing entries without the argument still work (window opens). Check: App.xaml.cs `w` type is MainWindow (StartHiddenOk is public); single-instance second launch uses FindWindowW("MoveIt") on a hidden window (verify it restores).
Section 18: when clipboard sharing is ON on the receiving laptop and a single MoveIt-Clipboard-*.txt arrives, a preview dialog asks "Copy" / "Not now" before the normal save-folder step (OfferClipboardThen in ClipboardUi.cs, reads the file from the staging folder). Nothing is copied without that click; sending is still manual and unencrypted-warning text may need updating now that sends are encrypted when both sides support it.
Not done: items listed in sections 48-57 (20e sync resume, 25 pass, 27 measurements, 29 tests).

## 59. WIP62 (written, not compiled): closes section 20 (e); clipboard wording; QA checklist
20(e): Folder Sync mid-file resume (SyncSide.cs). Remote put of a file >= 64 MB sends rs:1; SyncHost.Put keeps a partial named `<file>.moveit-part-<mtimeTicks>-<size>` next to the target (so a changed source never reuses it), answers {ok, off}; the sender reads and discards the first `off` bytes through the hashing stream (final SHA-256 covers the whole file) and sends the rest; the host hashes the existing partial first (Net.HashPrefix). Partial is KEPT when the connection drops (IOException), DELETED on checksum mismatch, on cancel and when another version of the same file arrives (obsolete `.moveit-part-*` of that file removed). Old receivers never send "off" (treated as 0), old senders never send rs (plain `.moveit-tmp` path). SyncScan.Snapshot now ignores `*.moveit-tmp` and `*.moveit-part-*` so leftovers never sync as files. Limit: a leftover partial of a file that is never synced again stays in the folder until that file is put again (no timed cleanup in the sync folder). Remote-to-local reads (OpenRead/get) do not resume: only uploads to the other laptop do.
Check when compiling: SyncSide.cs RemoteSide.Write (`ov`, `off`, `r0` names), SyncHost.Serve `case "put"` block with `out var rsx`, Put signature (extra bool wantRs), `resumable ? (object)new {...} : new {...}` conversions.
18: clipboard send dialog now says "encrypted if the other laptop runs this version, older versions unencrypted", and the result says Sent encrypted / WITHOUT encryption using SendReport.Encrypted.
29: QA_CHECKLIST.md header fixed to 1.4.0 and WIP52-62 tests added (checklist only, nothing run).
Section 16 table CHANGED: 20 moved to "Code written, test pending" (all listed gaps a-e closed in code; nothing compiled/tested). 25 is now listed as Partial (first steps done: ToolStatus strip, accessibility names; consistent icons/typography/spacing pass still open) and 27 stays Partial (no measurements; Scanner still holds the full file list in memory; ListView virtualization untested).
Still open code work: 25 consistent icons/typography/spacing pass over all pages; 27 streamed Scanner. Then the optimize pass the user wants after code is complete. No compile/test by Claude (user tests himself).

## 60. WIP63 (written, not compiled): 29, 25, 27 closed in code; handoff to the optimization phase
User (2026-11-28): finish all code, he tests himself, then OPTIMIZE the code, then move on.
29: new SelfTest.cs (Settings > "Run self-check (this PC only)"): 10 automatic checks that need only this PC: credential blocklist, unsafe sync paths refused, SHA-256 helper, scanner (empty folders, sensitive items skipped, source untouched), LocalSide verified write (bad checksum and short copy discarded, good file kept), sync trash/archive never erase, sync scan ignores .moveit-tmp / .moveit-part-*, CoalescingStream byte-identical, SecureStream loopback round trip (300 KB), wrong pairing code cannot read. Report saved to LocalAppData\MoveIt\Reports\self-check-*.txt. New QA_SPEC29.md = the complete step-by-step test plan for every item of spec section 29 (A install, B connection, C migration, D new features, E safety, F UI; 76 rows, all NOT RUN). QA_CHECKLIST.md points to it. A PASS in the self-check does NOT replace the two-laptop, installer or UI tests.
25: shared type styles (MiTitle..MiCaption), MiButton, GlassCard, progress strip, automation names were already in place; this round: two plain captions and the wizard Back button now use the shared styles. NOT done and cannot be judged without seeing the app: a visual review of every page (icons, spacing, contrast). That is the user's UI test (QA_SPEC29 F6-F12); fix what he reports.
27: Scanner already streams the enumeration (WIP54), parallel duplicate hashing, ArrayPool buffers, packed small writes, single-write messages. The scan result still keeps the full (path, rel) list in memory because the review dialog and the sender need it (about 200 bytes per file; 1 million files = roughly 200 MB). No measurements exist: benchmark with a 2 GB file and 50,000 tiny files first (QA_SPEC29 C2, C3).
Check when compiling: SelfTest.cs (lambda return types `string?`, `await co.WriteAsync(b)` on byte[] binds to the ReadOnlyMemory overload, `var (ss, _)` discard, `Task.Run(() => SelfTest.RunAll(...))` returning Task<List<Result>>), MainWindow.xaml new button handler SelfTest_Click.
OPTIMIZATION PHASE (next, after the user's test results or when he asks): 1) compile and fix errors first (risk spots in sections 34-60), 2) hot paths: Sender.Send body loop, Receiver.Handle write loop, Scanner memory (store rel lazily), SyncScan.Snapshot (one pass), FolderSync hashing (cache by size+mtime), 3) remove duplicated code between old local FolderSync.Plan/Apply and PlanAsync/ApplyAsync, 4) split MainWindow.xaml.cs (1250 lines) by page, 5) UI pass from the user's findings. Keep wire formats unchanged unless both sides announce a flag.

## 61. WIP64 (written, not compiled): full static review + low-end optimization (no dotnet in the sandbox, so NO compile; all checks below are scripts + reading)
FOUND AND FIXED a real compile error: Transfer.cs Sender.Send, the `try { f = new FileStream(...); // comment }` line had its closing `}` INSIDE the // comment (the try block never closed). Fixed (brace now before the comment). After the fix every .cs file is balanced for { } ( ) [ ] (tokenizer that ignores strings/comments), every .xaml parses as XML, build.yml parses as YAML.
Scripts run over all 55 .cs files (kept in the sandbox only, not in the repo): comment-swallows-code scan (no more hits); local-name scope conflicts CS0136/CS0128 (only false positives: sibling foreach/catch/for variables); Class.Member cross-reference of every project type (no missing member); call arity of calls to project classes (no mismatch); XAML handlers all exist, no duplicate x:Name, every x:Name used from C# exists. These cannot replace the compiler: types, nullability and overload resolution are unchecked.
Read in full: Sender.Send, Receiver.Handle (resume, abandon, checksum, cancel paths), Net.SendMsg/RecvMsg, CoalescingStream, SyncSide (all). No further logic bug found there.
Optimizations (all behaviour-preserving, wire formats unchanged): Net.RecvMsg frees the JsonDocument (it leaked pooled memory per message); Duplicates hashing uses max(1, min(4, cores/2)) threads (was min(4, cores): starved the UI on 2-core PCs); ParticleFlow.OnFrame runs at about 30 fps when the PC has 4 cores or fewer (LowEnd); AuroraBg clouds stop when the window is not active (and stay still when Windows "animation effects" is off) via AuroraBg.SetPaused, wired to MainWindow.Activated.
Known, not changed: installer.iss startup entry (reg add Run\MoveIt) writes the exe WITHOUT `--startup` while the in-app toggle writes `--startup`; same value name so no duplicate, but a startup chosen in the installer opens the window instead of staying in the tray. Fix later with care (Inno quoting). Sender/Receiver still allocate 1-2 MB buffers per session (not per file; ArrayPool would avoid large-object-heap allocations). Scanner keeps the full file list in memory. Old local FolderSync.Plan/Apply and PlanAsync/ApplyAsync are duplicated code (merge after the first compile).
Check first when compiling: AuroraBg.cs (`Still`, `continue` in the Layout foreach), MainWindow ctor `Activated` lambda, ParticleFlow `LowEnd`, Transfer.cs RecvMsg `using var doc`.
Next: user pushes to GitHub when he says so; fix compile errors; user tests with QA_SPEC29.md; then remaining optimization from section 60 (split MainWindow.xaml.cs, merge duplicated sync code, ArrayPool buffers, lazy rel paths in Scanner).

## 62. WIP65 (static scan session before the first GitHub push; NO dotnet in the sandbox, nothing compiled or run)
Read in full this session: MainWindow.xaml.cs (all 1254 lines), ParticleFlow.xaml.cs, AuroraBg.xaml.cs, Fx.cs, Secure.cs (SecureStream, PrefixStream, CoalescingStream), SelfTest.cs, App.xaml.cs, Startup.cs, Tray.cs, Updates.cs, Models.cs, Battery.cs, Pending.cs, SelectUi.cs, MoveIt.csproj, build.yml, installer.iss. Scripts run: XAML resource keys (all missing ones are built-in WinUI keys), C# Res()/Resources[] keys (all exist), XAML events that fire during InitializeComponent (only SwFilter_Changed has a null guard, Link_Changed is safe at index 0), await inside lock (none), Span/ref/out in async (none), project type names vs System/WinUI names (no clash).
FIXED (real bugs, all untested):
1. Tray.cs: GetCursorPos(out (int X, int Y)) used a ValueTuple in a P/Invoke -> MarshalDirectiveException on the first right-click of the tray icon. Now a real POINT struct.
2. MainWindow.xaml.cs Send_Click: `snapshot` was `cond ? new List<(string path,bool folder)>() : items.Select(i => (i.FullPath, i.Folder)).ToList()`; the differing tuple names are dropped, so `snapshot.Select(x => x.path)` would not compile (CS1061). The right side now names its elements path/folder.
3. ParticleFlow.xaml.cs + AuroraBg.xaml.cs: `new UISettings()` in a static initializer could crash the app at start-up if it throws; now wrapped (MakeUi) and AnimationsEnabled read inside try/catch.
4. installer.iss: the optional "start with Windows" entry now writes `"<exe>" --startup` (same as the in-app toggle). The known mismatch from WIP64 is closed. Only the quoting changed: check the .iss compile output on the first Actions run.
NOT scanned this session (see SCAN_CONTINUE_PROMPT.md for the ordered list): Transfer.cs (only RecvMsg / Probe parts), WebShare.cs, FolderSync.cs, Migration.cs, Duplicates.cs, Backup.cs, Connections.cs, WiredStatus.cs, NetCheck.cs, Reconnect.cs, Schedule*.cs, Queue*.cs, Sync*.cs UI files, Software/App*Ui files, ClipboardUi.cs, ConflictUi.cs, DashboardUi.cs, BatteryWatch.cs, Report*.cs, Tools.cs, Radar.*, and the XAML bodies (MainWindow.xaml, App.xaml, ParticleFlow.xaml).
Section 16 table is unchanged (nothing compiled or tested).
