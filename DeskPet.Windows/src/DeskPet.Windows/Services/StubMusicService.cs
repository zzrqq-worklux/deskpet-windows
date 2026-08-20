using DeskPet.Core.Services;

namespace DeskPet.Windows.Services;

/// <summary>
/// 音乐服务占位实现：Phase 1 不做音乐功能，IsPlaying 恒为 false。
/// </summary>
public sealed class StubMusicService : IMusicService
{
    public bool IsPlaying => false;
}
