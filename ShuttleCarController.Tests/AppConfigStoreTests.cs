using System.IO;
using ShuttleCarController.Models;
using ShuttleCarController.Services;

namespace ShuttleCarController.Tests;

/// <summary>配置持久化测试：正常回环、缺失文件和损坏文件自动备份。</summary>
public sealed class AppConfigStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "shuttlecar-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaultsWithoutWarning()
    {
        var store = new AppConfigStore(_directory);

        var result = store.Load();

        Assert.Null(result.Warning);
        Assert.Equal("", result.Config.Connection.IpAddress);
        Assert.Equal(502, result.Config.Connection.Port);
        Assert.Equal(500, result.Config.Connection.PollIntervalMs);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsAllSettings()
    {
        var store = new AppConfigStore(_directory);
        var config = new AppConfig
        {
            Connection = new ConnectionSettings
            {
                IpAddress = "10.1.2.3",
                Port = 1502,
                UnitId = 7,
                RequestTimeoutMs = 1500,
                PollIntervalMs = 700,
                ReconnectIntervalMs = 5000,
                PollStartAddress = 100,
                PollQuantity = 38
            }
        };

        store.Save(config);
        var result = store.Load();

        Assert.Null(result.Warning);
        var loaded = result.Config.Connection;
        Assert.Equal("10.1.2.3", loaded.IpAddress);
        Assert.Equal(1502, loaded.Port);
        Assert.Equal((byte)7, loaded.UnitId);
        Assert.Equal(1500, loaded.RequestTimeoutMs);
        Assert.Equal(700, loaded.PollIntervalMs);
        Assert.Equal(5000, loaded.ReconnectIntervalMs);
        Assert.Equal(100, loaded.PollStartAddress);
        Assert.Equal(38, loaded.PollQuantity);
    }

    [Fact]
    public void Load_BrokenJson_BacksUpFileAndReturnsDefaults()
    {
        Directory.CreateDirectory(_directory);
        var store = new AppConfigStore(_directory);
        File.WriteAllText(store.ConfigPath, "{ this is not valid json");

        var result = store.Load();

        Assert.NotNull(result.Warning);
        Assert.Contains("已备份", result.Warning);
        Assert.False(File.Exists(store.ConfigPath));
        Assert.Single(Directory.GetFiles(_directory, "config.broken-*.json"));
    }

    [Fact]
    public void Load_EmptyObject_ReturnsDefaultsWithoutThrowing()
    {
        Directory.CreateDirectory(_directory);
        var store = new AppConfigStore(_directory);
        File.WriteAllText(store.ConfigPath, "{}");

        var result = store.Load();

        Assert.Null(result.Warning);
        Assert.NotNull(result.Config.Connection);
    }

    [Fact]
    public void Save_IsAtomic_LeavesNoTemporaryFile()
    {
        var store = new AppConfigStore(_directory);

        store.Save(new AppConfig());

        Assert.True(File.Exists(store.ConfigPath));
        Assert.False(File.Exists(store.ConfigPath + ".tmp"));
    }
}
