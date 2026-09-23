namespace Pickle.Windows.Tests;

public class PlatformGuardTests
{
    [Fact]
    public void PluginInitializesOnAnyPlatform()
    {
        var plugin = new WindowsPlugin();
        Assert.Equal("pickle.windows", plugin.Id);
    }
}
