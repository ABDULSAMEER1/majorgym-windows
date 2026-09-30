# Phase 2 — Attendance + Backup parity report

Android (`majorapp-new-main`) was the golden reference and was not modified.

## A. Android Attendance implementation found
- `AttendanceScreen` (Screens.kt) = back arrow + "Attendance" title + `GymAttendanceQrCard` only.
- QR payload `QrUtils.GYM_ATTENDANCE_CODE = "MAJOR_GYM_ATTENDANCE_2026"` (verified, unchanged), rendered by
  `QRCodeWriter().encode(text, QR_CODE, 512, 512)` (ZXing default 4-module quiet zone). Card: glass gradient,
  cyan glow border, radius 16, padding 16; label "GYM ATTENDANCE QR"; caption; 176 dp gradient frame (radius 16) →
  5 dp → white (radius 12) → 10 dp → code; actions **Display** (fullscreen white dialog, caption
  "MAJOR GYM — Scan to mark attendance") and **Share** (PNG in cache `shared_qr/`).
- Bottom-nav "Attendance" opens **AttendanceLogsScreen**, not the QR screen. The QR screen is opened from the
  **Backup** screen's "Open Attendance Scanner" card.
- `AttendanceLogsScreen`: one row per member per selected day (earliest scan; members that no longer exist are
  skipped); search (name ci / phone / ID proof ci); filter All / Morning / Evening / Active (status == ACTIVE only) /
  Expired; sorted newest scan first; Present/Morning/Evening counts from the searched+filtered list; per-count
  visibility switches; rows open `AttendanceHistoryScreen`.
- `AttendanceHistoryScreen`: attendedDays = distinct `dayEpoch` over all retained records; eligibleDays =
  `eligibleAttendanceDays(member.joinedMillis)` (renewal never changes joinedMillis); percentage =
  `attendancePercentage(attended, eligible)`; "N Days Left"/"Expired"; visits grouped by month, newest first.
- `AttendanceRetentionWorker`: deletes attendance older than 4 months.

## B. Windows Attendance before
QR screen was the bottom-nav "Attendance" target and lacked the card/frame/Display/Share; Logs listed every scan with
Newer/Older day buttons, no search/filter/date picker, and its "Settings" button opened the QR screen; History measured
from the last history entry with a date-bounded day count; no retention sweep; QR drawn with zero quiet zone.

## C. Attendance changes
- Bottom-nav Attendance → Logs (`MainWindow.xaml.cs`).
- QR screen rewritten to Android's structure (`AttendanceView.xaml`, `AttendanceViewModel.cs`).
- Logs rewritten with Android's exact grouping/search/filter/count/settings rules (`AttendanceLogsViewModel.cs`,
  `AttendanceLogsView.xaml[.cs]`), live refresh on kiosk check-ins.
- History rewritten with Android's formulas and layout (`AttendanceHistoryViewModel.cs`, `AttendanceHistoryView.xaml`,
  new `Controls/ArcProgress.cs`); Back → Logs.
- `Repository.CleanupOldAttendance()` (Android retention rule) run once at startup (`App.xaml.cs`).
- Database schema untouched; no second attendance format.

## D/E. QR
Payload unchanged. Windows now renders at 512 px with the default quiet zone (`QrUtils.GymQrBitmap` only; the member
onboarding QR keeps its Phase 1 rendering), inside Android's 176 px frame; Display = in-app fullscreen overlay;
Share = PNG in `cache\shared_qr\gym_attendance_qr.png` + clipboard image + Explorer selection.

## F. Android Backup implementation
`BackupManager` (JSON), `BackupZip` (ZIP `backup.json`, verify), `BackupService` (validate, create, restore),
`Repository.mergeAll/restoreAttendance/restoreArchivedMembersFromBackup`, `LocalBackupManager`, `BackupHistoryPrefs`,
`BackupScreen`/`BackupHistoryScreen`.

## G. Windows Backup before
Field names already matched Android. Missing: schema validation, safety snapshot, atomic restore, phone-conflict
REPLACE semantics, tolerant type coercion, Android's screen layout/wording, internal backup copy for Share.

## H. Backup changes
- New `BackupService.cs`: `ValidateSchema` (Android messages verbatim), `CreateZipBackup`/`WriteAndVerify`,
  `Prepare` (no side effects) + `Commit` (safety snapshot → one DB transaction → photo files), `LocalBackupStore`.
- `BackupManager`: `ParseBackup` (in-memory decode, deferred photo writes), org.json-style tolerant readers (`Js`),
  relaxed JSON escaping on export.
