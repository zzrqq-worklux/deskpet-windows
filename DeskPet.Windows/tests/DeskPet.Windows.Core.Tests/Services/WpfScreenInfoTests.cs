using System.Linq;
using DeskPet.Windows.Services;
using Xunit;

namespace DeskPet.Windows.Core.Tests.Services;

public class WpfScreenInfoTests
{
    [Fact]
    public void ReportsAtLeastOneScreen()
    {
        var info = new WpfScreenInfo();

        Assert.NotEmpty(info.WorkAreas);
    }

    [Fact]
    public void PrimaryIsListedFirst()
    {
        var info = new WpfScreenInfo();

        Assert.Equal(info.PrimaryWorkArea, info.WorkAreas.First());
    }

    [Fact]
    public void AllRectanglesHavePositiveSize()
    {
        var info = new WpfScreenInfo();

        foreach (var r in info.WorkAreas)
        {
            Assert.True(r.Width > 0);
            Assert.True(r.Height > 0);
        }
    }
}
