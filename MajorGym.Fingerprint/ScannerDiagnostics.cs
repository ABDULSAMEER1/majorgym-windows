using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace MajorGym.Fingerprint;

/// <summary>
/// Minimal troubleshooting support for the SecuGen scanner path — NOT a logging framework.
///
/// Every scanner class already reports through <see cref="Trace"/>, but a Release WPF build has
/// no Trace listener attached, so those messages were being discarded and the cause of a failed
/// open could never be recovered after the fact. <see cref="Initialize"/> attaches one file
/// listener; <see cref="RunPreflightOnce"/> records the facts that decide whether the SecuGen
/// SDK can load at all (process architecture, which native DLLs sit beside the executable and
/// what CPU each was built for, whether the Visual C++ runtime the SDK links against is present,
/// whether sgfplib.dll actually loads and, if not, the Win32 error).
///
/// Nothing here touches fingerprint templates, images or member data.
/// </summary>
public static class ScannerDiagnostics
{
    private const long MaxLogBytes = 1_000_000;
    private static readonly object Gate = new();
    private static bool _listenerInstalled;
    private static PreflightResult? _cachedOk;

    /// <summary>Human-readable reason for the most recent failed open attempt, or null after a
    /// success. For troubleshooting only — not shown in the normal UI.</summary>
    public static volatile string? LastFailure;

    public sealed record PreflightResult(bool CanProceed, string Summary);

