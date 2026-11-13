using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using PortVisualizer.Core.Models;

namespace PortVisualizer.Core.Processes;

public sealed record RawProcess(string Name, string? Path, DateTime? StartTimeUtc, bool AccessDenied);

/// <summary>Abstraction over Windows process inspection so resolution logic is testable.</summary>
public interface IProcessApi
{
    /// <summary>Cheap probe used every cycle to detect PID reuse. Null when unavailable.</summary>
    DateTime? GetStartTimeUtc(int pid);
    /// <summary>Full query. Returns null if the process no longer exists.</summary>
    RawProcess? Query(int pid);
}

public interface IProcessResolver
{
    ProcessInfo Resolve(int pid);
    /// <summary>Drops cache entries for PIDs not seen this cycle.</summary>
    void Prune(IReadOnlySet<int> activePids);
    bool RecordPaths { get; set; }
}

public sealed class ProcessResolver : IProcessResolver
{
    private readonly IProcessApi _api;
    private readonly Dictionary<int, ProcessInfo> _cache = new();

    public ProcessResolver(IProcessApi api) => _api = api;

    public bool RecordPaths { get; set; } = true;

    public ProcessInfo Resolve(int pid)
    {
        if (pid == 0) return _cache.TryGetValue(0, out var idle) ? idle : _cache[0] = new ProcessInfo(0, "System (no owner)", null, null);

        _cache.TryGetValue(pid, out var cached);
        var start = _api.GetStartTimeUtc(pid);

        if (cached is not null)
        {
            // Same PID but a different start time means the PID was reused by a new process.
            var sameProcess = start is null ? cached.StartTimeUtc is null || cached.AccessDenied : cached.StartTimeUtc == start;
            if (sameProcess) return cached;
        }

        RawProcess? raw;
        try { raw = _api.Query(pid); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            raw = new RawProcess($"pid {pid}", null, start, true);
        }

        var info = raw is null
            ? ProcessInfo.Missing(pid)
            : new ProcessInfo(pid, raw.Name, RecordPaths ? raw.Path : null, raw.StartTimeUtc ?? start, raw.AccessDenied);
        _cache[pid] = info;
        return info;
    }

    public void Prune(IReadOnlySet<int> activePids)
    {
        if (_cache.Count <= activePids.Count + 8) return;
        foreach (var pid in _cache.Keys.Where(p => !activePids.Contains(p)).ToList()) _cache.Remove(pid);
    }
}

public sealed class WindowsProcessApi : IProcessApi
{
    private const uint QueryLimited = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr h, int flags, char[] buffer, ref int size);

    public DateTime? GetStartTimeUtc(int pid)
    {
        var h = OpenProcess(QueryLimited, false, pid);
        if (h == IntPtr.Zero) return null;
        try { return GetProcessTimes(h, out var c, out _, out _, out _) ? DateTime.FromFileTimeUtc(c) : null; }
        finally { CloseHandle(h); }
    }

    public RawProcess? Query(int pid)
    {
        if (pid == 4) return new RawProcess("System", null, null, true);
        var h = OpenProcess(QueryLimited, false, pid);
        if (h == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            var name = FallbackName(pid);
            if (name is null && err == 87) return null; // ERROR_INVALID_PARAMETER: no such process
            return new RawProcess(name ?? $"pid {pid}", null, null, err == 5);
        }
        try
        {
            DateTime? start = GetProcessTimes(h, out var c, out _, out _, out _) ? DateTime.FromFileTimeUtc(c) : null;
            var buf = new char[1024];
            var size = buf.Length;
            string? path = QueryFullProcessImageName(h, 0, buf, ref size) ? new string(buf, 0, size) : null;
            var name = path is not null ? System.IO.Path.GetFileName(path) : FallbackName(pid) ?? $"pid {pid}";
            return new RawProcess(name, path, start, path is null);
        }
        finally { CloseHandle(h); }
    }

    private static string? FallbackName(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName + ".exe"; }
        catch { return null; }
    }
}

/// <summary>Adds publisher and signature information. Slow; only call on demand and cache by path.</summary>
public sealed class ProcessEnricher
{
    private readonly Dictionary<string, (string? Publisher, SignatureStatus Sig)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public void Enrich(ProcessInfo p)
    {
        if (p.Path is null || p.Signature != SignatureStatus.NotChecked) return;
        lock (_cache)
        {
            if (!_cache.TryGetValue(p.Path, out var r))
                _cache[p.Path] = r = Inspect(p.Path);
            p.Publisher = r.Publisher;
            p.Signature = r.Sig;
        }
    }

    private static (string?, SignatureStatus) Inspect(string path)
    {
        string? publisher = null;
        try { publisher = FileVersionInfo.GetVersionInfo(path).CompanyName; } catch { }
        var sig = SignatureChecker.Check(path);
        if (sig == SignatureStatus.Valid || sig == SignatureStatus.Invalid)
        {
            try
            {
                var cert = X509Certificate.CreateFromSignedFile(path);
                var subject = new X509Certificate2(cert).GetNameInfo(X509NameType.SimpleName, false);
                if (!string.IsNullOrWhiteSpace(subject)) publisher = subject;
            }
            catch { }
        }
        return (string.IsNullOrWhiteSpace(publisher) ? null : publisher, sig);
    }
}

/// <summary>Verifies embedded Authenticode signatures using WinVerifyTrust (no network revocation lookups).</summary>
public static class SignatureChecker
{
    private static readonly Guid ActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public int cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public int cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public int dwUIChoice;        // 2 = none
        public int fdwRevocationChecks; // 0 = none
        public int dwUnionChoice;     // 1 = file
        public IntPtr pFile;
        public int dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public int dwProvFlags;
        public int dwUIContext;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);

    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);

    public static SignatureStatus Check(string path)
    {
        var fileInfo = new WinTrustFileInfo { cbStruct = Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = path };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WinTrustData
            {
                cbStruct = Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2,
                fdwRevocationChecks = 0,
                dwUnionChoice = 1,
                pFile = pFile,
                dwProvFlags = 0x00000010, // WTD_CACHE_ONLY_URL_RETRIEVAL: never hit the network
            };
            var action = ActionGenericVerifyV2;
            var rc = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            return rc switch
            {
                0 => SignatureStatus.Valid,
                TrustENoSignature or TrustEProviderUnknown or TrustESubjectFormUnknown => SignatureStatus.NoEmbeddedSignature,
                _ => SignatureStatus.Invalid,
            };
        }
        catch { return SignatureStatus.Unknown; }
        finally { Marshal.FreeHGlobal(pFile); }
    }
}
