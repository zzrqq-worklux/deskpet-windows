using System;

namespace DeskPet.Core.Pet;

/// <summary>单击/双击判定结果（拖拽中恒为 None，拖拽优先于点击）。</summary>
public enum ClickKind
{
    None,
    Single,
    Double
}

/// <summary>
/// T7 鼠标交互分类器：拖拽判定 + 单击/双击分类。
/// 纯 BCL（元组坐标），无 WPF 依赖，可单测。
/// 语义对照 Swift PetContentView.swift 77-115：位移超阈值 → 拖拽（不再触发点击）；
/// 否则按点击次数分类（Windows 重设计：阈值 5px，双击用 WPF ClickCount）。
/// </summary>
public static class PetInteractionClassifier
{
    /// <summary>拖拽判定阈值（物理像素；Swift 3pt 的 Windows 重设计，brief 约定 ≥5px）。</summary>
    public const double DefaultDragThresholdPx = 5.0;

    /// <summary>位移（欧氏距离）≥ 阈值视为拖拽手势。</summary>
    public static bool IsDragGesture((double X, double Y) start, (double X, double Y) current, double thresholdPx)
    {
        var dx = current.X - start.X;
        var dy = current.Y - start.Y;
        return Math.Sqrt(dx * dx + dy * dy) >= thresholdPx;
    }

    /// <summary>
    /// 点击分类：拖拽中（isDragging）一律 None（拖拽判定先于单击判定，Swift 语义）；
    /// 否则 ClickCount 1→Single、2→Double，其余 None。
    /// </summary>
    public static ClickKind Classify(int clickCount, bool isDragging)
    {
        if (isDragging) return ClickKind.None;
        return clickCount switch
        {
            1 => ClickKind.Single,
            2 => ClickKind.Double,
            _ => ClickKind.None,
        };
    }
}
