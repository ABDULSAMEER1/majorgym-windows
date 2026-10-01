# Phase 1 — SecuGen Hamster 20 detection (Windows)

## What the message actually means
"No fingerprint scanner was found…" is shown only for `OpenResult.DeviceNotFound`. Before this change that
result was produced in exactly three places in `FingerprintScanner.Open()`: the `SGFingerPrintManager`
constructor throwing, `Init(DEV_AUTO)` throwing, or `Init(DEV_AUTO)` returning any non-zero code.
`OpenDevice` failures were reported as "busy", so the failure on the laptop happened **before** `OpenDevice` —
at SDK load / Init — and the exception type or SecuGen error code was thrown away (and the Release app had no
Trace listener, so even the log lines were discarded). The single triggering cause therefore cannot be
proven from source alone; this change makes it visible and fixes the defects found on the way.

## Verified against the shipped SDK files (not guessed)
- Managed `SecuGen.FDxSDKPro.DotNet.Windows.dll` is a C++/CLI mixed-mode assembly targeting .NETCoreApp 6.0
  (needs VCRUNTIME140/MSVCP140). x64 folder = PE x64, x86 folder = PE x86, all native DLLs match.
- `SGFPMPortAddr.USB_AUTO_DETECT` = 597, `SGFPMDeviceName.DEV_AUTO` = 255, `ERROR_TIME_OUT` = 54,
  `ERROR_DEVICE_NOT_FOUND` = 55 — all match the code. Every SDK call compiles against both the x64 and x86 DLLs.
- `sgfplib.dll` loads per-model driver modules at Init time (`sgfdu03/04/05/06/06ap/07/07a/08/08a/09/09a/10a/sda`,
  `x64` or `m` suffixed). **None are in `vendor/SecuGen` or the build output.** Android bundles the equivalent
  `libjnisgfdu*.so` set. On Windows they only resolve if the SecuGen driver installer put them in System32.
- `PlatformTarget` follows `Platform` (x64 → x64, x86 → x86); build output contains the matching managed + native DLLs.

## Changes
| File | Change |
|---|---|
| `MajorGym.Fingerprint/ScannerDiagnostics.cs` (new) | File Trace listener (`%LOCALAPPDATA%\MajorGym\logs\scanner.log`); one-time preflight (arch, native DLL placement + PE machine, VC++ runtime, `sgfdu*.dll` locations, real `LoadLibraryEx` of `sgfplib.dll` with Win32 error); SDK-free `UsbScannerPresence` (SetupAPI, VID 0x1162 — Android's `isScannerConnected`). |
| `FingerprintScanner.cs` | Open() now distinguishes SDK-unavailable (ctor/Init throw, DLL-load error codes) / no device / busy / error, logs every code by name, runs `EnumerateDevice` + `NumberOfDevice` on the *same* manager for diagnostics, checks `GetDeviceInfo`'s return and image size. New `OpenResult.SdkUnavailable`. |
| `ScannerHub.cs` | Detach poll no longer creates a second `SGFingerPrintManager` (it could report 0 devices for an already-open scanner and force-release a healthy session); uses the OS USB list instead. Added `ReleaseSessionAsync`. |
| `FingerprintKioskLoop.cs` | `IsScannerConnected` is SDK-free; Android-style 3 s retry monitor (`StartRetryMonitor`) with pause flag so it never restarts the kiosk during enrollment; session released after 5 consecutive capture errors (Windows has no detach broadcast); 30 s back-off when the SDK itself cannot start. |
| `EnrollFingerprintViewModel.cs` | Message for `SdkUnavailable`; open-failure log line; Android parity: release wait 7000 ms and "trying anyway" on timeout, 2 open retries (400/800 ms), 10 000 ms capture timeout. |
| `App.xaml.cs` | Attach the log listener at startup; start the retry monitor. |
| `MajorGym.Fingerprint.csproj` | Three explicit native `None` items → one `sg*.dll` wildcard per architecture (same files today; deploys any `sgfdu*.dll` you add to `vendor/SecuGen/<arch>`). |

Unchanged: enrollment/duplicate/matching logic, kiosk timing constants (identical to Android), attendance, UI layout, DB, backup, sync, x86 support.

## Not tested
No Hamster 20 was available; no Windows/WPF build was possible here (WPF targeting packs need NuGet). Compiled for real:
the Fingerprint project sources and `FingerprintKioskLoop.cs` (with stubs for the Data types) against the actual x64 and
x86 SecuGen DLLs, and the project's native-DLL copy items. Not compiled: MajorGym.App (XAML, view models, App.xaml.cs).

## On the laptop
1. Build x64 (CI workflow or `dotnet build MajorGym.Windows.sln -p:Platform=x64`), run `MajorGym.exe` from its output folder.
2. Open Enroll Fingerprint once, then read `%LOCALAPPDATA%\MajorGym\logs\scanner.log`.
3. Look at the `PREFLIGHT` lines and the first `SCANNER_*` failure line:
   - `sgfplib.dll could not be loaded (Win32 error 126/193)` or `vcruntime140.dll present=False` → install the Microsoft Visual C++ 2015-2022 Redistributable (x64), or fix the architecture.
   - `Init(DEV_AUTO) failed code=6 (ERROR_DLLLOAD_FAILED_DRV)` (or 5/7/9/51/56) → the device module is missing: copy the `sgfdu*x64.dll` files from the SDK's x64 bin folder into `vendor/SecuGen/x64`, rebuild.
   - `Init … code=55/52` or `numberOfDevice=0` → SDK started but sees no device: check Device Manager that the SecuGen driver (not only a generic/WBF one) is bound.
   - `OpenDevice failed code=…` → send me the code.
4. After detection works: enroll (two scans), confirm kiosk resumes and records attendance, unplug/replug and confirm the kiosk recovers within a few seconds.
