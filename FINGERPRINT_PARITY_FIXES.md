# Windows fingerprint scanner — Android-parity fixes

Reference: Android `FingerprintScanner` / `ScannerHub` / `FingerprintKioskService` / `EnrollFingerprintScreen`.
The Windows scanner layer was already a close port; these changes fix the places where it behaved differently.

## Fixes
1. **Kiosk loop used the shared SQLite connection from background threads** (`FingerprintKioskLoop`).
   The app's single `SqliteConnection` belongs to the UI thread (see `WpfDbThread`), but the loop called
   `GetAll()` every 10 s and `Save()`/`RecordAttendanceVisit()` on every match from thread-pool threads.
   Microsoft.Data.Sqlite connections are not thread-safe, so a refresh could fail (cache stays empty =>
   every finger reads "Member Not Found") or an attendance write could fail. All repository calls now go through
   `IDbThread` (constructor gained a parameter; `App.xaml.cs` updated).
2. **Open() ran on the UI thread during enrollment** (`ScannerHub.EnsureOpenAsync`) — the blocking SDK Init/OpenDevice
   froze the window. Now runs on the thread pool.
3. **Failed Init leaked a native SDK object** (`FingerprintScanner.Open`). The 3 s retry monitor re-ran Open() while the
   scanner was plugged in, abandoning an undisposed `SGFingerPrintManager` each time. Now disposed immediately.
4. **Enrollment Cancel didn't cancel the capture** — a 10 s native capture kept running and swallowed the next finger.
   Capture now runs in 1 s slices (same 10 s total) so Cancel works in ~1 s.
5. **Stale session after unplug/replug during enrollment** — Windows has no detach event (Android has the broadcast).
   On a capture error enrollment now drops the session and reopens once, then retries the capture once.
6. **Audio** (`MembershipAudioPlayer`) is now played on the UI dispatcher (WPF `MediaPlayer` requires it).
   Kiosk overlay updates use `BeginInvoke` so the scan loop never blocks on the UI.
7. **Exit** no longer blocks the UI thread waiting on the kiosk loop (would deadlock after fix 1): `Shutdown()` cancels,
   scanner release is bounded to 5 s.
8. The "scanner software could not be started" message now says what to install and where `scanner.log` is.

## Not changed / needs your Windows PC
* Nothing here could be compiled or run with a real scanner in the authoring environment (no .NET SDK, no hardware).
  Build via the GitHub workflow or `dotnet build MajorGym.Windows.sln -p:Platform=x64`.
* The SecuGen device driver modules (`sgfdu*.dll`) are NOT in `vendor/SecuGen/x64` (Android bundles its `libjnisgfdu*.so`).
  On Windows they must come from the SecuGen driver/FDx SDK installer (System32) or be copied from the SDK's `bin\x64`
  folder into `vendor/SecuGen/x64` (they are picked up by the existing `sg*.dll` wildcard). If Init fails with a
  DLLLOAD error this is the cause. Also confirm in Device Manager that the Hamster Pro 20 is bound to the SecuGen
  driver, not the Windows Biometric (WBF) driver.
* If it still fails, send `%LOCALAPPDATA%\MajorGym\logs\scanner.log` (PREFLIGHT lines + first SCANNER_* error).

## Driver bundled with the app
* `vendor/SecuGenDriver/SgDrvSetupUniversal.exe` (SecuGen Universal Driver Installer v3.0.0.3 + its PDF guide) is checked in
  and copied to `Drivers\` beside `MajorGym.exe` on every build, so the CI zip contains it.
* If the SecuGen SDK cannot start, the Enroll Fingerprint screen shows an **Install Scanner Driver** button that launches
  that installer with the normal Windows administrator (UAC) prompt. It is never installed silently.
* The USB driver is machine-level (needs admin) — it cannot be loaded from the app folder, so each new PC runs it once.
* Check the SecuGen redistribution terms before sharing the repo/zip publicly (keep the repo private if unsure).

## Automated setup (replaces the manual checklist)
* CI (`windows-build.yml`) downloads Microsoft's `vc_redist.x64.exe` and ships it in `Drivers\`.
* **Install Scanner Driver** installs the Visual C++ runtime first (only if this PC lacks it), then the SecuGen driver,
  each behind the normal Windows administrator prompt.
* **Check Scanner** (Enroll Fingerprint screen) reports: Visual C++ runtime, sgfplib.dll, sgfdu*.dll driver modules,
  scanner present on USB — plus the next step to take.
* Cannot be automated: running a real capture test in GitHub (no scanner on the runner) or approving the admin prompt.

## Check-in sounds (Android parity)
Root cause of "no sound on Windows": the kiosk loop already called the audio player for ACTIVE / EXPIRED /
EXPIRING_TODAY / EXPIRING_IN_1_DAY / UNIDENTIFIED, but the five clips were never in the Windows project (the
Assets\Audio folder it looked in was empty, so every call was silently skipped). The five Android clips are now
in MajorGym.App\Assets\Audio, copied beside MajorGym.exe, and the app plays them from there. Status rules are the same
as Android (expired < today < tomorrow < active, by local calendar date).

## Enrollment: two scans (Android parity)
Same flow as Android's EnrollFingerprintScreen: "Start Scan" -> "First scan captured. Scan the same finger again to
confirm." -> "Scan Again to Confirm". The scanner stays held between the two scans, the first scan survives a
timeout/read error on the second, a mismatch restarts both scans, a fingerprint already enrolled for another member is
rejected, the confirm scan is the one saved, and leaving the screen hands the scanner back to the kiosk. Works the same
for new enrollment and re-enroll (re-enroll replaces the old template).

## Windows installer
CI now also publishes a self-contained build (no .NET install needed) and wraps it in `MajorGym-Setup.exe` with Inno
Setup (`installer\MajorGym.iss`): Start-menu/desktop shortcuts, uninstaller, and an optional step that installs the
Visual C++ runtime + SecuGen driver. Download it from Actions > the run > Artifacts > MajorGym-Setup, or push a tag
(`git tag v1.0.0 && git push origin v1.0.0`) to get it on the repo's Releases page.
