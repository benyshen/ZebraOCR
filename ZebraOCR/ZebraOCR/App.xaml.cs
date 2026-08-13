using System.Windows;
using System.Windows.Threading;

namespace ZebraOCR
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += (s, args) =>
            {
                System.Windows.MessageBox.Show(
                    "主线程异常：" + args.Exception.Message,
                    "错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                args.Handled = true;
            };
        }
    }
}
