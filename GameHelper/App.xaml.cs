using System.Windows;

namespace GameHelper
{
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            
            // 检查是否已有实例运行
            bool createdNew;
            using (var mutex = new System.Threading.Mutex(true, "GameHelper", out createdNew))
            {
                if (!createdNew)
                {
                    System.Windows.MessageBox.Show("GameHelper 已在运行中！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    Shutdown();
                    return;
                }
                
                // 创建主窗口但不显示（系统托盘模式）
                var mainWindow = new MainWindow();
                // 不调用 mainWindow.Show()，让程序在后台运行
            }
        }
    }
}