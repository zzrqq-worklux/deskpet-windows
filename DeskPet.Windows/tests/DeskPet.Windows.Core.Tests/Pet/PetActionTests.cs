using System.Linq;
using DeskPet.Core.Pet;
using Xunit;

namespace DeskPet.Windows.Core.Tests.Pet;

public class PetActionTests
{
    [Theory]
    [InlineData(PetAction.Idle, "idle", 6, true)]
    [InlineData(PetAction.Walk, "walk", 12, true)]
    [InlineData(PetAction.Music, "music", 14, true)]
    [InlineData(PetAction.Fall, "fall", 20, false)]
    [InlineData(PetAction.Yawn, "yawn", 8, false)]
    [InlineData(PetAction.Eat, "eat", 10, false)]
    public void Action_Metadata_Matches_Swift(PetAction a, string folder, double fps, bool loops)
    {
        Assert.Equal(folder, a.FolderName());
        Assert.Equal(fps, a.DefaultFps());
        Assert.Equal(loops, a.LoopsByDefault());
    }
    [Fact]
    public void All_Enum_Values_Have_Unique_FolderNames()
        => Assert.Equal(PetActionExtensions.All().Count, PetActionExtensions.All().Select(a => a.FolderName()).Distinct().Count());
}
