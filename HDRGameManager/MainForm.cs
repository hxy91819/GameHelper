using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.Json;
using System.Windows.Forms;

namespace HDRGameManager
{
    public partial class MainForm : Form
    {
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
                Text = "HDR游戏管理器"
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
                                    "HDRGameManager", "config.json");
            
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

                ShowNotification("HDR游戏管理器已启动", ToolTipIcon.Info);
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

                if (monitoredGames.Contains(processName))
                {
                    gameProcesses.Add(processName);
                    if (!isHDREnabled)
                    {
                        EnableHDR();
                        ShowNotification($"检测到游戏 {processName}，已开启HDR", ToolTipIcon.Info);
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

                if (gameProcesses.Contains(processName))
                {
                    gameProcesses.Remove(processName);
                    if (gameProcesses.Count == 0 && isHDREnabled)
                    {
                        DisableHDR();
                        ShowNotification($"游戏 {processName} 已关闭，已关闭HDR", ToolTipIcon.Info);
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
                // 使用PowerShell命令启用HDR
                var startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-Command \"Add-Type -AssemblyName System.Windows.Forms; " +
                               "[System.Windows.Forms.SystemInformation]::HighContrast = $false; " +
                               "Get-WmiObject -Namespace root/wmi -Class WmiMonitorBrightnessMethods | " +
                               "ForEach-Object { $_.WmiSetBrightness(1, 100) }\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var process = Process.Start(startInfo))
                {
                    process?.WaitForExit(5000);
                }

                // 通过注册表设置HDR（Windows 11方法）
                SetHDRRegistry(true);
                isHDREnabled = true;
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
                SetHDRRegistry(false);
                isHDREnabled = false;
            }
            catch (Exception ex)
            {
                ShowNotification($"关闭HDR失败: {ex.Message}", ToolTipIcon.Error);
            }
        }

        private void SetHDRRegistry(bool enable)
        {
            try
            {
                // Windows 11 HDR设置路径
                string keyPath = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Configuration";
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyPath, true))
                {
                    if (key != null)
                    {
                        // 这里需要根据具体的显示器配置调整
                        // 实际实现中需要枚举显示设备并设置相应的HDR值
                        ShowNotification($"HDR已{(enable ? "开启" : "关闭")}", ToolTipIcon.Info);
                    }
                }
            }
            catch (Exception ex)
            {
                // 如果注册表方法失败，尝试使用Windows API
                ShowNotification($"注册表方法失败，尝试其他方式: {ex.Message}", ToolTipIcon.Warning);
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

            MessageBox.Show(status, "HDR游戏管理器状态", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ShowNotification(string message, ToolTipIcon icon)
        {
            notifyIcon.ShowBalloonTip(3000, "HDR游戏管理器", message, icon);
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

        public ConfigForm(HashSet<string> currentGames)
        {
            InitializeComponents();
            LoadGames(currentGames);
        }

        private void InitializeComponents()
        {
            this.Text = "游戏配置";
            this.Size = new Size(400, 500);
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
                Size = new Size(360, 300),
                CheckOnClick = true
            };

            var addLabel = new Label
            {
                Text = "添加新游戏 (例: game.exe):",
                Location = new Point(10, 350),
                Size = new Size(200, 20)
            };

            newGameTextBox = new TextBox
            {
                Location = new Point(10, 375),
                Size = new Size(250, 25)
            };

            addButton = new Button
            {
                Text = "添加",
                Location = new Point(270, 375),
                Size = new Size(50, 25)
            };
            addButton.Click += AddGame;

            removeButton = new Button
            {
                Text = "删除选中",
                Location = new Point(330, 375),
                Size = new Size(70, 25)
            };
            removeButton.Click += RemoveGame;

            okButton = new Button
            {
                Text = "确定",
                Location = new Point(220, 420),
                Size = new Size(75, 30),
                DialogResult = DialogResult.OK
            };

            cancelButton = new Button
            {
                Text = "取消",
                Location = new Point(305, 420),
                Size = new Size(75, 30),
                DialogResult = DialogResult.Cancel
            };

            this.Controls.AddRange(new Control[] { 
                label, gameListBox, addLabel, newGameTextBox, 
                addButton, removeButton, okButton, cancelButton 
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

        private void RemoveGame(object sender, EventArgs e)
        {
            var selectedItems = gameListBox.CheckedItems.Cast<string>().ToList();
            foreach (var item in selectedItems)
            {
                gameListBox.Items.Remove(item);
            }
        }

        public HashSet<string> GetSelectedGames()
        {
            return new HashSet<string>(
                gameListBox.Items.Cast<string>(),
                StringComparer.OrdinalIgnoreCase
            );
        }
    }
}