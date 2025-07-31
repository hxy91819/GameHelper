using System;
using System.Windows.Forms;

namespace HDRGameManager
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            
            // 检查是否已有实例运行
            bool createdNew;
            using (var mutex = new System.Threading.Mutex(true, "HDRGameManager", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("HDR游戏管理器已在运行中！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                
                Application.Run(new MainForm());
            }
        }
    }
}