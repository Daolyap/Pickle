using Pickle.Windows.Storage;

namespace Pickle.Windows.Tests.Storage;

public sealed class DiskLayoutServiceTests
{
    [Fact]
    public void ParsesTheScriptsJson()
    {
        const string Json = """
            [{"Number":0,"Name":"Samsung SSD 980","PartitionStyle":"GPT","Size":1000204886016,"AllocatedSize":1000203837440,
              "Status":"Online","IsOffline":false,"IsReadOnly":false,"IsBoot":true,"IsSystem":true,"BusType":"NVMe",
              "Partitions":[
                {"DiskNumber":0,"PartitionNumber":1,"DriveLetter":null,"Size":104857600,"Type":"System","IsBoot":false,"IsSystem":true,"FileSystem":"FAT32","Label":"","Free":71303168},
                {"DiskNumber":0,"PartitionNumber":3,"DriveLetter":"c","Size":999000000000,"Type":"Basic","IsBoot":true,"IsSystem":false,"FileSystem":"NTFS","Label":"Windows","Free":400000000000}]},
             {"Number":2,"Name":"Blank","PartitionStyle":"RAW","Size":500107862016,"AllocatedSize":0,"Status":"Offline","IsOffline":true,
              "IsReadOnly":false,"IsBoot":false,"IsSystem":false,"BusType":"SATA","Partitions":[]}]
            """;

        var disks = DiskLayoutService.Parse(Json);

        Assert.Equal([0, 2], disks.Select(d => d.Number));
        Assert.Equal([null, 'C'], disks[0].Partitions.Select(p => p.DriveLetter));
        Assert.Null(disks[0].Partitions[0].Label);
        Assert.Equal(400000000000, disks[0].Partitions[1].FreeBytes);
        Assert.True(disks[1].IsRaw);
        Assert.True(disks[1].IsOffline);
        Assert.Equal(500107862016, disks[1].UnallocatedBytes);
    }

    [Fact]
    public void AcceptsASingleDiskObjectAndEmptyOutput()
    {
        Assert.Single(DiskLayoutService.Parse("""{"Number":1,"Name":"USB","Partitions":{"DiskNumber":1,"PartitionNumber":1,"DriveLetter":"E"}}""")[0].Partitions);
        Assert.Empty(DiskLayoutService.Parse(" "));
    }

    [Fact]
    public void TheScriptParsesAsPowerShell()
    {
        System.Management.Automation.Language.Parser.ParseInput(DiskLayoutService.Script, out _, out var errors);
        Assert.Empty(errors);
    }
}
