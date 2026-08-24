namespace DeskPet.Core.Pet;

/// <summary>
/// 双击防抖判定（用户验收修复：宠物走动时窗口移动，WPF 原生双击检测要求两次点击在窗口
/// 客户区坐标内小于双击尺寸（~4px），窗口移动会把它重置为 ClickCount=1 → 双击失效）。
/// 防抖语义：单击延迟一个双击时间窗；窗口内的第二次单击（无论 WPF 是否识别为双击）→ Double。
/// 纯 BCL，可单测。与 Swift PetContentView.handleClick 的时间戳双击（&lt;0.4s）语义一致。
/// </summary>
public static class PetClickDebounce
{
    /// <summary>
    /// 每次非拖拽 Up 到达时调用：
    ///   ClickCount ≥ 2（WPF 原生双击，快速三击亦然）或已有待定单击（双击时间窗内的第二次单击）→ Double；
    ///   否则 → Single（调用方开启待定窗口并计时，超时后执行单击动作）。
    /// </summary>
    public static ClickKind ClassifyUp(int clickCount, bool hasPendingSingle)
        => clickCount >= 2 || hasPendingSingle ? ClickKind.Double : ClickKind.Single;
}
