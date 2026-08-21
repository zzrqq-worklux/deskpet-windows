using DeskPet.Core.Pet;
using Xunit;

namespace DeskPet.Windows.Core.Tests.Pet;

/// <summary>
/// T7 鼠标交互分类器测试：拖拽判定（欧氏距离阈值）、单击/双击分类、拖拽优先于点击。
/// Core 内部辅助，纯 BCL（元组坐标），不依赖 WPF。
/// 语义对照 Swift PetContentView.swift 77-115（DragGesture: 位移 &gt; 3 拖拽，否则点击）与
/// Windows 重设计（阈值 5px，双击用 WPF ClickCount）。
/// </summary>
public class PetInteractionClassifierTests
{
    // MARK: - IsDragGesture

    [Theory]
    [InlineData(0.0, 0.0, 5.0)]
    [InlineData(3.0, 0.0, 5.0)]
    [InlineData(0.0, 4.9, 5.0)]
    [InlineData(2.5, 2.0, 5.0)]
    public void IsDragGesture_below_threshold_is_false(double dx, double dy, double threshold)
    {
        var start = (X: 100.0, Y: 200.0);
        var current = (X: start.X + dx, Y: start.Y + dy);

        Assert.False(PetInteractionClassifier.IsDragGesture(start, current, threshold));
    }

    [Theory]
    [InlineData(5.0, 0.0)]
    [InlineData(0.0, 5.0)]
    [InlineData(3.0, 4.0)]
    [InlineData(-5.0, 0.0)]
    [InlineData(0.0, -5.0)]
    public void IsDragGesture_at_or_above_threshold_is_true(double dx, double dy)
    {
        var start = (X: 100.0, Y: 200.0);
        var current = (X: start.X + dx, Y: start.Y + dy);

        Assert.True(PetInteractionClassifier.IsDragGesture(start, current, PetInteractionClassifier.DefaultDragThresholdPx));
    }

    [Fact]
    public void IsDragGesture_uses_euclidean_distance_not_axis_aligned()
    {
        var start = (X: 0.0, Y: 0.0);
        var current = (X: 4.0, Y: 4.0);

        Assert.True(PetInteractionClassifier.IsDragGesture(start, current, 5.0));
        Assert.False(PetInteractionClassifier.IsDragGesture(start, current, 6.0));
    }

    [Fact]
    public void IsDragGesture_zero_displacement_is_false()
    {
        var p = (X: 50.0, Y: 60.0);

        Assert.False(PetInteractionClassifier.IsDragGesture(p, p, PetInteractionClassifier.DefaultDragThresholdPx));
    }

    // MARK: - Classify

    [Fact]
    public void Classify_single_click_returns_single()
    {
        Assert.Equal(ClickKind.Single, PetInteractionClassifier.Classify(1, isDragging: false));
    }

    [Fact]
    public void Classify_double_click_returns_double()
    {
        Assert.Equal(ClickKind.Double, PetInteractionClassifier.Classify(2, isDragging: false));
    }

    [Fact]
    public void Classify_unknown_click_count_returns_none()
    {
        Assert.Equal(ClickKind.None, PetInteractionClassifier.Classify(0, isDragging: false));
        Assert.Equal(ClickKind.None, PetInteractionClassifier.Classify(3, isDragging: false));
    }

    // MARK: - 拖拽优先于点击（drag-before-click ordering）

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Classify_ignores_click_when_dragging(int clickCount)
    {
        Assert.Equal(ClickKind.None, PetInteractionClassifier.Classify(clickCount, isDragging: true));
    }

    [Fact]
    public void Drag_suppresses_click_even_after_threshold_was_crossed()
    {
        var start = (X: 10.0, Y: 10.0);
        var current = (X: 10.0 + 6.0, Y: 10.0 + 6.0);
        var isDrag = PetInteractionClassifier.IsDragGesture(start, current, PetInteractionClassifier.DefaultDragThresholdPx);

        Assert.True(isDrag);
        Assert.Equal(ClickKind.None, PetInteractionClassifier.Classify(2, isDragging: isDrag));
        Assert.Equal(ClickKind.None, PetInteractionClassifier.Classify(1, isDragging: isDrag));
    }
}
