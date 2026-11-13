using System;
using System.IO;
using System.Text.Json;
using ShuttleCarController.Models;

namespace ShuttleCarController.Services;

/// <summary>
/// 使用 System.Text.Json 读写本地应用配置。
/// 配置保存在当前用户的 LocalApplicationData 下，不需要管理员权限，也不会进入项目源码目录。
/// </summary>
public sealed class AppConfigStore
{
    /// <summary>JSON 采用缩进格式，便于现场人员备份和人工检查。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>配置文件所在目录，不含文件名。</summary>
    private readonly string _configDirectory;

    /// <summary>初始化当前用户专属的配置目录。</summary>
    public AppConfigStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShuttleCarController"))
    {
    }

    /// <summary>
    /// 使用指定目录初始化配置存储。
    /// 供单元测试注入临时目录，避免读写当前用户的真实配置。
    /// </summary>
    /// <param name="configDirectory">存放 config.json 的目录。</param>
    public AppConfigStore(string configDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        _configDirectory = configDirectory;
    }

    /// <summary>主配置文件 config.json 的完整路径。</summary>
    public string ConfigPath => Path.Combine(_configDirectory, "config.json");

    /// <summary>
    /// 加载配置。
    /// 文件不存在时返回默认值；文件损坏或结构不兼容时先尝试备份，再返回默认配置，
    /// 从而避免配置问题阻止程序启动。
    /// </summary>
    public ConfigLoadResult Load()
    {
        if (!File.Exists(ConfigPath))
        {
            return new ConfigLoadResult(new AppConfig(), null);
        }

        try
        {
            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (config is null || config.Connection is null)
            {
                throw new JsonException("配置文件内容为空。");
            }

            return new ConfigLoadResult(config, null);
        }
        catch (Exception exception)
        {
            // 文件占用、权限、JSON 语法和版本不兼容都走同一降级路径。
            var backupPath = BackupBrokenConfig();
            var warning = backupPath is null
                ? $"配置文件读取失败，将使用默认配置：{exception.Message}"
                : $"配置文件读取失败，已备份为 {backupPath}，将使用默认配置：{exception.Message}";
            return new ConfigLoadResult(new AppConfig(), warning);
        }
    }

    /// <summary>
    /// 保存配置。
    /// 先写临时文件，再替换正式文件，减少程序在写入中途退出导致配置只剩半段 JSON 的风险。
    /// </summary>
    /// <param name="config">需要持久化的完整配置。</param>
    public void Save(AppConfig config)
    {
        Directory.CreateDirectory(_configDirectory);
        var temporaryPath = ConfigPath + ".tmp";
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, ConfigPath, true);
    }

    /// <summary>
    /// 将损坏配置重命名为带时间戳的备份文件。
    /// 备份失败时返回 null，由 Load 将失败信息转换为界面警告。
    /// </summary>
    private string? BackupBrokenConfig()
    {
        try
        {
            Directory.CreateDirectory(_configDirectory);
            var backupPath = Path.Combine(
                _configDirectory,
                $"config.broken-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Move(ConfigPath, backupPath, true);
            return backupPath;
        }
        catch
        {
            // 备份失败不能再次阻止应用使用默认配置启动。
            return null;
        }
    }
}

/// <summary>配置加载结果，同时携带配置对象和需要提示用户的非致命警告。</summary>
/// <param name="Config">成功读取的配置，或发生异常时创建的默认配置。</param>
/// <param name="Warning">配置损坏、备份结果等警告；正常加载时为 null。</param>
public sealed record ConfigLoadResult(AppConfig Config, string? Warning);
