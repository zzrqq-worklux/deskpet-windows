using System;
using System.IO;
using System.Text.Json;
using DeskPet.Core.Services;

namespace DeskPet.Windows.Services;

/// <summary>
/// JSON 文件实现的设置服务，对应 Swift 端 UserDefaults 语义。
/// 默认路径：%APPDATA%/DeskPet/settings.json。
/// </summary>
public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly string _path;
    private SettingsData _data;

    public JsonSettingsService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DeskPet", "settings.json"))
    {
    }

    public JsonSettingsService(string settingsFilePath)
    {
        _path = settingsFilePath;
        _data = Load(_path);
    }

    public double PetScale
    {
        get { lock (_lock) return _data.PetScale; }
        set { Set(d => d.PetScale = value); }
    }

    public double RoamInterval
    {
        get { lock (_lock) return _data.RoamInterval; }
        set { Set(d => d.RoamInterval = value); }
    }

    public double StatDecaySpeed
    {
        get { lock (_lock) return _data.StatDecaySpeed; }
        set { Set(d => d.StatDecaySpeed = value); }
    }

    public double SoundVolume
    {
        get { lock (_lock) return _data.SoundVolume; }
        set { Set(d => d.SoundVolume = value); }
    }

    public bool MusicDance
    {
        get { lock (_lock) return _data.MusicDance; }
        set { Set(d => d.MusicDance = value); }
    }

    public string SkinDirectory
    {
        get { lock (_lock) return _data.SkinDirectory; }
        set { Set(d => d.SkinDirectory = value ?? ""); }
    }

    private void Set(Action<SettingsData> mutate)
    {
        lock (_lock)
        {
            mutate(_data);
            Save();
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(_data, Options));
        }
        catch (Exception)
        {
            // 写失败不崩溃（磁盘满/权限等），保留内存中的值。
        }
    }

    private static SettingsData Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var data = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(path));
                if (data != null) return data;
            }
        }
        catch (Exception)
        {
            // 损坏文件/无权限：回退到默认值，不崩溃。
        }
        return new SettingsData();
    }

    private sealed class SettingsData
    {
        public double PetScale { get; set; } = 1.0;
        public double RoamInterval { get; set; } = 4.0;
        public double StatDecaySpeed { get; set; } = 1.0;
        public double SoundVolume { get; set; } = 1.0;
        public bool MusicDance { get; set; }
        public string SkinDirectory { get; set; } = "";
    }
}
