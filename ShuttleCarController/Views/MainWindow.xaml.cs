using System;
using System.Collections.Specialized;
using System.Windows;
using ShuttleCarController.ViewModels;

namespace ShuttleCarController.Views
{
    /// <summary>
    /// 主窗口代码后置。
    /// 页面布局和绑定全部放在 XAML 中，这里只负责窗口生命周期清理和日志自动滚动。
    /// </summary>
    public partial class MainWindow : Window
    {
        private MainWindowViewModel? _viewModel;

        /// <summary>
        /// 初始化 XAML 生成的可视树，并注册窗口关闭和 DataContext 变化事件。
        /// DataContext 由 Prism ViewModelLocator 自动创建和绑定。
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();
            Closed += OnWindowClosed;
            DataContextChanged += OnDataContextChanged;
        }

        /// <summary>
        /// ViewModel 替换时迁移日志集合订阅，避免旧集合继续触发滚动或造成事件泄漏。
        /// </summary>
        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_viewModel is not null)
            {
                _viewModel.FilteredLogs.CollectionChanged -= OnFilteredLogsChanged;
            }

            _viewModel = e.NewValue as MainWindowViewModel;
            if (_viewModel is not null)
            {
                _viewModel.FilteredLogs.CollectionChanged += OnFilteredLogsChanged;
            }
        }

        /// <summary>
        /// 新日志追加后滚动到最后一行；操作员勾选“暂停滚动”时保持当前视图位置。
        /// </summary>
        private void OnFilteredLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_viewModel is null
                || _viewModel.IsLogAutoScrollPaused
                || !IsLoaded
                || e.Action != NotifyCollectionChangedAction.Add
                || _viewModel.FilteredLogs.Count == 0)
            {
                return;
            }

            LogDataGrid.ScrollIntoView(_viewModel.FilteredLogs[^1]);
        }

        /// <summary>
        /// 窗口关闭后解除 ViewModel 对日志、通信事件和 DispatcherTimer 的订阅。
        /// 不在这里直接关闭 Modbus 连接，通信资源由 IShuttleCarClient 实现负责管理。
        /// </summary>
        private void OnWindowClosed(object? sender, EventArgs e)
        {
            if (_viewModel is not null)
            {
                _viewModel.FilteredLogs.CollectionChanged -= OnFilteredLogsChanged;
            }

            (DataContext as IDisposable)?.Dispose();
        }
    }
}
