namespace DeskPet.Core.Pet;
public enum PetAction { Idle, Walk, Sleep, Eat, Happy, Hurt, Drag, Music, Climb, Hang, Fall, Dizzy, Yawn, Home }
public enum PetLifeState { Roaming, AtHome }

public static class PetActionExtensions
{
    public static string FolderName(this PetAction a) => a.ToString().ToLowerInvariant();
    public static double DefaultFps(this PetAction a) => a switch
    {
        PetAction.Walk => 12, PetAction.Music or PetAction.Drag => 14,
        PetAction.Climb => 10, PetAction.Hang => 5, PetAction.Fall => 20,
        PetAction.Dizzy or PetAction.Yawn => 8, PetAction.Idle or PetAction.Sleep => 6,
        _ => 10 // Eat, Happy, Hurt, Home
    };
    public static bool LoopsByDefault(this PetAction a) => a switch
    {
        PetAction.Eat or PetAction.Fall or PetAction.Happy or PetAction.Hurt or PetAction.Yawn => false,
        _ => true
    };
    public static IReadOnlyList<PetAction> All() => Enum.GetValues<PetAction>();
}
