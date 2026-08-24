using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using DeskPet.Core.Services;

namespace DeskPet.Windows.Services;

/// <summary>
/// 屏幕信息服务（WinForms Screen）。坐标系换算：
/// Screen.Bounds 为"左上原点、y 向下"的物理像素（多屏时相对主屏左上角）；
/// Core Rect 为"左下原点、y 向上"（同 Swift NSScreen.frame）。
/// 换算：X 不变；y_up = 虚拟屏幕总高 - (bounds.Y + bounds.Height)；
/// 其中虚拟屏幕总高 = max(所有屏的 bounds.Y + bounds.Height)。
/// DPI：Bounds 为物理像素，DpiScale（物理/DIP）暴露给 Core 用于 Swift pt 对齐。
/// </summary>
public sealed class WpfScreenInfo : IScreenInfo
{
    public IReadOnlyList<Rect> WorkAreas { get; }

    public Rect PrimaryWorkArea { get; }

    /// <summary>主屏 物理像素/DIP：WinForms Bounds 为物理像素，SystemParameters.PrimaryScreenWidth 为 WPF DIP；
    /// 除零/非正（无主屏等）回落 1.0。</summary>
    public double DpiScale { get; }

    public WpfScreenInfo()
    {
        var all = Screen.AllScreens;
        var primary = Screen.PrimaryScreen;

        var ordered = new List<Screen>(all.Length);
        if (primary != null)
        {
            ordered.Add(primary);
            ordered.AddRange(all.Where(s => !ReferenceEquals(s, primary)));
        }
        else
        {
            ordered.AddRange(all);
        }

        var virtualHeight = all.Length == 0 ? 0 : all.Max(s => s.Bounds.Y + s.Bounds.Height);

        var rects = new List<Rect>(ordered.Count);
        foreach (var screen in ordered)
        {
            var b = screen.Bounds;
            rects.Add(new Rect(
                b.X,
                virtualHeight - (b.Y + b.Height),
                b.Width,
                b.Height));
        }

        WorkAreas = rects;
        PrimaryWorkArea = rects.Count > 0 ? rects[0] : new Rect(0, 0, 0, 0);

        var primaryBoundsWidth = primary?.Bounds.Width ?? 0;
        var dipWidth = System.Windows.SystemParameters.PrimaryScreenWidth;
        DpiScale = dipWidth > 0 ? primaryBoundsWidth / dipWidth : 0;
        if (DpiScale <= 0) DpiScale = 1.0;
    }
}