- `Repository.RestoreBackup`: single transaction; newer-local-wins; Android REPLACE behaviour for phone collisions;
  attendance/archived INSERT OR IGNORE; `SaveCore` factored out of `Save`.
- `BackupZip`: distinguishes a truncated ZIP (no end-of-central-directory) and says so.
- Backup / Backup History screens rewritten to Android's layout and strings; no restore confirm dialog (Android has none).

## I. Android backup JSON schema (schemaVersion 5)
Root: `app:"MajorGym"`, `schemaVersion:5`, `exportedAt`, `members[]`, `attendance[]`, `archivedMembers[]`.
Member: `id,name,phone,plan,fee,joinedMillis,expiryMillis,updatedAtMillis,history[],idProof,passwordHash,createdAtMillis,
lastAttendanceMillis?,archived,qrToken,qrTokenExpiryMillis,pendingDeletionMillis?,photoBase64?,idProofPhotoBase64?,
fingerprintTemplateBase64?` (Base64 NO_WRAP). Attendance: `memberId,timestampMillis,dayEpoch,session`.
Archived: `originalMemberId,name,phone,joinedMillis,lastPlan,lastFee,lastStartMillis,lastExpiryMillis,idProof,archivedAtMillis`.
Container: ZIP with root entry `backup.json` (legacy plain JSON also accepted; detected by `PK\3\4`). No change-log/sync
data is exported.

## K. Import failure root cause
The supplied `majorbackup (1).zip` is **truncated**: 147,456 bytes (exactly 144 KiB), starts with a valid ZIP local header
(flag 0x808, deflate) but has no data descriptor, no central directory and no end-of-central-directory record; the deflate
stream never terminates. Only 196,357 bytes of JSON can be inflated, ending inside the first member's `photoBase64`; not
one complete member survives. No importer (Android's included) can read it. Android verifies its ZIP before handing it out,
so the file was cut during copy/transfer. Windows was right to report it as corrupted; the message now says it looks
incomplete and reports the received size. **Re-copy or re-export the backup from the phone.**
Separate latent importer defects were also fixed (H).

## L. Export compatibility
Windows export uses Android's field names/types/container. Verified by a Windows export → Windows import round trip
(photo and fingerprint bytes identical). Android's own importer was not run.

## M/N/O. Files
Modified: App.xaml.cs, Assets/Theme.xaml, ViewModels/{AttendanceHistory,AttendanceLogs,Attendance,BackupHistory,Backup}ViewModel.cs,
Views/{AttendanceHistory,AttendanceLogs,Attendance,BackupHistory,Backup}View.xaml, Views/AttendanceLogsView.xaml.cs,
Views/MainWindow.xaml.cs, Data/{BackupManager,BackupZip,PhotoStore,QrUtils,Repository}.cs.
Added: App/Controls/ArcProgress.cs, Data/BackupService.cs. Deleted: none.

## P. Build status
**Not built.** No Windows/WPF toolchain and no NuGet access in the authoring environment.

## Q. Static validation performed
- .NET 8 SDK compile of the Data project (all files except real `PhotoStore`/`QrUtils`) and the five new view models against
  small stubs for Sqlite/WPF/System.Drawing: 0 errors (it caught and led to fixing a missing `using MajorGym.Kiosk`).
- Roslyn parse of every changed C# file: 0 syntax errors.
- Executed the real BackupManager/BackupZip/BackupService code on 27 checks: Android-style data-descriptor ZIP, escaped
  slashes, type-coercion edge cases, BOM/legacy JSON, invalid/other-app/bad-version/no-entry files, truncated ZIPs (including
  the user's file), deferred photo writes, failed-restore writes nothing, export→import round trip. All pass.
- All XAML parsed; every StaticResource resolves; every binding maps to a view-model member.

## R. Remaining limitations
- Never run on Windows: XAML layout/rendering, popups, Calendar, file dialogs, Explorer/clipboard share are untested.
- `Repository.RestoreBackup`/`CleanupOldAttendance` were type-checked but not run against SQLite.
- Member History omits Android's decorative gym-graphic backdrop and rotated "DISCIPLINE BUILDS FREEDOM" tagline, and
  approximates sweep-gradient rings with linear gradients; the stepped hero-card outline is a plain rounded rectangle.
- Share uses clipboard + Explorer (no Windows share sheet). No restore confirmation (matches Android).
- Restored members are also written to the sync change log (Android backfills later; Windows has no backfill yet).
- The Windows restore reports rows actually applied; Android reports rows parsed.
