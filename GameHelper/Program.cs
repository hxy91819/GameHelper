using System;
using System.Windows.Forms;

namespace GameHelper
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
            using (var mutex = new System.Threading.Mutex(true, "GameHelper", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("GameHelper 已在运行中！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                
                Application.Run(new MainForm());
            }
        }
    }
}