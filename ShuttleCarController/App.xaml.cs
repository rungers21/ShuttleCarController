using Prism.DryIoc;
using Prism.Ioc;
using ShuttleCarController.Services;
using ShuttleCarController.Services.Devices;
using ShuttleCarController.ViewModels;
using ShuttleCarController.Views;
using System.Diagnostics;
using System.Windows;

namespace ShuttleCarController
{
    /// <summary>
    /// WPF 应用入口及 Prism 容器配置。
    /// 负责创建主窗口、注册应用级单例服务，并在退出时释放 ViewModel 与通信客户端。
    /// </summary>
    public partial class App
    {
        /// <summary>
        /// 创建应用主窗口。
        /// MainWindow 的 ViewModel 由 XAML 中的 ViewModelLocator 自动绑定。
        /// </summary>
        protected override Window CreateShell()
        {
            return Container.Resolve<MainWindow>();
        }

        /// <summary>
        /// 注册依赖注入服务。
        /// 配置、日志和主 ViewModel 均为单例，保证整个窗口共享同一份状态和事件订阅。
        /// </summary>
        protected override void RegisterTypes(IContainerRegistry containerRegistry)
        {
            containerRegistry.RegisterSingleton<AppConfigStore>();
            containerRegistry.RegisterSingleton<ILogService, LogService>();

            // 启动时按配置文件选择设备协议画像；未知标识回退默认画像并写调试跟踪。
            // 传输层与 ViewModel 不再直接绑定 ProtocolMap 的静态偏移。
            var profileId = new AppConfigStore().Load().Config.DeviceProfileId;
            var protocolProfile = DeviceProfileCatalog.Resolve(profileId);
            containerRegistry.RegisterInstance<IDeviceProtocolProfile>(protocolProfile);
            containerRegistry.RegisterSingleton<IDeviceClientFactory, DeviceClientFactory>();

            containerRegistry.RegisterSingleton<IShuttleCarClient, ModbusShuttleCarClient>();
            containerRegistry.RegisterSingleton<MainWindowViewModel>();
        }

        /// <summary>
        /// 应用退出时主动释放 ViewModel 和通信客户端。
        /// MainWindow.Closed 也会调用 ViewModel.Dispose；ViewModel 内部做了幂等处理，重复释放不会出错。
        /// 通信客户端必须在这里 DisposeAsync：它会停止后台轮询并关闭 TCP Socket，
        /// 避免进程退出后端口仍被占用或后台任务在回调已释放的 UI 对象。
        /// </summary>
        protected override void OnExit(ExitEventArgs e)
        {
            Container.Resolve<MainWindowViewModel>().Dispose();

            try
            {
                // OnExit 是同步入口；把异步释放放到线程池执行，避免在 UI 线程上等待后台续体而死锁。
                var client = Container.Resolve<IShuttleCarClient>();
                Task.Run(async () => await client.DisposeAsync()).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                // 退出阶段不允许异常阻止进程结束；写入诊断输出便于排查。
                Trace.WriteLine($"释放通信客户端失败：{exception}");
            }

            try
            {
                // 日志使用后台单写队列；这里等待队列清空，避免最后几条诊断日志丢失。
                (Container.Resolve<ILogService>() as IDisposable)?.Dispose();
            }
            catch (Exception exception)
            {
                Trace.WriteLine($"释放日志服务失败：{exception}");
            }

            base.OnExit(e);
        }
    }
}
