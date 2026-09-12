using System.Runtime.InteropServices;
using System.Text;

namespace ZenLoop.Core;

/// <summary>
/// RTSS-grade OSD path: publish Optimize / live telemetry into
/// (1) RivaTuner Statistics Server shared-memory OSD slots (SDK custom-layer pattern),
/// (2) a documented ZenLoop named shared-memory block for OverlayEditor plugins,
/// (3) a plain UTF-8 OSD text file for file-based layers.
/// Keeps <see cref="MetricsSnapshotExport"/> JSON as the durable session export.
/// Fail-closed when RTSS is absent — never invents overlay content.
/// </summary>
public static class RtssOsdBridge
{
    public const string OwnerId = "ZenLoop";
    public const string RtssMapName = "RTSSSharedMemoryV2";
    public const string ZenLoopMapName = "Local\\ZenLoopMetricsOsd";
    public const uint ZenLoopSignature = 0x54524C5A; // 'ZLRT'
    public const uint ZenLoopLayoutVersion = 1;
    public const string OsdTextFileName = "zenloop-osd.txt";

    public const uint RtssSignatureLive = 0x53535452; // 'RTSS'
    public const uint RtssSignatureDead = 0x44454144; // 'DEAD'

    /// <summary>%LocalAppData%\ZenLoop\zenloop-osd.txt</summary>
    public static string DefaultOsdTextPath =>
        Path.Combine(MetricsSnapshotExport.DefaultDirectory, OsdTextFileName);

    public static string InstallHelp =>
        "RTSS OSD path (not a fake overlay):" + Environment.NewLine
        + "1. Install RivaTuner Statistics Server (RTSS) from the MSI Afterburner package or guru3D." + Environment.NewLine
        + "2. Keep AMD Adrenalin as the only GPU controller — do not enable Afterburner GPU control on Radeon." + Environment.NewLine
        + "3. ZenLoop claims an RTSS OSD slot (owner ZenLoop) via RTSSSharedMemoryV2 when RTSS is running." + Environment.NewLine
        + $"4. Also publishes {ZenLoopMapName} and {DefaultOsdTextPath} for OverlayEditor / custom layers." + Environment.NewLine
        + $"5. Session JSON remains at {MetricsSnapshotExport.DefaultPath} (schema {MetricsSnapshotExport.SchemaVersion})." + Environment.NewLine
        + "See docs/RTSS-OSD.md.";

    /// <summary>Single-line / multi-line OSD text from a metrics snapshot (pure; unit-testable).</summary>
    public static string FormatOsdText(MetricsSnapshot snap, bool multiLine = true)
    {
        ArgumentNullException.ThrowIfNull(snap);
        var sep = multiLine ? "\n" : " | ";
        var parts = new List<string> { "ZenLoop" };

        if (!string.IsNullOrWhiteSpace(snap.Result) && snap.Result != "Unknown")
            parts.Add(snap.Result);
        if (!string.IsNullOrWhiteSpace(snap.Goal))
            parts.Add(snap.Goal!);

        if (snap.ScoreBefore is double sb && snap.ScoreAfter is double sa)
            parts.Add($"score {sb:0.#}->{sa:0.#}");
        else if (snap.Session?.SpeedPct is double sp)
            parts.Add($"speed {sp:+0.0;-0.0}%");

        if (snap.Live?.CpuTempC is double ct)
            parts.Add($"CPU {ct:0.#}C");
        if (snap.Live?.HotspotC is double hs)
            parts.Add($"HS {hs:0.#}C");
        else if (snap.Live?.GpuTempC is double gt)
            parts.Add($"GPU {gt:0.#}C");
        if (snap.Live?.BoardPowerW is double bp)
            parts.Add($"{bp:0.#}W");
        else if (snap.Live?.CpuPowerW is double cp)
            parts.Add($"PPT {cp:0.#}W");

        if (parts.Count == 1 && !string.IsNullOrWhiteSpace(snap.Summary))
            parts.Add(snap.Summary!);

        return string.Join(sep, parts);
    }

