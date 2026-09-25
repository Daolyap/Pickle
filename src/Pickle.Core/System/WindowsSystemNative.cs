using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Pickle.Core.SystemMonitoring;

/// <summary>
/// The few Win32 calls the system panels need that .NET doesn't expose: machine CPU and memory totals, a process's
/// image path, command line, parent and owner (all with PROCESS_QUERY_LIMITED_INFORMATION, so no admin rights),
/// and the TCP/UDP tables with owning process ids. Every method returns null on failure.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WindowsSystemNative
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int ProcessBasicInformation = 0;
    private const int ProcessCommandLineInformation = 60;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int StatusBufferTooSmall = unchecked((int)0xC0000023);
    private const uint ErrorInsufficientBuffer = 122;
    private const uint AfInet = 2;
    private const uint AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;

    private static readonly ConcurrentDictionary<string, string> AccountNames = new(StringComparer.Ordinal);

    /// <summary>Busy and total 100 ns ticks across all processors since boot.</summary>
    public static (long Busy, long Total)? SystemTimes()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return null;
        }

        // Kernel time includes idle time.
        var total = kernel + user;
        return (total - idle, total);
    }

    public static (long Used, long Total)? Memory()
    {
        var status = new MemoryStatusEx { Length = (uint)sizeof(MemoryStatusEx) };
        if (!GlobalMemoryStatusEx(ref status))
        {
            return null;
        }

        return ((long)(status.TotalPhys - status.AvailPhys), (long)status.TotalPhys);
    }

    public static string? ImagePath(int processId) => WithProcess(processId, handle =>
    {
        foreach (var capacity in new[] { 1024, 32768 })
        {
            var buffer = new char[capacity];
            var size = (uint)capacity;
            fixed (char* p = buffer)
            {
                if (QueryFullProcessImageName(handle, 0, p, ref size))
                {
                    return new string(buffer, 0, (int)size);
                }
            }

            if ((uint)Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer)
            {
                break;
            }
        }

        return null;
    });

    public static string? CommandLine(int processId) => WithProcess(processId, handle =>
    {
        var length = 4096u;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = NativeMemory.Alloc(length);
            try
            {
                var status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, length, out var needed);
                if (status is StatusInfoLengthMismatch or StatusBufferTooSmall && needed > length)
                {
                    length = needed;
                    continue;
                }

                if (status != 0)
                {
                    return null;
                }

                var text = (UnicodeString*)buffer;
                return text->Buffer == 0 ? string.Empty : new string((char*)text->Buffer, 0, text->Length / 2);
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }

        return null;
    });

    public static int? ParentId(int processId) => WithProcess<int?>(processId, handle =>
    {
        var info = default(BasicInformation);
        return NtQueryInformationProcess(handle, ProcessBasicInformation, &info, (uint)sizeof(BasicInformation), out _) == 0
            ? (int)info.InheritedFromUniqueProcessId
            : null;
    });

    /// <summary>The owner's account name without the domain (cached per SID).</summary>
    public static string? UserName(int processId) => WithProcess(processId, handle =>
    {
        if (!OpenProcessToken(handle, TokenQuery, out var token))
        {
            return null;
        }

        try
        {
            using var identity = new WindowsIdentity(token);
            if (identity.User is not { } sid)
            {
                return null;
            }

            return AccountNames.GetOrAdd(sid.Value, _ =>
            {
                try
                {
                    var name = sid.Translate(typeof(NTAccount)).Value;
                    return name[(name.LastIndexOf('\\') + 1)..];
                }
                catch (SystemException)
                {
                    return sid.Value;
                }
            });
        }
        finally
        {
            CloseHandle(token);
        }
    });

    /// <summary>A raw MIB_*TABLE_OWNER_PID buffer (see <see cref="ConnectionTables.ParseWindowsTable"/>).</summary>
    public static byte[]? ConnectionTable(bool udp, bool ipv6)
    {
        var family = ipv6 ? AfInet6 : AfInet;
        var size = 0u;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var buffer = size == 0 ? null : NativeMemory.Alloc(size);
            try
            {
                var result = udp
                    ? GetExtendedUdpTable(buffer, ref size, false, family, UdpTableOwnerPid, 0)
                    : GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidAll, 0);
                if (result == ErrorInsufficientBuffer)
                {
                    continue;
                }

                if (result != 0 || buffer is null)
                {
                    return null;
                }

                return new ReadOnlySpan<byte>(buffer, (int)size).ToArray();
            }
            finally
            {
                if (buffer is not null)
                {
                    NativeMemory.Free(buffer);
                }
            }
        }

        return null;
    }

    private static T? WithProcess<T>(int processId, Func<nint, T?> query)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == 0)
        {
            return default;
        }

        try
        {
            return query(handle);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInformation
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(nint process, uint flags, char* buffer, ref uint size);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(nint process, int informationClass, void* information, uint length, out uint returnLength);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(void* table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint addressFamily, int tableClass, uint reserved);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedUdpTable(void* table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint addressFamily, int tableClass, uint reserved);
}
