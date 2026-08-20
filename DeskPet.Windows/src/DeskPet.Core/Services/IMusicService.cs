namespace DeskPet.Core.Services;

/// <summary>
/// 音乐服务。Phase 1 仅占位：IsPlaying 恒为 false，音乐联动整体延后至
/// Phase 2（SMTC）实现；本周期只保证 PetManager 移植可编译，不做任何音乐功能。
/// </summary>
public interface IMusicService
{
    /// <summary>是否正在播放音乐（Phase 1 占位，恒 false）。</summary>
    bool IsPlaying { get; }
}