    /// <summary>Attaches a file-backed Trace listener (idempotent). Safe to call before anything
    /// else touches the scanner. Never throws.</summary>
    public static void Initialize(string logFilePath)
    {
        lock (Gate)
        {
            if (_listenerInstalled) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);
                // Keep the file small: start fresh once it grows past ~1 MB.
                if (File.Exists(logFilePath) && new FileInfo(logFilePath).Length > MaxLogBytes)
                    File.Delete(logFilePath);

                var stream = new FileStream(logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                var writer = new StreamWriter(stream) { AutoFlush = true };
                var listener = new TextWriterTraceListener(writer, "MajorGymScanner")
                {
                    TraceOutputOptions = TraceOptions.DateTime
                };
                Trace.Listeners.Add(listener);
                Trace.AutoFlush = true;
                _listenerInstalled = true;
                Trace.TraceInformation($"[ScannerDiagnostics] ===== session start, log file: {logFilePath} =====");
            }
            catch
            {
                // Diagnostics must never be the reason the application fails to start.
            }
        }
    }

    /// <summary>Checks (and logs) everything that must be true for the SecuGen SDK to load. Only a
    /// definitive failure to load sgfplib.dll yields <c>CanProceed == false</c>; anything merely
    /// suspicious is logged and the real SDK call is still allowed to make the final decision.
    /// A successful result is cached for the process; a failed one is re-evaluated each call (cheap)
    /// so fixing the cause and retrying does not require guessing.</summary>
    public static PreflightResult RunPreflightOnce()
    {
        lock (Gate)
        {
            if (_cachedOk is not null) return _cachedOk;
            var result = RunPreflight();
            if (result.CanProceed) _cachedOk = result;
            return result;
        }
    }

    /// <summary>Plain-language self-check shown by the "Check Scanner" button — the same checks as the manual
    /// troubleshooting steps (runtime, SecuGen DLLs, driver modules, USB presence). Never throws.</summary>
    public static string BuildReport()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var sysDir = Environment.SystemDirectory;
            bool Has(string name) => File.Exists(Path.Combine(baseDir, name)) || File.Exists(Path.Combine(sysDir, name));
            string Mark(bool ok) => ok ? "OK" : "MISSING";

            var vc = Has("vcruntime140.dll") && Has("msvcp140.dll");
            var lib = File.Exists(Path.Combine(baseDir, "sgfplib.dll"));
            var modules = new List<string>();
            foreach (var dir in new[] { baseDir, sysDir })
            {
                try { modules.AddRange(Directory.GetFiles(dir, "sgfdu*.dll").Select(Path.GetFileName)!); } catch { }
            }
            var usb = UsbScannerPresence.TryDetect();
            var lines = new List<string>
            {
                $"Visual C++ runtime: {Mark(vc)}",
                $"SecuGen library (sgfplib.dll): {Mark(lib)}",
                $"SecuGen driver modules (sgfdu*.dll): {(modules.Count == 0 ? "MISSING" : "OK (" + modules.Count + ")")}",
                $"Scanner on USB: {(usb is null ? "unknown" : usb.Value ? "detected" : "NOT detected")}",
            };
            if (!string.IsNullOrEmpty(LastFailure)) lines.Add($"Last error: {LastFailure}");
            lines.Add(!vc ? "Next: install the Visual C++ runtime (Install Scanner Driver does this)."
                : modules.Count == 0 ? "Next: press Install Scanner Driver, then re-plug the scanner."
                : usb == false ? "Next: plug the scanner in (try another USB 2.0 port)."
                : "Everything needed is present. Press Start Scan.");
            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception e)
        {
            return $"Scanner check failed: {e.Message}";
        }
    }

    private static PreflightResult RunPreflight()
    {
        var baseDir = AppContext.BaseDirectory;
        var procArch = RuntimeInformation.ProcessArchitecture;
        Trace.TraceInformation(
            $"[ScannerDiagnostics] PREFLIGHT process={procArch} os={RuntimeInformation.OSArchitecture} " +
            $"is64BitProcess={Environment.Is64BitProcess} baseDir={baseDir} cwd={Environment.CurrentDirectory}");

        ushort expectedMachine = procArch switch
        {
            Architecture.X64 => 0x8664,
            Architecture.X86 => 0x014C,
            _ => 0
        };
        if (expectedMachine == 0)
            Trace.TraceWarning($"[ScannerDiagnostics] PREFLIGHT process architecture {procArch} has no SecuGen build in vendor/SecuGen");

        // Managed wrapper. Touching the type loads the C++/CLI assembly, which itself needs the
        // Visual C++ runtime — if that fails, the exception below IS the diagnosis.
        try
        {
            var location = GetManagedAssemblyLocation();
            var machine = ReadPeMachine(location);
            Trace.TraceInformation($"[ScannerDiagnostics] PREFLIGHT managed SDK assembly={location} machine={MachineName(machine)}");
            if (expectedMachine != 0 && machine != 0 && machine != expectedMachine)
                Trace.TraceError($"[ScannerDiagnostics] PREFLIGHT ARCHITECTURE MISMATCH managed SDK assembly is {MachineName(machine)} but process is {procArch}");
        }
        catch (Exception e)
        {
            Trace.TraceError($"[ScannerDiagnostics] PREFLIGHT managed SDK assembly failed to load: {e.GetType().Name}: {e.Message}");
        }

        // Visual C++ 2015-2022 runtime (the SecuGen managed wrapper and sgwsqlib.dll link to it).
        var sysDir = Environment.SystemDirectory;
        foreach (var crt in new[] { "vcruntime140.dll", "msvcp140.dll" })
        {
            var found = File.Exists(Path.Combine(baseDir, crt)) || File.Exists(Path.Combine(sysDir, crt));
            Trace.TraceInformation($"[ScannerDiagnostics] PREFLIGHT {crt} present={found}");
            if (!found)
                Trace.TraceWarning($"[ScannerDiagnostics] PREFLIGHT {crt} not found — the Microsoft Visual C++ 2015-2022 Redistributable ({procArch}) may be missing");
        }

        // Native SecuGen DLLs beside the executable.
        foreach (var name in new[] { "sgfplib.dll", "sgfpamx.dll", "sgwsqlib.dll" })
        {
            var path = Path.Combine(baseDir, name);
            if (!File.Exists(path))
            {
                Trace.TraceWarning($"[ScannerDiagnostics] PREFLIGHT {name} NOT beside executable");
                continue;
            }
            var machine = ReadPeMachine(path);
            var ok = expectedMachine == 0 || machine == expectedMachine;
            Trace.TraceInformation($"[ScannerDiagnostics] PREFLIGHT {name} machine={MachineName(machine)} matchesProcess={ok}");
        }

        // Device-specific SecuGen driver modules (sgfduNN*.dll) that sgfplib.dll loads at Init time
        // for the attached model. They are not in the manual's required-DLL list, but their absence
        // from BOTH locations below is the first thing to check if Init returns a DLLLOAD error.
        LogMatchingFiles("beside executable", baseDir);
        LogMatchingFiles("system directory", sysDir);

        // Definitive test: can sgfplib.dll actually be loaded into this process? Loading it by full
        // path first also pins the vendor copy that shipped with the app, so the SDK's own later
        // by-name LoadLibrary resolves to it regardless of working directory or search path.
        var libPath = Path.Combine(baseDir, "sgfplib.dll");
        IntPtr handle;
        int err = 0;
        if (File.Exists(libPath))
        {
            handle = LoadLibraryExW(libPath, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
            if (handle == IntPtr.Zero) err = Marshal.GetLastWin32Error();
        }
        else
        {
            handle = LoadLibraryExW("sgfplib.dll", IntPtr.Zero, 0);
            if (handle == IntPtr.Zero) err = Marshal.GetLastWin32Error();
        }

        if (handle == IntPtr.Zero)
        {
            var summary = $"sgfplib.dll could not be loaded (Win32 error {err}: {DescribeWin32(err)})";
            Trace.TraceError($"[ScannerDiagnostics] PREFLIGHT FAILED {summary}");
            return new PreflightResult(false, summary);
        }

        Trace.TraceInformation("[ScannerDiagnostics] PREFLIGHT sgfplib.dll loaded OK");
        return new PreflightResult(true, "ok");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string GetManagedAssemblyLocation() =>
        typeof(SecuGen.FDxSDKPro.Windows.SGFingerPrintManager).Assembly.Location;

    private static void LogMatchingFiles(string label, string dir)
    {
        try
        {
            var names = Directory.GetFiles(dir, "sgfdu*.dll").Select(Path.GetFileName).OrderBy(n => n).ToArray();
            Trace.TraceInformation($"[ScannerDiagnostics] PREFLIGHT sgfdu*.dll {label}: {(names.Length == 0 ? "(none)" : string.Join(", ", names))}");
        }
        catch (Exception e)
        {
            Trace.TraceWarning($"[ScannerDiagnostics] PREFLIGHT could not list sgfdu*.dll {label}: {e.Message}");
        }
    }

    private static ushort ReadPeMachine(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);
            fs.Seek(0x3C, SeekOrigin.Begin);
            var peOffset = br.ReadInt32();
            fs.Seek(peOffset, SeekOrigin.Begin);
            if (br.ReadUInt32() != 0x00004550) return 0; // "PE\0\0"
            return br.ReadUInt16();
        }
        catch
        {
            return 0;
        }
    }

    private static string MachineName(ushort machine) => machine switch
    {
        0x8664 => "x64",
        0x014C => "x86",
        0xAA64 => "arm64",
        0 => "unknown",
        _ => $"0x{machine:X4}"
    };

    private static string DescribeWin32(int code) => code switch
    {
        126 => "module or one of its dependencies not found — often a missing Visual C++ runtime or companion SecuGen DLL",
        127 => "procedure not found",
        193 => "not a valid Win32 application — x86/x64 mismatch",
        _ => new System.ComponentModel.Win32Exception(code).Message
    };

    private const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string lpFileName, IntPtr hFile, uint dwFlags);
}

