using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace GameHelper
{
    public partial class MainForm : Form
    {
        // Windows API 声明 - 用于模拟按键
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        // 按键常量
        private const int VK_LWIN = 0x5B;      // 左Windows键
        private const int VK_MENU = 0x12;      // Alt键
        private const int VK_B = 0x42;         // B键
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private NotifyIcon notifyIcon;
        private ManagementEventWatcher processStartWatcher;
        private ManagementEventWatcher processStopWatcher;
        private HashSet<string> gameProcesses = new HashSet<string>();
        private HashSet<string> monitoredGames = new HashSet<string>();
        private bool isHDREnabled = false;
        private string configPath;

        public MainForm()
        {
            InitializeComponent();
            InitializeSystemTray();
            LoadConfiguration();
            StartProcessMonitoring();
            CheckInitialHDRState();
        }

        private void InitializeComponent()
        {
            this.WindowState = FormWindowState.Minimized;
            this.ShowInTaskbar = false;
            this.Visible = false;
        }

        private void InitializeSystemTray()
        {
            notifyIcon = new NotifyIcon()
            {
                Icon = SystemIcons.Application,
                Visible = true,
                Text = "GameHelper - 游戏助手"
            };

            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("配置游戏", null, ShowConfigDialog);
            contextMenu.Items.Add("当前状态", null, ShowStatus);
            contextMenu.Items.Add("-");
            contextMenu.Items.Add("退出", null, ExitApplication);
            
            notifyIcon.ContextMenuStrip = contextMenu;
            notifyIcon.DoubleClick += ShowConfigDialog;
        }

        private void LoadConfiguration()
        {
            configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
                                    "GameHelper", "config.json");
            
            Directory.CreateDirectory(Path.GetDirectoryName(configPath));
            
            if (File.Exists(configPath))
            {
                try
                {
                    var json = File.ReadAllText(configPath);
                    var config = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                    if (config.ContainsKey("games"))
                    {
                        var games = JsonSerializer.Deserialize<string[]>(config["games"].ToString());
                        monitoredGames = new HashSet<string>(games, StringComparer.OrdinalIgnoreCase);
                    }
                }
                catch (Exception ex)
                {
                    ShowNotification($"配置加载失败: {ex.Message}", ToolTipIcon.Warning);
                }
            }
            else
            {
                // 默认游戏列表
                monitoredGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "cyberpunk2077.exe",
                    "witcher3.exe",
                    "rdr2.exe",
                    "msfs.exe",
                    "forza_horizon_5.exe"
                };
                SaveConfiguration();
            }
        }

        private void SaveConfiguration()
        {
            try
            {
                var config = new Dictionary<string, object>
                {
                    ["games"] = monitoredGames.ToArray()
                };
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(configPath, json);
            }
            catch (Exception ex)
            {
                ShowNotification($"配置保存失败: {ex.Message}", ToolTipIcon.Error);
            }
        }     
   private void StartProcessMonitoring()
        {
            try
            {
                // 监控进程启动
                processStartWatcher = new ManagementEventWatcher(
                    new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
                processStartWatcher.EventArrived += OnProcessStarted;
                processStartWatcher.Start();

                // 监控进程结束
                processStopWatcher = new ManagementEventWatcher(
                    new WqlEventQuery("SELECT * FROM Win32_ProcessStopTrace"));
                processStopWatcher.EventArrived += OnProcessStopped;
                processStopWatcher.Start();

                ShowNotification("GameHelper 游戏助手已启动", ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"启动进程监控失败: {ex.Message}\n请以管理员权限运行程序。", 
                              "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.Exit();
            }
        }

        private void OnProcessStarted(object sender, EventArrivedEventArgs e)
        {
            try
            {
                string processName = e.NewEvent["ProcessName"]?.ToString();
                if (string.IsNullOrEmpty(processName)) return;

                // 调试信息：显示事件触发时间
                var eventTime = DateTime.Now.ToString("HH:mm:ss.fff");
                System.Diagnostics.Debug.WriteLine($"[{eventTime}] 进程启动事件: {processName}");

                if (monitoredGames.Contains(processName))
                {
                    gameProcesses.Add(processName);
                    if (!isHDREnabled)
                    {
                        EnableHDR();
                        ShowNotification($"[{eventTime}] 检测到游戏 {processName}，已开启HDR", ToolTipIcon.Info);
                    }
                }
            }
            catch (Exception ex)
            {
                ShowNotification($"处理进程启动事件失败: {ex.Message}", ToolTipIcon.Warning);
            }
        }

        private void OnProcessStopped(object sender, EventArrivedEventArgs e)
        {
            try
            {
                string processName = e.NewEvent["ProcessName"]?.ToString();
                if (string.IsNullOrEmpty(processName)) return;

                // 调试信息：显示事件触发时间
                var eventTime = DateTime.Now.ToString("HH:mm:ss.fff");
                System.Diagnostics.Debug.WriteLine($"[{eventTime}] 进程结束事件: {processName}");

                if (gameProcesses.Contains(processName))
                {
                    gameProcesses.Remove(processName);
                    if (gameProcesses.Count == 0 && isHDREnabled)
                    {
                        DisableHDR();
                        ShowNotification($"[{eventTime}] 游戏 {processName} 已关闭，已关闭HDR", ToolTipIcon.Info);
                    }
                }
            }
            catch (Exception ex)
            {
                ShowNotification($"处理进程结束事件失败: {ex.Message}", ToolTipIcon.Warning);
            }
        }

        private void CheckInitialHDRState()
        {
            // 检查当前是否有监控的游戏在运行
            var runningProcesses = Process.GetProcesses();
            foreach (var process in runningProcesses)
            {
                try
                {
                    string processName = process.ProcessName + ".exe";
                    if (monitoredGames.Contains(processName))
                    {
                        gameProcesses.Add(processName);
                    }
                }
                catch { } // 忽略访问被拒绝的进程
            }

            if (gameProcesses.Count > 0)
            {
                EnableHDR();
                ShowNotification($"检测到 {gameProcesses.Count} 个游戏正在运行，已开启HDR", ToolTipIcon.Info);
            }
        }     
        private void EnableHDR()
        {
            try
            {
                // 检查当前HDR状态，避免重复切换
                if (!isHDREnabled)
                {
                    // 使用Windows 11快捷键 WIN + ALT + B 切换HDR
                    SendHDRToggleKey();
                    isHDREnabled = true;
                    ShowNotification("已自动开启HDR (WIN+ALT+B)", ToolTipIcon.Info);
                }
            }
            catch (Exception ex)
            {
                ShowNotification($"启用HDR失败: {ex.Message}", ToolTipIcon.Error);
            }
        }

        private void DisableHDR()
        {
            try
            {
                // 检查当前HDR状态，避免重复切换
                if (isHDREnabled)
                {
                    // 使用Windows 11快捷键 WIN + ALT + B 切换HDR
                    SendHDRToggleKey();
                    isHDREnabled = false;
                    ShowNotification("已自动关闭HDR (WIN+ALT+B)", ToolTipIcon.Info);
                }
            }
            catch (Exception ex)
            {
                ShowNotification($"关闭HDR失败: {ex.Message}", ToolTipIcon.Error);
            }
        }

        private void SendHDRToggleKey()
        {
            try
            {
                // 模拟按下 WIN + ALT + B 快捷键来切换HDR
                // 按下Windows键
                keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
                
                // 按下Alt键
                keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                
                // 按下B键
                keybd_event(VK_B, 0, 0, UIntPtr.Zero);
                
                // 等待一小段时间
                System.Threading.Thread.Sleep(50);
                
                // 释放B键
                keybd_event(VK_B, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                
                // 释放Alt键
                keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                
                // 释放Windows键
                keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch (Exception ex)
            {
                ShowNotification($"发送HDR快捷键失败: {ex.Message}", ToolTipIcon.Error);
            }
        }

        private void ShowConfigDialog(object sender, EventArgs e)
        {
            var configForm = new ConfigForm(monitoredGames);
            if (configForm.ShowDialog() == DialogResult.OK)
            {
                monitoredGames = configForm.GetSelectedGames();
                SaveConfiguration();
                ShowNotification("配置已更新", ToolTipIcon.Info);
            }
        }

        private void ShowStatus(object sender, EventArgs e)
        {
            string status = $"HDR状态: {(isHDREnabled ? "开启" : "关闭")}\n" +
                           $"运行中的游戏: {gameProcesses.Count}\n" +
                           $"监控的游戏数量: {monitoredGames.Count}";
            
            if (gameProcesses.Count > 0)
            {
                status += "\n\n当前运行的游戏:\n" + string.Join("\n", gameProcesses);
            }

            MessageBox.Show(status, "GameHelper 状态", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ShowNotification(string message, ToolTipIcon icon)
        {
            notifyIcon.ShowBalloonTip(3000, "GameHelper", message, icon);
        }

        private void ExitApplication(object sender, EventArgs e)
        {
            if (isHDREnabled)
            {
                DisableHDR();
            }
            
            processStartWatcher?.Stop();
            processStopWatcher?.Stop();
            notifyIcon.Visible = false;
            Application.Exit();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                processStartWatcher?.Dispose();
                processStopWatcher?.Dispose();
                notifyIcon?.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(false);
        }
    }

    // 配置窗口类
    public class ConfigForm : Form
    {
        private CheckedListBox gameListBox;
        private TextBox newGameTextBox;
        private Button addButton;
        private Button removeButton;
        private Button okButton;
        private Button cancelButton;
        private Button hdrOnButton;
        private Button hdrOffButton;
        private Label hdrStatusLabel;

        public ConfigForm(HashSet<string> currentGames)
        {
            InitializeComponents();
            LoadGames(currentGames);
        }

        private void InitializeComponents()
        {
            this.Text = "游戏配置 & HDR测试";
            this.Size = new Size(400, 580);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;

            var label = new Label
            {
                Text = "选择要监控HDR的游戏进程:",
                Location = new Point(10, 10),
                Size = new Size(300, 20)
            };

            gameListBox = new CheckedListBox
            {
                Location = new Point(10, 35),
                Size = new Size(360, 250),
                CheckOnClick = true
            };

            var addLabel = new Label
            {
                Text = "添加新游戏 (例: game.exe):",
                Location = new Point(10, 300),
                Size = new Size(200, 20)
            };

            newGameTextBox = new TextBox
            {
                Location = new Point(10, 325),
                Size = new Size(180, 25)
            };

            var browseButton = new Button
            {
                Text = "浏览",
                Location = new Point(200, 325),
                Size = new Size(60, 25)
            };
            browseButton.Click += BrowseGame;

            addButton = new Button
            {
                Text = "添加",
                Location = new Point(270, 325),
                Size = new Size(50, 25)
            };
            addButton.Click += AddGame;

            removeButton = new Button
            {
                Text = "删除选中",
                Location = new Point(330, 325),
                Size = new Size(70, 25)
            };
            removeButton.Click += RemoveGame;

            // HDR测试区域
            var hdrTestLabel = new Label
            {
                Text = "HDR测试 (使用 WIN+ALT+B 快捷键):",
                Location = new Point(10, 365),
                Size = new Size(300, 20),
                Font = new Font("Microsoft YaHei", 9, FontStyle.Bold)
            };

            hdrStatusLabel = new Label
            {
                Text = "HDR状态: 未知",
                Location = new Point(10, 390),
                Size = new Size(200, 20),
                ForeColor = Color.Blue
            };

            hdrOnButton = new Button
            {
                Text = "开启HDR",
                Location = new Point(10, 415),
                Size = new Size(80, 35),
                BackColor = Color.LightGreen
            };
            hdrOnButton.Click += TestHDROn;

            hdrOffButton = new Button
            {
                Text = "关闭HDR",
                Location = new Point(100, 415),
                Size = new Size(80, 35),
                BackColor = Color.LightCoral
            };
            hdrOffButton.Click += TestHDROff;

            var infoLabel = new Label
            {
                Text = "注意: 只有支持HDR的显示器才能看到效果",
                Location = new Point(190, 415),
                Size = new Size(200, 35),
                ForeColor = Color.Gray,
                Font = new Font("Microsoft YaHei", 8)
            };

            okButton = new Button
            {
                Text = "确定",
                Location = new Point(220, 500),
                Size = new Size(75, 30),
                DialogResult = DialogResult.OK
            };

            cancelButton = new Button
            {
                Text = "取消",
                Location = new Point(305, 500),
                Size = new Size(75, 30),
                DialogResult = DialogResult.Cancel
            };

            this.Controls.AddRange(new Control[] { 
                label, gameListBox, addLabel, newGameTextBox, browseButton,
                addButton, removeButton, hdrTestLabel, hdrStatusLabel,
                hdrOnButton, hdrOffButton, infoLabel, okButton, cancelButton 
            });
        }

        private void LoadGames(HashSet<string> games)
        {
            foreach (var game in games.OrderBy(g => g))
            {
                gameListBox.Items.Add(game, true);
            }
        }

        private void AddGame(object sender, EventArgs e)
        {
            string gameName = newGameTextBox.Text.Trim();
            if (!string.IsNullOrEmpty(gameName))
            {
                if (!gameName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    gameName += ".exe";
                }

                if (!gameListBox.Items.Contains(gameName))
                {
                    gameListBox.Items.Add(gameName, true);
                    newGameTextBox.Clear();
                }
                else
                {
                    MessageBox.Show("该游戏已存在于列表中", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        private void BrowseGame(object sender, EventArgs e)
        {
            using (var openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*";
                openFileDialog.Title = "选择游戏可执行文件";
                openFileDialog.InitialDirectory = @"C:\Program Files";

                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    string fileName = Path.GetFileName(openFileDialog.FileName);
                    newGameTextBox.Text = fileName;
                }
            }
        }

        private void RemoveGame(object sender, EventArgs e)
        {
            var selectedItems = gameListBox.CheckedItems.Cast<string>().ToList();
            foreach (var item in selectedItems)
            {
                gameListBox.Items.Remove(item);
            }
        }

        private void TestHDROn(object sender, EventArgs e)
        {
            try
            {
                SendHDRToggleKey();
                hdrStatusLabel.Text = "HDR状态: 已发送开启命令";
                hdrStatusLabel.ForeColor = Color.Green;
                MessageBox.Show("已发送HDR开启命令 (WIN+ALT+B)\n请检查显示器是否切换到HDR模式。", 
                              "HDR测试", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"HDR开启测试失败: {ex.Message}", "错误", 
                              MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void TestHDROff(object sender, EventArgs e)
        {
            try
            {
                SendHDRToggleKey();
                hdrStatusLabel.Text = "HDR状态: 已发送关闭命令";
                hdrStatusLabel.ForeColor = Color.Red;
                MessageBox.Show("已发送HDR关闭命令 (WIN+ALT+B)\n请检查显示器是否切换到SDR模式。", 
                              "HDR测试", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"HDR关闭测试失败: {ex.Message}", "错误", 
                              MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SendHDRToggleKey()
        {
            // 模拟按下 WIN + ALT + B 快捷键来切换HDR
            // 按下Windows键
            keybd_event(0x5B, 0, 0, UIntPtr.Zero);
            
            // 按下Alt键
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            
            // 按下B键
            keybd_event(0x42, 0, 0, UIntPtr.Zero);
            
            // 等待一小段时间
            System.Threading.Thread.Sleep(50);
            
            // 释放B键
            keybd_event(0x42, 0, 0x0002, UIntPtr.Zero);
            
            // 释放Alt键
            keybd_event(0x12, 0, 0x0002, UIntPtr.Zero);
            
            // 释放Windows键
            keybd_event(0x5B, 0, 0x0002, UIntPtr.Zero);
        }

        // Windows API 声明 - 用于模拟按键
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        public HashSet<string> GetSelectedGames()
        {
            return new HashSet<string>(
                gameListBox.Items.Cast<string>(),
                StringComparer.OrdinalIgnoreCase
            );
        }
    }
}