using Prism.DryIoc;
using Prism.Ioc;
using ShuttleCarController.Services;
using ShuttleCarController.ViewModels;
using ShuttleCarController.Views;
using System.Windows;

namespace ShuttleCarController
{
    /// <summary>
    /// WPF 应用入口及 Prism 容器配置。
    /// 负责创建主窗口、注册应用级单例服务，并在退出时解除 ViewModel 的事件和定时器订阅。
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
            containerRegistry.RegisterSingleton<IShuttleCarClient, ModbusShuttleCarClient>();
            containerRegistry.RegisterSingleton<MainWindowViewModel>();
        }

        /// <summary>
        /// 应用退出时主动释放 ViewModel。
        /// MainWindow.Closed 也会调用 Dispose；ViewModel 内部做了幂等处理，重复释放不会出错。
        /// </summary>
        protected override void OnExit(ExitEventArgs e)
        {
            Container.Resolve<MainWindowViewModel>().Dispose();
            base.OnExit(e);
        }
    }
}
