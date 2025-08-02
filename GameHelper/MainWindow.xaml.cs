using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace GameHelper
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        // Windows API 声明 - 用于模拟按键
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        // 按键常量
        private const int VK_LWIN = 0x5B;      // 左Windows键
        private const int VK_MENU = 0x12;      // Alt键
        private const int VK_B = 0x42;         // B键
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private NotifyIcon? notifyIcon;
        private ManagementEventWatcher? processStartWatcher;
        private ManagementEventWatcher? processStopWatcher;
        private HashSet<string> gameProcesses = new HashSet<string>();
        private Dictionary<string, GameConfig> monitoredGames = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase);
        private bool isHDREnabled = false;
        private string configPath = "";

        // 数据绑定属性
        public ObservableCollection<GameItem> Games { get; set; } = new ObservableCollection<GameItem>();
        
        private string _newGameName = "";
        public string NewGameName
        {
            get => _newGameName;
            set
            {
                _newGameName = value;
                OnPropertyChanged(nameof(NewGameName));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            
            InitializeSystemTray();
            LoadConfiguration();
            StartProcessMonitoring();
            CheckInitialHDRState();
            
            // 绑定游戏列表
            GameListControl.ItemsSource = Games;
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
            contextMenu.Items.Add("显示主窗口", null, ShowMainWindow);
            contextMenu.Items.Add("当前状态", null, ShowStatus);
            contextMenu.Items.Add("-");
            contextMenu.Items.Add("退出", null, ExitApplication);
            
            notifyIcon.ContextMenuStrip = contextMenu;
            notifyIcon.DoubleClick += (s, e) => ShowMainWindow(s, e);
        }

        private void LoadConfiguration()
        {
            configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
                                    "GameHelper", "config.json");
            
            Directory.CreateDirectory(Path.GetDirectoryName(configPath) ?? "");
            
            if (File.Exists(configPath))
            {
                try
                {
                    var json = File.ReadAllText(configPath);
                    var config = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                    
                    if (config?.ContainsKey("games") == true)
                    {
                        // 尝试加载新格式的配置
                        try
                        {
                            var gameConfigs = JsonSerializer.Deserialize<GameConfig[]>(config["games"].ToString() ?? "");
                            if (gameConfigs != null)
                            {
                                monitoredGames = gameConfigs.ToDictionary(g => g.Name, g => g, StringComparer.OrdinalIgnoreCase);
                            }
                        }
                        catch
                        {
                            // 兼容旧格式配置
                            var games = JsonSerializer.Deserialize<string[]>(config["games"].ToString() ?? "");
                            if (games != null)
                            {
                                monitoredGames = games.ToDictionary(
                                    g => g, 
                                    g => new GameConfig { Name = g, IsEnabled = true, HDREnabled = true }, 
                                    StringComparer.OrdinalIgnoreCase);
                            }
                        }
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
                var defaultGames = new[]
                {
                    "cyberpunk2077.exe",
                    "witcher3.exe", 
                    "rdr2.exe",
                    "msfs.exe",
                    "forza_horizon_5.exe"
                };
                
                monitoredGames = defaultGames.ToDictionary(
                    g => g,
                    g => new GameConfig { Name = g, IsEnabled = true, HDREnabled = true },
                    StringComparer.OrdinalIgnoreCase);
                    
                SaveConfiguration();
            }

            // 更新UI
            RefreshGameList();
        }

        private void RefreshGameList()
        {
            Games.Clear();
            foreach (var game in monitoredGames.OrderBy(g => g.Key))
            {
                Games.Add(new GameItem { Name = game.Key, IsEnabled = game.Value.IsEnabled, HDREnabled = game.Value.HDREnabled });
            }
        }

        private void SaveConfiguration()
        {
            try
            {
                // 保存游戏配置数组，包含启用状态
                var gameConfigs = monitoredGames.Values.ToArray();
                var config = new Dictionary<string, object>
                {
                    ["games"] = gameConfigs
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
                              "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                System.Windows.Application.Current.Shutdown();
            }
        }

        private void OnProcessStarted(object sender, EventArrivedEventArgs e)
        {
            try
            {
                string? processName = e.NewEvent["ProcessName"]?.ToString();
                if (string.IsNullOrEmpty(processName)) return;

                var eventTime = DateTime.Now.ToString("HH:mm:ss.fff");
                System.Diagnostics.Debug.WriteLine($"[{eventTime}] 进程启动事件: {processName}");

                // 检查游戏是否在监控列表中且已启用
                if (monitoredGames.ContainsKey(processName))
                {
                    var gameConfig = monitoredGames[processName];
                    if (gameConfig.IsEnabled)
                    {
                        gameProcesses.Add(processName);
                        if (!isHDREnabled)
                        {
                            EnableHDR();
                            ShowNotification($"[{eventTime}] 检测到游戏 {processName}，已开启HDR", ToolTipIcon.Info);
                        }
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
                string? processName = e.NewEvent["ProcessName"]?.ToString();
                if (string.IsNullOrEmpty(processName)) return;

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
            var runningProcesses = Process.GetProcesses();
            foreach (var process in runningProcesses)
            {
                try
                {
                    string processName = process.ProcessName + ".exe";
                    // 检查游戏是否在监控列表中且已启用
                    if (monitoredGames.ContainsKey(processName))
                    {
                        var gameConfig = monitoredGames[processName];
                        if (gameConfig.IsEnabled)
                        {
                            gameProcesses.Add(processName);
                        }
                    }
                }
                catch { }
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
                if (!isHDREnabled)
                {
                    SendHDRToggleKey();
                    isHDREnabled = true;
                    
                    Dispatcher.Invoke(() =>
                    {
                        HDRStatusLabel.Content = "HDR状态: 已开启";
                        HDRStatusLabel.Foreground = System.Windows.Media.Brushes.Green;
                    });
                    
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
                if (isHDREnabled)
                {
                    SendHDRToggleKey();
                    isHDREnabled = false;
                    
                    Dispatcher.Invoke(() =>
                    {
                        HDRStatusLabel.Content = "HDR状态: 已关闭";
                        HDRStatusLabel.Foreground = System.Windows.Media.Brushes.Red;
                    });
                    
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
                keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
                keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                keybd_event(VK_B, 0, 0, UIntPtr.Zero);
                
                System.Threading.Thread.Sleep(50);
                
                keybd_event(VK_B, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch (Exception ex)
            {
                ShowNotification($"发送HDR快捷键失败: {ex.Message}", ToolTipIcon.Error);
            }
        }

        // UI事件处理
        private void BrowseGame_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                Title = "选择游戏可执行文件",
                InitialDirectory = @"C:\Program Files"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                NewGameName = Path.GetFileName(openFileDialog.FileName);
            }
        }

        private void AddGame_Click(object sender, RoutedEventArgs e)
        {
            string gameName = NewGameName.Trim();
            if (!string.IsNullOrEmpty(gameName))
            {
                if (!gameName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    gameName += ".exe";
                }

                if (!monitoredGames.ContainsKey(gameName))
                {
                    monitoredGames.Add(gameName, new GameConfig { Name = gameName, IsEnabled = true, HDREnabled = true });
                    Games.Add(new GameItem { Name = gameName, IsEnabled = true });
                    NewGameName = "";
                }
                else
                {
                    MessageBox.Show("该游戏已存在于列表中", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        private void RemoveGame_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button && button.Tag is string gameName)
            {
                monitoredGames.Remove(gameName);
                var gameItem = Games.FirstOrDefault(g => g.Name == gameName);
                if (gameItem != null)
                {
                    Games.Remove(gameItem);
                }
            }
        }

        private void EnableHDR_Click(object sender, RoutedEventArgs e)
        {
            SendHDRToggleKey();
            HDRStatusLabel.Content = "HDR状态: 已发送开启命令";
            HDRStatusLabel.Foreground = System.Windows.Media.Brushes.Green;
            MessageBox.Show("已发送HDR开启命令 (WIN+ALT+B)\n请检查显示器是否切换到HDR模式。", 
                          "HDR测试", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void DisableHDR_Click(object sender, RoutedEventArgs e)
        {
            SendHDRToggleKey();
            HDRStatusLabel.Content = "HDR状态: 已发送关闭命令";
            HDRStatusLabel.Foreground = System.Windows.Media.Brushes.Red;
            MessageBox.Show("已发送HDR关闭命令 (WIN+ALT+B)\n请检查显示器是否切换到SDR模式。", 
                          "HDR测试", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ShowStatus_Click(object sender, RoutedEventArgs e)
        {
            ShowStatus(sender, EventArgs.Empty);
        }

        private void SaveConfig_Click(object sender, RoutedEventArgs e)
        {
            SaveConfiguration();
            MessageBox.Show("配置已保存", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void MinimizeToTray_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        // 系统托盘事件
        private void ShowMainWindow(object? sender, EventArgs e)
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void ShowStatus(object? sender, EventArgs e)
        {
            string status = $"HDR状态: {(isHDREnabled ? "开启" : "关闭")}\n" +
                           $"运行中的游戏: {gameProcesses.Count}\n" +
                           $"监控的游戏数量: {monitoredGames.Count}";
            
            if (gameProcesses.Count > 0)
            {
                status += "\n\n当前运行的游戏:\n" + string.Join("\n", gameProcesses);
            }

            MessageBox.Show(status, "GameHelper 状态", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ShowNotification(string message, ToolTipIcon icon)
        {
            notifyIcon?.ShowBalloonTip(3000, "GameHelper", message, icon);
        }

        private void ExitApplication(object? sender, EventArgs e)
        {
            if (isHDREnabled)
            {
                DisableHDR();
            }
            
            processStartWatcher?.Stop();
            processStopWatcher?.Stop();
            notifyIcon?.Dispose();
            System.Windows.Application.Current.Shutdown();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }

        protected override void OnClosed(EventArgs e)
        {
            processStartWatcher?.Dispose();
            processStopWatcher?.Dispose();
            notifyIcon?.Dispose();
            base.OnClosed(e);
        }
    }

    // 数据模型
    public class GameItem : INotifyPropertyChanged
    {
        private string _name = "";
        private bool _isEnabled = true;
        private bool _hdrEnabled = true;

        public string Name
        {
            get => _name;
            set
            {
                _name = value;
                OnPropertyChanged(nameof(Name));
            }
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                _isEnabled = value;
                OnPropertyChanged(nameof(IsEnabled));
            }
        }

        public bool HDREnabled
        {
            get => _hdrEnabled;
            set
            {
                _hdrEnabled = value;
                OnPropertyChanged(nameof(HDREnabled));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    // 配置数据模型
    public class GameConfig
    {
        public string Name { get; set; } = "";
        public bool IsEnabled { get; set; } = true;
        public bool HDREnabled { get; set; } = true;
    }
}