namespace DeskPet.Core.Services;

/// <summary>
/// 屏幕信息服务，对应 Swift 端 NSScreen 语义：主屏在前（下标 0 = 主屏），
/// 坐标与 NSScreen.frame 同向 —— X=左、Y=下，y 轴向上。Core 自定义类型，不依赖 WPF/Forms。
/// </summary>
public interface IScreenInfo
{
    /// <summary>所有屏幕的工作区，主屏在前，下标 0 = 主屏。</summary>
    IReadOnlyList<Rect> WorkAreas { get; }

    /// <summary>主屏工作区。</summary>
    Rect PrimaryWorkArea { get; }
}

/// <summary>
/// 屏幕矩形（Core 自定义类型，禁止使用 WPF/Forms 类型）。
/// X=左、Y=下（屏幕坐标，y 向上，对应 Swift NSScreen.frame 的坐标系）。
/// </summary>
public record Rect(double X, double Y, double Width, double Height);
