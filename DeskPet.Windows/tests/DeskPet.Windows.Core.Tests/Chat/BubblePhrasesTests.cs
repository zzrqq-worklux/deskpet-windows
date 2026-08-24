using System;
using System.Collections.Generic;
using System.Linq;
using DeskPet.Core.Chat;
using DeskPet.Core.Pet;
using Xunit;

namespace DeskPet.Windows.Core.Tests.Chat;

/// <summary>
/// Task 5 BubblePhrases 测试（Ruling 5b）：台词字典与 Swift PetBubbleManager.swift 104-115
/// 逐字一致（含多字节标点/emoji），Pick 随机抽取且可通过注入 Random 固定确定性。
/// </summary>
public class BubblePhrasesTests
{
    // MARK: - 字典内容（Swift 104-115 逐字对照）

    [Fact]
    public void Phrases_ContainVerbatimSwiftEntries()
    {
        Assert.Contains("工作累了吗？", BubblePhrases.Phrases[PetAction.Idle]);
        Assert.Contains("你终于回来了！", BubblePhrases.Phrases[PetAction.Idle]);
        Assert.Contains("好无聊…陪我玩会儿嘛", BubblePhrases.Phrases[PetAction.Idle]);
        Assert.Contains("抱抱你～", BubblePhrases.Phrases[PetAction.Idle]);
        Assert.Contains("在想什么呢？", BubblePhrases.Phrases[PetAction.Idle]);

        Assert.Contains("出去溜达一下～", BubblePhrases.Phrases[PetAction.Walk]);
        Assert.Contains("活动活动筋骨～", BubblePhrases.Phrases[PetAction.Walk]);
        Assert.Contains("走走更健康！", BubblePhrases.Phrases[PetAction.Walk]);

        Assert.Contains("嘿嘿，今天心情超好！", BubblePhrases.Phrases[PetAction.Happy]);
        Assert.Contains("耶！开心！", BubblePhrases.Phrases[PetAction.Happy]);
        Assert.Contains("你最好啦～", BubblePhrases.Phrases[PetAction.Happy]);

        Assert.Contains("呜…轻一点嘛", BubblePhrases.Phrases[PetAction.Hurt]);
        Assert.Contains("你戳我干嘛呀…", BubblePhrases.Phrases[PetAction.Hurt]);

        Assert.Contains("哈啊…有点困了", BubblePhrases.Phrases[PetAction.Yawn]);
        Assert.Contains("想睡觉了…", BubblePhrases.Phrases[PetAction.Yawn]);

        Assert.Contains("Zzz…", BubblePhrases.Phrases[PetAction.Sleep]);
        Assert.Contains("呼噜…", BubblePhrases.Phrases[PetAction.Sleep]);

        Assert.Equal("Zzz… 睡大觉", Assert.Single(BubblePhrases.Phrases[PetAction.Home]));

        Assert.Contains("♪(´▽｀) 跟着音乐摇摆～", BubblePhrases.Phrases[PetAction.Music]);
        Assert.Contains("这歌真好听！", BubblePhrases.Phrases[PetAction.Music]);

        Assert.Equal("哇——飞起来啦！", Assert.Single(BubblePhrases.Phrases[PetAction.Drag]));
        Assert.Equal("呜哇——！", Assert.Single(BubblePhrases.Phrases[PetAction.Fall]));
    }

    [Fact]
    public void Phrases_CountsMatchSwift()
    {
        Assert.Equal(5, BubblePhrases.Phrases[PetAction.Idle].Count);
        Assert.Equal(3, BubblePhrases.Phrases[PetAction.Walk].Count);
        Assert.Equal(3, BubblePhrases.Phrases[PetAction.Happy].Count);
        Assert.Equal(2, BubblePhrases.Phrases[PetAction.Hurt].Count);
        Assert.Equal(2, BubblePhrases.Phrases[PetAction.Yawn].Count);
        Assert.Equal(2, BubblePhrases.Phrases[PetAction.Sleep].Count);
        Assert.Single(BubblePhrases.Phrases[PetAction.Home]);
        Assert.Equal(2, BubblePhrases.Phrases[PetAction.Music].Count);
        Assert.Single(BubblePhrases.Phrases[PetAction.Drag]);
        Assert.Single(BubblePhrases.Phrases[PetAction.Fall]);
    }

    [Fact]
    public void Phrases_HaveNoEntriesForNonSpeakableActions()
    {
        // Swift speakableActions（132）只含 walk/sleep/yawn/happy/hurt/fall/drag/music；
        // idle/home 台词仅供定时自言自语使用。Eat/Climb/Hang/Dizzy 无台词。
        Assert.False(BubblePhrases.Phrases.ContainsKey(PetAction.Eat));
        Assert.False(BubblePhrases.Phrases.ContainsKey(PetAction.Climb));
        Assert.False(BubblePhrases.Phrases.ContainsKey(PetAction.Hang));
        Assert.False(BubblePhrases.Phrases.ContainsKey(PetAction.Dizzy));
    }

    // MARK: - Pick 随机抽取

    [Fact]
    public void Pick_WithInjectedRandom_IsDeterministicAndMemberOfSet()
    {
        var rng = new Random(42);
        var first = BubblePhrases.Pick(PetAction.Idle, rng);
        var second = BubblePhrases.Pick(PetAction.Idle, rng);

        var fresh = new Random(42);
        Assert.Equal(first, BubblePhrases.Pick(PetAction.Idle, fresh));
        Assert.Equal(second, BubblePhrases.Pick(PetAction.Idle, fresh));
        Assert.Contains(first, BubblePhrases.Phrases[PetAction.Idle]);
    }

    [Fact]
    public void Pick_DefaultRandom_ReturnsMemberOfSet()
    {
        foreach (var action in new[]
                 {
                     PetAction.Idle, PetAction.Walk, PetAction.Happy, PetAction.Hurt,
                     PetAction.Yawn, PetAction.Sleep, PetAction.Home, PetAction.Music,
                     PetAction.Drag, PetAction.Fall,
                 })
        {
            for (var i = 0; i < 10; i++)
            {
                var phrase = BubblePhrases.Pick(action);
                Assert.NotNull(phrase);
                Assert.Contains(phrase!, BubblePhrases.Phrases[action]);
            }
        }
    }

    [Fact]
    public void Pick_ActionWithoutPhrases_ReturnsNull()
    {
        Assert.Null(BubblePhrases.Pick(PetAction.Eat));
        Assert.Null(BubblePhrases.Pick(PetAction.Climb));
        Assert.Null(BubblePhrases.Pick(PetAction.Hang));
        Assert.Null(BubblePhrases.Pick(PetAction.Dizzy));
    }

    [Fact]
    public void Pick_WithNullRandom_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => BubblePhrases.Pick(PetAction.Idle, null!));
    }
}
