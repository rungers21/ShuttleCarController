namespace ShuttleCarController.ViewModels;

/// <summary>
/// Modbus 功能码下拉框选项。
/// Code 保存实际发送给通信层的数值，Display 保存面向操作人员的界面说明。
/// </summary>
/// <param name="Code">Modbus 功能码，例如 03、04、06 或 16。</param>
/// <param name="Display">界面显示文本，包含功能码和寄存器类型说明。</param>
public sealed record FunctionCodeOption(byte Code, string Display)
{
    /// <summary>让未显式设置 ItemTemplate 的 ItemsControl 也能显示友好文本。</summary>
    public override string ToString()
    {
        return Display;
    }
}
