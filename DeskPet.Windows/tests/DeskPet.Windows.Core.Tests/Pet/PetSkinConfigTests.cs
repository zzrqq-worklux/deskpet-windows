using DeskPet.Core.Pet;
using Xunit;

namespace DeskPet.Windows.Core.Tests.Pet;

public class PetSkinConfigTests
{
    [Fact]
    public void Parse_Full_Config()
    {
        var json = """{"fps":8,"scale":1.2,"actions":{"idle":{"fps":6},"walk":{"fps":12,"loops":1}}}""";
        var c = PetSkinConfig.Parse(json);
        Assert.Equal(8, c.Fps); Assert.Equal(1.2, c.Scale);
        Assert.Equal(6, c.Actions!["idle"]!.Fps);
        Assert.Equal(1, c.Actions!["walk"]!.Loops);
    }
    [Fact]
    public void Parse_Empty_Json_Returns_Defaults()
        => Assert.Null(PetSkinConfig.Parse("").Fps);
    [Fact]
    public void Parse_Invalid_Json_Does_Not_Throw()
        => Assert.NotNull(PetSkinConfig.Parse("{oops"));
}
