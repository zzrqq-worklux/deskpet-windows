namespace DeskPet.Core.Pet;

/// <summary>
/// 拖拽抓取点保持（Swift 语义：宠物停留在抓取点下方，而不是左下角吸附光标）。
/// 纯 BCL（元组坐标），无 WPF 依赖，可单测。
/// 不变式：target = cursor − Offset(cursor, anchor) == anchor → 拖拽启动瞬间窗口不跳、
/// 拖拽全程抓取点相对光标保持不动。
/// </summary>
public static class PetDragGrab
{
    /// <summary>抓取偏移：光标 Core 坐标 − 窗口底左（WindowPosition）Core 坐标。</summary>
    public static (double X, double Y) Offset((double X, double Y) cursor, (double X, double Y) windowAnchor)
        => (cursor.X - windowAnchor.X, cursor.Y - windowAnchor.Y);

    /// <summary>拖拽目标：光标 Core 坐标 − 抓取偏移（窗口底左应处位置，喂给 SyncWindowPosition）。</summary>
    public static (double X, double Y) Target((double X, double Y) cursor, (double X, double Y) offset)
        => (cursor.X - offset.X, cursor.Y - offset.Y);
}
