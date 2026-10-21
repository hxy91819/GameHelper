using System.IO;
using System.Windows;

namespace GameHelper.WpfPrototype;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var window = new MainWindow(e.Args);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            // A failed config read must not silently replace the user's catalog with demo data.
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "GameHelper.WpfPrototype-startup-error.txt"), ex.ToString());
            if (!e.Args.Contains("--snapshot", StringComparer.Ordinal))
                MessageBox.Show(ex.Message, "GameHelper · 原型无法打开", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
