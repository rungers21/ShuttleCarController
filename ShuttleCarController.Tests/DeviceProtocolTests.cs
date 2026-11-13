using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ShuttleCarController.Models;
using ShuttleCarController.Services;
using ShuttleCarController.Services.Devices;
using Xunit;

namespace ShuttleCarController.Tests;

/// <summary>
/// 设备协议抽象层测试：验证客户端与解码器只依赖 IDeviceProtocolProfile，
/// 换成另一套寄存器布局时无需修改通信层或 ViewModel。
/// </summary>
public sealed class DeviceProtocolTests : IDisposable
{
    private readonly string _logDirectory = Path.Combine(Path.GetTempPath(), "shuttle-device-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Decode_UsesProfileRegisterLayout()
    {
        var profile = new MockDeviceProtocolProfile();
        var block = new ushort[] { 0, 77, 1, 0, 0, 0, 0, 0 };

        var snapshot = profile.Decode(block, 100, DateTimeOffset.Now);

        Assert.Equal(77, snapshot.TaskNumber);
        Assert.True(snapshot.TaskCompletedFlag);
        Assert.Equal(DataQuality.Good, snapshot.Quality);
    }

    [Fact]
    public void EncodeCommand_UsesProfileAddressesAndValues()
    {
        var profile = new MockDeviceProtocolProfile();
        var command = new ShuttleCarCommand
        {
            Type = ShuttleCarCommandType.StartTask,
            TaskNumber = 0x0001_0002,
            TaskType = 2
        };

        var steps = profile.EncodeCommand(command, 100);

        var step = Assert.Single(steps);
        Assert.Equal(105, step.Address);
        Assert.Equal(new ushort[] { 2, 0 }, step.Values);
    }

    [Fact]
    public void EncodeCommand_ConfirmCompleteSetsProfileFlag()
    {
        var profile = new MockDeviceProtocolProfile();

        var steps = profile.EncodeCommand(
            new ShuttleCarCommand { Type = ShuttleCarCommandType.ConfirmComplete, TaskNumber = 5 },
            100);

        var step = Assert.Single(steps);
        Assert.Equal(106, step.Address);
        Assert.Equal(0, step.SetFlagBit);
        Assert.Empty(step.Values);
    }

    [Fact]
    public async Task Factory_ResolvesProfileAndCreatesClientWithoutChangingCode()
    {
        var profile = new MockDeviceProtocolProfile();
        var factory = new DeviceClientFactory(new LogService(_logDirectory));

        var resolved = factory.ResolveProfile(MockDeviceProtocolProfile.MockId);
        // 目录中尚未登记 Mock 画像，因此回退默认；显式注入时客户端必须使用传入画像。
        Assert.Equal(TwoWayShuttleProtocolProfile.ProfileId, resolved.Id);

        var client = factory.CreateClient(profile);
        var modbus = Assert.IsType<ModbusShuttleCarClient>(client);
        Assert.Same(profile, modbus.Profile);
        await client.DisposeAsync();
    }

    [Fact]
    public void Factory_UnknownIdFallsBackToDefaultProfile()
    {
        var factory = new DeviceClientFactory(new LogService(_logDirectory));

        var resolved = factory.ResolveProfile("not-a-real-profile");

        Assert.Same(TwoWayShuttleProtocolProfile.Instance, resolved);
    }

    [Fact]
    public void StatusSnapshotCache_UsesInjectedProfile()
    {
        var profile = new MockDeviceProtocolProfile();
        var cache = new StatusSnapshotCache(profile);
        var block = new ushort[] { 0, 42, 0, 0, 0, 0, 0, 0 };

        var snapshot = cache.Decode(block, 0, DateTimeOffset.Now);

        Assert.Equal(42, snapshot.TaskNumber);
        Assert.Equal(2, snapshot.Fields.Count);
        Assert.Equal("任务号", snapshot.Fields[0].Name);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_logDirectory))
            {
                Directory.Delete(_logDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 日志后台写入未及时释放时忽略清理失败，不影响测试结论。
        }
    }

    /// <summary>
    /// 与二向穿梭车完全不同的寄存器布局：任务号在寄存器 1（16 位），完成位在 2.0，
    /// 任务块起始于寄存器 5，确认完成标志位于 6.0。
    /// </summary>
    private sealed class MockDeviceProtocolProfile : IDeviceProtocolProfile
    {
        public const string MockId = "mock-layout";

        private static readonly ProtocolField[] MockFields =
        [
            new("车辆→上位机", "任务号", "0.0", 1, "Int"),
            new("车辆→上位机", "任务完成", "2.0", 2, "Bool", 0)
        ];

        public string Id => MockId;

        public string DisplayName => "Mock 设备";

        public int TotalLength => 8;

        public IReadOnlyList<ProtocolField> Fields => MockFields;

        public IReadOnlyList<ProtocolField> FaultFields => [];

        public ShuttleCarSnapshot Decode(IReadOnlyList<ushort> block, int baseAddress, DateTimeOffset updatedAt)
        {
            if (block.Count < 3)
            {
                return new ShuttleCarSnapshot
                {
                    UpdatedAt = updatedAt,
                    Quality = DataQuality.Error,
                    QualityMessage = "Mock 帧长度不足。"
                };
            }

            var taskNumber = (int)block[1];
            var completed = ProtocolCodec.GetBit(block[2], 0);
            return new ShuttleCarSnapshot
            {
                UpdatedAt = updatedAt,
                Quality = DataQuality.Good,
                TaskNumber = taskNumber,
                TaskCompletedFlag = completed,
                Fields = BuildFieldValues(taskNumber, completed, updatedAt)
            };
        }

        private static IReadOnlyList<ShuttleCarFieldValue> BuildFieldValues(
            int taskNumber,
            bool completed,
            DateTimeOffset updatedAt)
        {
            return
            [
                new ShuttleCarFieldValue(
                    "车辆→上位机", "任务号", "0.0", "1", "Int",
                    taskNumber.ToString(), taskNumber.ToString(), DataQuality.Good, updatedAt),
                new ShuttleCarFieldValue(
                    "车辆→上位机", "任务完成", "2.0", "2", "Bool",
                    completed ? "1" : "0", completed ? "完成" : "未完成", DataQuality.Good, updatedAt)
            ];
        }

        public IReadOnlyList<DeviceWriteStep> EncodeCommand(ShuttleCarCommand command, int baseAddress)
        {
            return command.Type switch
            {
                ShuttleCarCommandType.StartTask =>
                [
                    new DeviceWriteStep(
                        (ushort)(baseAddress + 5),
                        [unchecked((ushort)(command.TaskType ?? 0)), 0])
                ],
                ShuttleCarCommandType.ConfirmComplete =>
                [
                    DeviceWriteStep.SetFlag((ushort)(baseAddress + 6), 0)
                ],
                _ => []
            };
        }

        public bool IsCommandSatisfied(ShuttleCarCommand command, ShuttleCarSnapshot snapshot)
        {
            return command.Type switch
            {
                ShuttleCarCommandType.StartTask => command.TaskNumber is { } number && snapshot.TaskNumber == number,
                ShuttleCarCommandType.ConfirmComplete => snapshot.TaskNumber is null or 0 && !snapshot.TaskCompletedFlag,
                _ => true
            };
        }

        public bool ShouldWaitForSatisfaction(ShuttleCarCommandType type)
            => type is ShuttleCarCommandType.StartTask or ShuttleCarCommandType.ConfirmComplete;
    }
}
