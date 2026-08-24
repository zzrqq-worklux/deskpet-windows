using System;
using System.Collections.Generic;
using DeskPet.Core.Pet;

namespace DeskPet.Core.Chat;

/// <summary>
/// 气泡台词字典 + 随机抽取（Ruling 5b）：台词文本与 Swift <c>PetBubbleManager.swift</c>
/// 104-115 <c>phrases</c> 字典逐字一致；<c>Pick</c> 按动作随机抽一句（无台词的动作返回 null）。
/// Swift 侧 speakableActions（132）只含 walk/sleep/yawn/happy/hurt/fall/drag/music，
/// idle/home 的台词仅供定时自言自语使用——本类不区分用途，全部可抽取。
/// </summary>
public static class BubblePhrases
{
    /// <summary>动作 → 台词（Swift PetBubbleManager.swift 104-115 逐字移植）。</summary>
    public static IReadOnlyDictionary<PetAction, IReadOnlyList<string>> Phrases { get; } =
        new Dictionary<PetAction, IReadOnlyList<string>>
        {
            [PetAction.Idle] = new[]
            {
                "工作累了吗？", "你终于回来了！", "好无聊…陪我玩会儿嘛", "抱抱你～", "在想什么呢？",
            },
            [PetAction.Walk] = new[] { "出去溜达一下～", "活动活动筋骨～", "走走更健康！" },
            [PetAction.Happy] = new[] { "嘿嘿，今天心情超好！", "耶！开心！", "你最好啦～" },
            [PetAction.Hurt] = new[] { "呜…轻一点嘛", "你戳我干嘛呀…" },
            [PetAction.Yawn] = new[] { "哈啊…有点困了", "想睡觉了…" },
            [PetAction.Sleep] = new[] { "Zzz…", "呼噜…" },
            [PetAction.Home] = new[] { "Zzz… 睡大觉" },
            [PetAction.Music] = new[] { "♪(´▽｀) 跟着音乐摇摆～", "这歌真好听！" },
            [PetAction.Drag] = new[] { "哇——飞起来啦！" },
            [PetAction.Fall] = new[] { "呜哇——！" },
        };

    /// <summary>按当前动作随机抽一句台词（Swift <c>phrases[action]?.randomElement()</c> 语义）。</summary>
    public static string? Pick(PetAction action) => Pick(action, Random.Shared);

    /// <summary>
    /// 按当前动作随机抽一句台词；<paramref name="rng"/> 注入便于测试确定性。
    /// 动作无台词时返回 null（Swift <c>randomElement()</c> 的 nil 语义）。
    /// </summary>
    public static string? Pick(PetAction action, Random rng)
    {
        if (rng is null) throw new ArgumentNullException(nameof(rng));
        if (!Phrases.TryGetValue(action, out var list) || list.Count == 0) return null;
        return list[rng.Next(list.Count)];
    }
}
