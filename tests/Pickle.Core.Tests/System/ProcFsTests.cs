using System.Net;
using Pickle.Core.SystemMonitoring;

namespace Pickle.Core.Tests.SystemMonitoring;

public class ProcFsTests
{
    [Fact]
    public void StatHandlesSpacesAndParenthesesInTheCommandName()
    {
        var stat = ProcFs.ParseStat("4242 (my (odd) app) S 1 4242 4242 0 -1 4194304 85 0 0 0 150 50 0 0 20 0 7 0 177018 2920448 368 18446744073709551615 0 0 0");

        Assert.NotNull(stat);
        Assert.Equal(4242, stat.Value.Pid);
        Assert.Equal("my (odd) app", stat.Value.Comm);
        Assert.Equal('S', stat.Value.State);
        Assert.Equal(1, stat.Value.ParentId);
        Assert.Equal(150, stat.Value.UserTicks);
        Assert.Equal(50, stat.Value.SystemTicks);
        Assert.Equal(7, stat.Value.Threads);
        Assert.Equal(177018, stat.Value.StartTicks);
        Assert.Equal(368, stat.Value.RssPages);
        Assert.Null(ProcFs.ParseStat("garbage"));
        Assert.Null(ProcFs.ParseStat("12 (short) S 1 2"));
    }

    [Fact]
    public void CpuLineGivesBusyAndTotalJiffies()
    {
        const string text = "cpu  100 10 50 800 40 0 0 0 0 0\ncpu0 1 2 3 4 5 6 7 8 9 10\nbtime 1700000000\n";

        Assert.Equal((160L, 1000L), ProcFs.ParseCpu(text));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), ProcFs.ParseBootTime(text));
    }

    [Fact]
    public void MemInfoUsesMemAvailable()
    {
        const string text = "MemTotal:       16000 kB\nMemFree:         2000 kB\nMemAvailable:    6000 kB\n";

        Assert.Equal((10000L * 1024, 16000L * 1024), ProcFs.ParseMemInfo(text));
        Assert.Null(ProcFs.ParseMemInfo("Nothing: here\n"));
    }

    [Fact]
    public void UsersComeFromStatusAndPasswd()
    {
        Assert.Equal(1000, ProcFs.ParseUid("Name:\tbash\nUid:\t1000\t1000\t1000\t1000\n"));
        var users = ProcFs.ParsePasswd("root:x:0:0:root:/root:/bin/bash\nalice:x:1000:1000::/home/alice:/bin/sh\n#comment\n");
        Assert.Equal("root", users[0]);
        Assert.Equal("alice", users[1000]);
    }

    [Fact]
    public void EndpointsDecodeHostOrderWords()
    {
        Assert.Equal((IPAddress.Loopback, 3306), ProcFs.ParseEndpoint("0100007F:0CEA"));
        Assert.Equal((IPAddress.IPv6Loopback, 443), ProcFs.ParseEndpoint("00000000000000000000000001000000:01BB"));
        Assert.Null(ProcFs.ParseEndpoint("xyz"));
    }

    [Fact]
    public void NetTablesYieldConnectionsListenersAndInodes()
    {
        const string tcp = """
              sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
               0: 00000000:1F90 00000000:0000 0A 00000000:00000000 00:00000000 00000000     0        0 3144 1 0000000000000000 100 0 0 10 0
               1: 0100007F:A1B2 0100007F:1F90 01 00000000:00000000 00:00000000 00000000  1000        0 5555 1 0000000000000000 20 4 30 10 -1
            """;

        var entries = ProcFs.ParseNet(tcp, udp: false).ToList();

        Assert.Equal(2, entries.Count);
        Assert.Equal(("Listen", 8080, 3144L), (entries[0].State, entries[0].LocalPort, entries[0].Inode));
        Assert.Equal(("Established", 41394, 8080, 5555L), (entries[1].State, entries[1].LocalPort, entries[1].RemotePort, entries[1].Inode));
        Assert.Equal("Listen", ProcFs.ParseNet(tcp, udp: true).First().State);
    }

    [Fact]
    public void DiskStatsGiveSectorsReadAndWritten()
    {
        const string text = "   8       0 sda 100 0 2048 10 50 0 4096 20 0 30 30\n   7       0 loop0 0 0 0 0 0 0 0 0 0 0 0\n";

        var stats = ProcFs.ParseDiskStats(text).ToList();

        Assert.Equal(new DiskStat("sda", 2048, 4096), stats[0]);
        Assert.Equal("loop0", stats[1].Device);
    }
}
