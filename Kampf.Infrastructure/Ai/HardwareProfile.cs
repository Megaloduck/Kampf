using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Kampf.Infrastructure.Ai;

/// <summary>
/// What this machine brings to a local model: how many threads it can run and
/// how much memory is actually free right now. Inference here is CPU-only, so
/// these are the numbers that decide whether a GGUF fits and how quickly it can
/// answer.
/// </summary>
public sealed record HardwareProfile(
    int LogicalCores,
    long TotalMemoryBytes,
    long AvailableMemoryBytes)
{
    public double TotalMemoryGb => TotalMemoryBytes / 1073741824d;

    public double AvailableMemoryGb => AvailableMemoryBytes / 1073741824d;
}

/// <summary>
/// Reads the machine's real memory rather than guessing from the process's GC
/// budget: Windows through <c>GlobalMemoryStatusEx</c>, Linux through
/// <c>/proc/meminfo</c>, macOS through <c>sysctl</c>. Anything that cannot be
/// read falls back to the runtime's view, so the page always has numbers.
/// </summary>
public static class HardwareProbe
{
    public static HardwareProfile Detect()
    {
        var cores = Math.Max(1, Environment.ProcessorCount);

        long total = 0;
        long available = 0;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                (total, available) = WindowsMemory();
            }
            else if (OperatingSystem.IsLinux())
            {
                (total, available) = LinuxMemory();
            }
            else if (OperatingSystem.IsMacOS())
            {
                total = MacTotalMemory();
                available = MacAvailableMemory();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException)
        {
            // A locked-down machine can refuse these reads; the fallback below
            // still produces a usable profile.
        }

        var runtimeMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

        if (total <= 0)
            total = runtimeMemory;

        if (available <= 0)
            available = OperatingSystem.IsWindows() || OperatingSystem.IsLinux()
                ? total
                : runtimeMemory;

        if (total <= 0)
            total = 1;

        if (available <= 0 || available > total)
            available = total;

        return new HardwareProfile(cores, total, available);
    }

    // ------------------------------------------------------------------ Windows

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    private static (long Total, long Available) WindowsMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };

        if (!GlobalMemoryStatusEx(ref status))
            return (0, 0);

        return ((long)status.TotalPhysical, (long)status.AvailablePhysical);
    }

    // -------------------------------------------------------------------- Linux

    private static (long Total, long Available) LinuxMemory()
    {
        long total = 0;
        long available = 0;

        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                total = Kilobytes(line);
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                available = Kilobytes(line);
        }

        return (total, available);
    }

    private static long Kilobytes(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], out var value) ? value * 1024 : 0;
    }

    // -------------------------------------------------------------------- macOS

    private static long MacTotalMemory()
    {
        using var process = Process.Start(new ProcessStartInfo("sysctl", "-n hw.memsize")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        if (process is null)
            return 0;

        var text = process.StandardOutput.ReadToEnd();
        process.WaitForExit(2000);

        return long.TryParse(text.Trim(), out var bytes) ? bytes : 0;
    }

    /// <summary>
    /// macOS keeps RAM it can hand out as free plus inactive pages, so both are
    /// counted; the page size is printed in <c>vm_stat</c>'s own header.
    /// </summary>
    private static long MacAvailableMemory()
    {
        using var process = Process.Start(new ProcessStartInfo("vm_stat")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        if (process is null)
            return 0;

        var text = process.StandardOutput.ReadToEnd();
        process.WaitForExit(2000);

        var lines = text.Split('\n');
        long pageSize = 4096;

        var sizeAt = lines[0].IndexOf("page size of ", StringComparison.Ordinal);
        if (sizeAt >= 0)
        {
            var rest = lines[0][(sizeAt + "page size of ".Length)..];
            var end = rest.IndexOf(' ');
            if (end > 0 && long.TryParse(rest[..end], out var parsed))
                pageSize = parsed;
        }

        long pages = 0;

        foreach (var line in lines)
        {
            if (line.StartsWith("Pages free:", StringComparison.Ordinal)
                || line.StartsWith("Pages inactive:", StringComparison.Ordinal))
            {
                var digits = new string(line.Where(char.IsDigit).ToArray());
                if (long.TryParse(digits, out var value))
                    pages += value;
            }
        }

        return pages * pageSize;
    }
}
