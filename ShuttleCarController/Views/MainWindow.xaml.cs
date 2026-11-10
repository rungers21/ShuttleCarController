using System;
using System.Windows;

namespace ShuttleCarController.Views
{
    /// <summary>
    /// 主窗口代码后置。
    /// 页面布局和绑定全部放在 XAML 中，这里只负责窗口生命周期清理，不包含车辆业务逻辑。
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>
        /// 初始化 XAML 生成的可视树，并注册窗口关闭事件。
        /// DataContext 由 Prism ViewModelLocator 自动创建和绑定。
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();
            Closed += OnWindowClosed;
        }

        /// <summary>
        /// 窗口关闭后解除 ViewModel 对日志、通信事件和 DispatcherTimer 的订阅。
        /// 不在这里直接关闭 Modbus 连接，通信资源由 IShuttleCarClient 实现负责管理。
        /// </summary>
        private void OnWindowClosed(object? sender, EventArgs e)
        {
            (DataContext as IDisposable)?.Dispose();
        }
    }
}
