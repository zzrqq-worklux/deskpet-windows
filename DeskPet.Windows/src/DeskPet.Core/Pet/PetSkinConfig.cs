using System.Text.Json;

namespace DeskPet.Core.Pet;
public sealed record PetActionConfig { public double? Fps { get; init; } public int? Loops { get; init; } }
public sealed record PetSkinConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public double? Fps { get; init; }
    public double? Scale { get; init; }
    public Dictionary<string, PetActionConfig>? Actions { get; init; }
    public static PetSkinConfig Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new PetSkinConfig();
        try { return JsonSerializer.Deserialize<PetSkinConfig>(json, JsonOptions) ?? new PetSkinConfig(); }
        catch (JsonException) { return new PetSkinConfig(); }
    }
}