    /// <summary>
    /// Publish OSD text + shared memory. JSON export is separate (<see cref="MetricsSnapshotExport.Write"/>).
    /// Returns a result describing which channels succeeded.
    /// </summary>
    public static RtssPublishResult Publish(MetricsSnapshot snap, string? osdTextPath = null)
    {
        ArgumentNullException.ThrowIfNull(snap);
        var text = FormatOsdText(snap, multiLine: true);
        var path = string.IsNullOrWhiteSpace(osdTextPath) ? DefaultOsdTextPath : osdTextPath!;
        var fileOk = TryWriteOsdTextFile(text, path);
        var zlOk = TryPublishZenLoopSharedMemory(snap, text);
        var rtssOk = TryPublishRtssOsd(text);
        return new RtssPublishResult(rtssOk, zlOk, fileOk, path, text);
    }

    public static bool TryWriteOsdTextFile(string text, string? path = null)
    {
        try
        {
            var target = string.IsNullOrWhiteSpace(path) ? DefaultOsdTextPath : path!;
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var tmp = target + ".tmp";
            File.WriteAllText(tmp, text ?? "", Encoding.UTF8);
            try
            {
                if (File.Exists(target))
                    File.Replace(tmp, target, destinationBackupFileName: null);
                else
                    File.Move(tmp, target);
            }
            catch (IOException)
            {
                File.Copy(tmp, target, overwrite: true);
                try { File.Delete(tmp); } catch { /* ignore */ }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Documented ZenLoop MMF layout (see docs/RTSS-OSD.md). Works on Windows only.
    /// On non-Windows returns false without throwing.
    /// </summary>
    public static bool TryPublishZenLoopSharedMemory(MetricsSnapshot snap, string? osdText = null)
    {
        if (!OperatingSystem.IsWindows()) return false;
        ArgumentNullException.ThrowIfNull(snap);
        try
        {
            osdText ??= FormatOsdText(snap);
            var bytes = BuildZenLoopPayload(snap, osdText);
            return NativeMm.WriteNamedMap(ZenLoopMapName, bytes);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Build the ZenLoop MMF payload (pure; unit-testable).</summary>
    public static byte[] BuildZenLoopPayload(MetricsSnapshot snap, string osdText)
    {
        ArgumentNullException.ThrowIfNull(snap);
        // Fixed layout: header + doubles + fixed strings
        // 4+4+4+8 = 20 header; 6*8 = 48 doubles; result[16]+goal[32]+osd[512] = 560; total 628
        const int size = 628;
        var buf = new byte[size];
        void U32(int off, uint v) => BitConverter.TryWriteBytes(buf.AsSpan(off, 4), v);
        void I64(int off, long v) => BitConverter.TryWriteBytes(buf.AsSpan(off, 8), v);
        void F64(int off, double? v)
        {
            var d = v ?? double.NaN;
            BitConverter.TryWriteBytes(buf.AsSpan(off, 8), d);
        }
        void Str(int off, int max, string? s)
        {
            var raw = Encoding.UTF8.GetBytes(s ?? "");
            var n = Math.Min(raw.Length, max - 1);
            if (n > 0) raw.AsSpan(0, n).CopyTo(buf.AsSpan(off, n));
            buf[off + n] = 0;
        }

        U32(0, ZenLoopSignature);
        U32(4, ZenLoopLayoutVersion);
        U32(8, 1); // flags: updated
        I64(12, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        F64(20, snap.Live?.CpuTempC);
        F64(28, snap.Live?.CpuPowerW ?? snap.Live?.PptW);
        F64(36, snap.Live?.BoardPowerW);
        F64(44, snap.Live?.HotspotC ?? snap.Live?.GpuTempC);
        F64(52, snap.ScoreBefore ?? snap.Session?.ScoreBefore);
        F64(60, snap.ScoreAfter ?? snap.Session?.ScoreAfter);
        Str(68, 16, snap.Result);
        Str(84, 32, snap.Goal);
        Str(116, 512, osdText);
        return buf;
    }

    /// <summary>
    /// Claim / update an RTSS OSD slot owned by ZenLoop (RTSS SDK SharedMemory sample pattern).
    /// </summary>
    public static bool TryPublishRtssOsd(string text)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            return NativeRtss.UpdateOsd(OwnerId, text);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryReleaseRtssOsd()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return NativeRtss.ReleaseOsd(OwnerId);
        }
        catch
        {
            return false;
        }
    }
}

public readonly record struct RtssPublishResult(
    bool RtssSlot,
    bool ZenLoopSharedMemory,
    bool OsdTextFile,
    string OsdTextPath,
    string Text)
{
    public bool Any => RtssSlot || ZenLoopSharedMemory || OsdTextFile;

    public string Describe()
    {
        var bits = new List<string>();
        if (RtssSlot) bits.Add("RTSS OSD slot");
        if (ZenLoopSharedMemory) bits.Add("ZenLoop MMF");
        if (OsdTextFile) bits.Add($"file {OsdTextPath}");
        return bits.Count == 0 ? "no OSD channel available (is RTSS installed/running?)" : string.Join(" + ", bits);
    }
}

static class NativeMm
{
    const uint PageReadWrite = 0x04;
    const uint FileMapAllAccess = 0x000F001F;

    public static bool WriteNamedMap(string name, byte[] payload)
    {
        var h = CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, PageReadWrite, 0, (uint)payload.Length, name);
        if (h == IntPtr.Zero) return false;
        try
        {
            var view = MapViewOfFile(h, FileMapAllAccess, 0, 0, (UIntPtr)(uint)payload.Length);
            if (view == IntPtr.Zero) return false;
            try
            {
                Marshal.Copy(payload, 0, view, payload.Length);
                return true;
            }
            finally
            {
                UnmapViewOfFile(view);
            }
        }
        finally
        {
            CloseHandle(h);
        }
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileMappingW(
        IntPtr hFile, IntPtr attrs, uint protect, uint maxHi, uint maxLo, string name);

    [DllImport("kernel32", SetLastError = true)]
    static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint offHi, uint offLo, UIntPtr size);

    [DllImport("kernel32", SetLastError = true)]
    static extern bool UnmapViewOfFile(IntPtr view);

    [DllImport("kernel32", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
}

/// <summary>Minimal RTSSSharedMemoryV2 writer (Unwinder SDK layout).</summary>
static class NativeRtss
{
    const uint FileMapAllAccess = 0x000F001F;

    public static bool UpdateOsd(string owner, string text)
    {
        var h = OpenFileMappingW(FileMapAllAccess, false, RtssOsdBridge.RtssMapName);
        if (h == IntPtr.Zero) return false;
        try
        {
            var view = MapViewOfFile(h, FileMapAllAccess, 0, 0, UIntPtr.Zero);
            if (view == IntPtr.Zero) return false;
            try
            {
                var sig = (uint)Marshal.ReadInt32(view, 0);
                if (sig == RtssOsdBridge.RtssSignatureDead) return false;
                if (sig != RtssOsdBridge.RtssSignatureLive) return false;

                var version = (uint)Marshal.ReadInt32(view, 4);
                if ((version & 0xFFFF0000) != 0x00020000) return false;

                var osdEntrySize = (uint)Marshal.ReadInt32(view, 20);
                var osdArrOffset = (uint)Marshal.ReadInt32(view, 24);
                var osdArrSize = (uint)Marshal.ReadInt32(view, 28);
                if (osdEntrySize < 512 || osdArrSize == 0 || osdArrSize > 256) return false;

                // Prefer an existing owned slot; else first empty owner.
                int? empty = null;
                for (uint i = 0; i < osdArrSize; i++)
                {
                    var entry = IntPtr.Add(view, (int)(osdArrOffset + i * osdEntrySize));
                    var slotOwner = ReadAnsi(entry, 256, 256); // szOSDOwner at +256
                    if (string.Equals(slotOwner, owner, StringComparison.Ordinal))
                    {
                        WriteSlot(entry, osdEntrySize, owner, text);
                        BumpFrame(view);
                        return true;
                    }
                    if (empty is null && string.IsNullOrEmpty(slotOwner))
                        empty = (int)i;
                }

                if (empty is int idx)
                {
                    var entry = IntPtr.Add(view, (int)(osdArrOffset + (uint)idx * osdEntrySize));
                    WriteSlot(entry, osdEntrySize, owner, text);
                    BumpFrame(view);
                    return true;
                }

                return false;
            }
            finally
            {
                UnmapViewOfFile(view);
            }
        }
        finally
        {
            CloseHandle(h);
        }
    }

    public static bool ReleaseOsd(string owner)
    {
        var h = OpenFileMappingW(FileMapAllAccess, false, RtssOsdBridge.RtssMapName);
        if (h == IntPtr.Zero) return false;
        try
        {
            var view = MapViewOfFile(h, FileMapAllAccess, 0, 0, UIntPtr.Zero);
            if (view == IntPtr.Zero) return false;
            try
            {
                var sig = (uint)Marshal.ReadInt32(view, 0);
                if (sig != RtssOsdBridge.RtssSignatureLive) return false;
                var osdEntrySize = (uint)Marshal.ReadInt32(view, 20);
                var osdArrOffset = (uint)Marshal.ReadInt32(view, 24);
                var osdArrSize = (uint)Marshal.ReadInt32(view, 28);
                for (uint i = 0; i < osdArrSize; i++)
                {
                    var entry = IntPtr.Add(view, (int)(osdArrOffset + i * osdEntrySize));
                    var slotOwner = ReadAnsi(entry, 256, 256);
                    if (!string.Equals(slotOwner, owner, StringComparison.Ordinal)) continue;
                    // Clear owner + text
                    WriteAnsi(entry, 0, 256, "");
                    WriteAnsi(entry, 256, 256, "");
                    if (osdEntrySize >= 256 + 256 + 4096)
                        WriteAnsi(entry, 512, 4096, "");
                    BumpFrame(view);
                    return true;
                }
                return false;
            }
            finally
            {
                UnmapViewOfFile(view);
            }
        }
        finally
        {
            CloseHandle(h);
        }
    }

    static void WriteSlot(IntPtr entry, uint entrySize, string owner, string text)
    {
        // szOSD[256] at 0; szOSDOwner[256] at 256; optional szOSDEx[4096] at 512
        WriteAnsi(entry, 0, 256, TruncateForAnsi(text, 255));
        WriteAnsi(entry, 256, 256, TruncateForAnsi(owner, 255));
        if (entrySize >= 512 + 4096)
            WriteAnsi(entry, 512, 4096, TruncateForAnsi(text, 4095));
    }

    static void BumpFrame(IntPtr view)
    {
        // dwOSDFrame at offset 32
        var frame = Marshal.ReadInt32(view, 32);
        Marshal.WriteInt32(view, 32, unchecked(frame + 1));
    }

    static string TruncateForAnsi(string s, int maxChars)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // RTSS slots are ANSI; strip non-ASCII to keep byte length == char length.
        var sb = new StringBuilder(Math.Min(s.Length, maxChars));
        foreach (var ch in s)
        {
            if (sb.Length >= maxChars) break;
            sb.Append(ch <= 0x7F ? ch : '?');
        }
        return sb.ToString();
    }

    static string ReadAnsi(IntPtr entry, int offset, int max)
    {
        var bytes = new byte[max];
        Marshal.Copy(IntPtr.Add(entry, offset), bytes, 0, max);
        var n = Array.IndexOf(bytes, (byte)0);
        if (n < 0) n = max;
        if (n <= 0) return "";
        return Encoding.ASCII.GetString(bytes, 0, n);
    }

    static void WriteAnsi(IntPtr entry, int offset, int max, string value)
    {
        var bytes = new byte[max];
        var raw = Encoding.ASCII.GetBytes(value ?? "");
        var n = Math.Min(raw.Length, max - 1);
        if (n > 0) Array.Copy(raw, 0, bytes, 0, n);
        Marshal.Copy(bytes, 0, IntPtr.Add(entry, offset), max);
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);

    [DllImport("kernel32", SetLastError = true)]
    static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint offHi, uint offLo, UIntPtr size);

    [DllImport("kernel32", SetLastError = true)]
    static extern bool UnmapViewOfFile(IntPtr view);

    [DllImport("kernel32", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
}
