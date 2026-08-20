namespace DeskPet.Core.Services;

/// <summary>
/// 持久化设置读写服务，对应 Swift 端 <c>Defaults[.petScale]</c> 等 UserDefaults 语义。
/// 属性名称与含义逐一对应 Swift 的 Defaults key：PetScale/RoamInterval/StatDecaySpeed/
/// SoundVolume(0.0-1.0)/MusicDance/SkinDirectory("" 表示默认皮肤目录)。
/// </summary>
public interface ISettingsService
{
    /// <summary>宠物缩放比例（Swift <c>Defaults[.petScale]</c>，默认 1.0）。</summary>
    double PetScale { get; set; }

    /// <summary>自由漫游间隔秒数（Swift <c>Defaults[.petRoamInterval]</c>）。</summary>
    double RoamInterval { get; set; }

    /// <summary>属性值自然衰减速度（Swift <c>Defaults[.petStatDecaySpeed]</c>）。</summary>
    double StatDecaySpeed { get; set; }

    /// <summary>音效音量，取值 0.0-1.0（Swift <c>Defaults[.petSoundVolume]</c>）。</summary>
    double SoundVolume { get; set; }

    /// <summary>是否随音乐律动（Swift <c>Defaults[.petMusicDance]</c>）。</summary>
    bool MusicDance { get; set; }

    /// <summary>皮肤目录路径，空字符串表示使用默认目录（Swift <c>Defaults[.petSkinDirectory]</c>）。</summary>
    string SkinDirectory { get; set; }
}
