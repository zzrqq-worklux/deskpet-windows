namespace DeskPet.Core.Services;

/// <summary>
/// 音效播放服务，对应 Swift 端 <c>PetManager.playSound/stopSound</c> 语义：
/// 动作切换时停掉旧声再播新声；有路径则异步加载播放避免阻塞 UI 线程。
/// </summary>
public interface IAudioService
{
    /// <summary>
    /// 播放指定音效。path 非空时异步加载并播放；path 为空或 null 时仅停止当前播放；
    /// volume 取值 0.0-1.0。
    /// </summary>
    void Play(string? path, double volume);

    /// <summary>停止当前播放。</summary>
    void Stop();
}
