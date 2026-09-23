namespace Pickle.Wizards.Tests;

public class WizardsPluginTests
{
    [Fact]
    public void HasStableId() => Assert.Equal("pickle.wizards", new WizardsPlugin().Id);
}