/// <summary>
/// Windows counterpart of Android's <c>FingerprintKioskService.isScannerConnected</c>: a cheap,
/// side-effect-free check of the OS's own device list for a SecuGen USB vendor ID (0x1162 =
/// 4450, same VID the Android device_filter.xml lists). It creates NO SecuGen SDK object and
/// opens NO device, so it can never compete with the one persistent connection owned by
/// <see cref="ScannerHub"/>.
/// </summary>
public static class UsbScannerPresence
{
    private const string SecuGenVid = "VID_1162";
    private const uint DIGCF_PRESENT = 0x2;
    private const uint DIGCF_ALLCLASSES = 0x4;
    private static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>true = a SecuGen USB device is currently present; false = none is; null = the
    /// OS query itself failed (answer unknown — callers must not treat that as "detached").</summary>
    public static bool? TryDetect()
    {
        IntPtr set = IntPtr.Zero;
        try
        {
            set = SetupDiGetClassDevsW(IntPtr.Zero, "USB", IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (set == InvalidHandle || set == IntPtr.Zero) return null;

            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            var buffer = new StringBuilder(512);
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                buffer.Clear();
                if (SetupDiGetDeviceInstanceIdW(set, ref data, buffer, (uint)buffer.Capacity, out _)
                    && buffer.ToString().Contains(SecuGenVid, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            // Enumeration ended: either the list is exhausted (ERROR_NO_MORE_ITEMS = 259) or failed.
            return Marshal.GetLastWin32Error() == 259 ? false : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (set != IntPtr.Zero && set != InvalidHandle) SetupDiDestroyDeviceInfoList(set);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public UIntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, StringBuilder deviceInstanceId, uint deviceInstanceIdSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
}
