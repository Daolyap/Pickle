using System.Globalization;
using System.Net;

namespace Pickle.Core.SystemMonitoring;

/// <summary>One line of <c>/proc/&lt;pid&gt;/stat</c> (times in clock ticks, resident set in pages).</summary>
internal readonly record struct ProcStat(int Pid, string Comm, char State, int ParentId, long UserTicks, long SystemTicks, int Threads, long StartTicks, long RssPages);

internal readonly record struct DiskStat(string Device, long SectorsRead, long SectorsWritten);

internal readonly record struct ProcNetEntry(IPAddress Local, int LocalPort, IPAddress Remote, int RemotePort, string State, long Inode);

/// <summary>Parsers for the Linux <c>/proc</c> files the system panels read. Pure functions over file text.</summary>
internal static class ProcFs
{
    /// <summary>USER_HZ: the unit of the times in <c>/proc/&lt;pid&gt;/stat</c>; 100 on every mainstream kernel.</summary>
    public const int ClockTicksPerSecond = 100;

    public const int SectorSize = 512;

    public static ProcStat? ParseStat(string text)
    {
        // comm may contain spaces and parentheses: it runs from the first '(' to the last ')'.
        var open = text.IndexOf('(', StringComparison.Ordinal);
        var close = text.LastIndexOf(')');
        if (open <= 0 || close < open || !int.TryParse(text.AsSpan(0, open).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
        {
            return null;
        }

        var fields = text[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 22)
        {
            return null;
        }

        long L(int index) => long.TryParse(fields[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        return new ProcStat(
            pid,
            text[(open + 1)..close],
            fields[0].Length > 0 ? fields[0][0] : '?',
            (int)L(1),
            L(11),
            L(12),
            (int)L(17),
            L(19),
            L(21));
    }

    /// <summary>Busy and total jiffies from the aggregate <c>cpu</c> line of <c>/proc/stat</c>.</summary>
    public static (long Busy, long Total)? ParseCpu(string procStat)
    {
        foreach (var line in procStat.Split('\n'))
        {
            if (!line.StartsWith("cpu ", StringComparison.Ordinal))
            {
                continue;
            }

            var values = line[4..].Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(v => long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0)
                .ToArray();
            if (values.Length < 4)
            {
                return null;
            }

            // user nice system idle iowait irq softirq steal (guest time is already part of user).
            var total = values.Take(8).Sum();
            var idle = values[3] + (values.Length > 4 ? values[4] : 0);
            return (total - idle, total);
        }

        return null;
    }

    /// <summary>Boot time (<c>btime</c> in <c>/proc/stat</c>).</summary>
    public static DateTimeOffset? ParseBootTime(string procStat)
    {
        foreach (var line in procStat.Split('\n'))
        {
            if (line.StartsWith("btime ", StringComparison.Ordinal) &&
                long.TryParse(line.AsSpan(6).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
        }

        return null;
    }

    /// <summary>Used and total memory in bytes (used = MemTotal − MemAvailable).</summary>
    public static (long Used, long Total)? ParseMemInfo(string text)
    {
        long? total = null, available = null, free = null;
        foreach (var line in text.Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var parts = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb))
            {
                continue;
            }

            switch (line[..colon])
            {
                case "MemTotal":
                    total = kb * 1024;
                    break;
                case "MemAvailable":
                    available = kb * 1024;
                    break;
                case "MemFree":
                    free = kb * 1024;
                    break;
            }
        }

        return total is { } t && (available ?? free) is { } a ? (Math.Max(0, t - a), t) : null;
    }

    /// <summary>The real user id from <c>/proc/&lt;pid&gt;/status</c>.</summary>
    public static int? ParseUid(string status)
    {
        foreach (var line in status.Split('\n'))
        {
            if (line.StartsWith("Uid:", StringComparison.Ordinal))
            {
                var parts = line[4..].Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
                return parts.Length > 0 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var uid) ? uid : null;
            }
        }

        return null;
    }

    /// <summary>uid → user name from <c>/etc/passwd</c>.</summary>
    public static Dictionary<int, string> ParsePasswd(string text)
    {
        var users = new Dictionary<int, string>();
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Split(':');
            if (parts.Length > 2 && parts[0].Length > 0 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var uid))
            {
                users.TryAdd(uid, parts[0]);
            }
        }

        return users;
    }

    public static IEnumerable<DiskStat> ParseDiskStats(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 10)
            {
                continue;
            }

            if (long.TryParse(fields[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var read) &&
                long.TryParse(fields[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var written))
            {
                yield return new DiskStat(fields[2], read, written);
            }
        }
    }

    /// <summary>Entries of <c>/proc/net/{tcp,tcp6,udp,udp6}</c>.</summary>
    public static IEnumerable<ProcNetEntry> ParseNet(string text, bool udp)
    {
        foreach (var line in text.Split('\n').Skip(1))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 10 ||
                ParseEndpoint(fields[1]) is not { } local ||
                ParseEndpoint(fields[2]) is not { } remote ||
                !int.TryParse(fields[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var state) ||
                !long.TryParse(fields[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var inode))
            {
                continue;
            }

            yield return new ProcNetEntry(local.Address, local.Port, remote.Address, remote.Port, udp ? "Listen" : TcpStateName(state), inode);
        }
    }

    /// <summary>"0100007F:0CEA" → 127.0.0.1:3306. The kernel prints each 32-bit word of the address in host byte order.</summary>
    public static (IPAddress Address, int Port)? ParseEndpoint(string text)
    {
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0 || !int.TryParse(text.AsSpan(colon + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var port))
        {
            return null;
        }

        var hex = text.AsSpan(0, colon);
        if (hex.Length is not (8 or 32))
        {
            return null;
        }

        var bytes = new byte[hex.Length / 2];
        for (var word = 0; word < hex.Length / 8; word++)
        {
            if (!uint.TryParse(hex.Slice(word * 8, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            BitConverter.GetBytes(value).CopyTo(bytes, word * 4);
        }

        return (new IPAddress(bytes), port);
    }

    private static string TcpStateName(int state) => state switch
    {
        0x01 => "Established",
        0x02 => "SynSent",
        0x03 or 0x0C => "SynReceived",
        0x04 => "FinWait1",
        0x05 => "FinWait2",
        0x06 => "TimeWait",
        0x07 => "Closed",
        0x08 => "CloseWait",
        0x09 => "LastAck",
        0x0A => "Listen",
        0x0B => "Closing",
        _ => "Unknown",
    };
}
