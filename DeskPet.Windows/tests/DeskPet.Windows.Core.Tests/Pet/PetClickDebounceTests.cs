using DeskPet.Core.Pet;
using Xunit;

namespace DeskPet.Windows.Core.Tests.Pet;

/// <summary>
/// 双击防抖判定（用户验收修复：宠物走动时窗口移动，WPF 原生 ClickCount 在"双击尺寸"检查
/// （窗口客户区坐标）下被重置为 1 → 双击失效）。防抖语义：
///   单击延迟一个双击时间窗；窗口内的第二次单击（无论 WPF 是否识别为双击）→ Double。
/// </summary>
public class PetClickDebounceTests
{
    [Fact]
    public void First_click_without_pending_is_single()
    {
        Assert.Equal(ClickKind.Single, PetClickDebounce.ClassifyUp(clickCount: 1, hasPendingSingle: false));
    }

    [Fact]
    public void Second_click_within_window_is_double_even_when_wpf_resets_click_count()
    {
        // 移动窗口场景：宠物走动导致两次点击的客户区坐标差超限，WPF 第二次 ClickCount=1，
        // 但待定单击存在 → 仍判定为双击（自检测）。
        Assert.Equal(ClickKind.Double, PetClickDebounce.ClassifyUp(clickCount: 1, hasPendingSingle: true));
    }

    [Fact]
    public void Native_double_click_is_double()
    {
        Assert.Equal(ClickKind.Double, PetClickDebounce.ClassifyUp(clickCount: 2, hasPendingSingle: false));
    }

    [Fact]
    public void Native_double_click_with_pending_is_still_double()
    {
        Assert.Equal(ClickKind.Double, PetClickDebounce.ClassifyUp(clickCount: 2, hasPendingSingle: true));
    }

    [Fact]
    public void Triple_click_is_double_again()
    {
        // WPF 快速三击 ClickCount 递增到 3；防抖下第三次点击同样视为双击（Swift 切换语义一致）
        Assert.Equal(ClickKind.Double, PetClickDebounce.ClassifyUp(clickCount: 3, hasPendingSingle: false));
    }

    [Fact]
    public void Single_after_pending_expired_restarts_window()
    {
        // 待定单击已被计时器消费（Poke 已执行）→ 下一次单击重新开启双击窗口
        Assert.Equal(ClickKind.Single, PetClickDebounce.ClassifyUp(clickCount: 1, hasPendingSingle: false));
    }
}
