using System;
using System.Windows;
using System.Windows.Threading;
using ShuttleCarSimulator.Services;
using ShuttleCarSimulator.ViewModels;

namespace ShuttleCarSimulator;

/// <summary>
/// 仿真器主窗口。UI 刷新使用单个 300 ms 的 DispatcherTimer，避免每个寄存器单独定时。
/// </summary>
public partial class MainWindow : Window
{
    private readonly ModbusSlaveSimulator _simulator = new();
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = System.TimeSpan.FromMilliseconds(300) };

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel(_simulator);
        DataContext = _viewModel;

        _refreshTimer.Tick += (sender, args) => _viewModel.Refresh();
        _refreshTimer.Start();

        Closed += async (sender, args) =>
        {
            _refreshTimer.Stop();
            await _simulator.DisposeAsync().ConfigureAwait(false);
        };
    }

    private async void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ToggleServiceAsync().ConfigureAwait(false);
    }

    private void SimulateTaskComplete_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SimulateTaskComplete();
    }

    private void ClearFaults_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ClearFaults();
    }

    private void SetBattery_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ApplyBattery();
    }

    private void SetAlarm_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ApplyAlarm();
    }
}
