using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskPet.Core.Chat;

/// <summary>
/// 对话历史持久化（Ruling 5）：Load/Save/Trim，文件路径构造注入（Core 不写死 %APPDATA%），
/// cap = ChatHistoryCount（裁剪到最近 N 条）。对应 Swift <c>PetChatManager.persist</c>
/// （493-495，<c>Array(messages.suffix(max(...)))</c> 的 suffix 裁剪语义），
/// 消息经 System.Text.Json 序列化（角色枚举按名称写，便于阅读，同 Swift Codable）。
/// 损坏/缺失文件回退空历史、写失败吞异常——与 <c>JsonSettingsService</c> 同一容错策略。
/// </summary>
public sealed class ChatHistoryStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public ChatHistoryStore(string filePath)
    {
        _path = filePath ?? throw new ArgumentNullException(nameof(filePath));
    }

    /// <summary>读取历史（文件缺失/损坏 → 空列表，不抛异常；Swift <c>Defaults[.petChatHistory]</c> 缺失语义）。</summary>
    public IReadOnlyList<ChatMessage> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<List<ChatMessage>>(File.ReadAllText(_path), Options);
                if (loaded != null) return loaded;
            }
        }
        catch (Exception)
        {
            // 损坏文件/无权限：回退空历史，不崩溃。
        }
        return Array.Empty<ChatMessage>();
    }

    /// <summary>写入历史（目录自动创建；写失败吞异常，保留内存态——磁盘满/权限等不崩溃）。</summary>
    public void Save(IReadOnlyList<ChatMessage> messages)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(messages, Options));
        }
        catch (Exception)
        {
            // 写失败不崩溃。
        }
    }

    /// <summary>
    /// 裁剪到最近 <paramref name="cap"/> 条（cap ≤ 0 → 空；Swift persist 的 suffix 语义，
    /// 上限取设置 <c>ChatHistoryCount</c>）。返回新列表，不改动入参。
    /// </summary>
    public static IReadOnlyList<ChatMessage> Trim(IReadOnlyList<ChatMessage> messages, int cap)
    {
        if (messages is null || cap <= 0) return Array.Empty<ChatMessage>();
        if (messages.Count <= cap) return messages.ToList();
        return messages.Skip(messages.Count - cap).ToList();
    }
}
