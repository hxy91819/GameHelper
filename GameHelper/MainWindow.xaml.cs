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
        
        // 游戏游玩时间管理器
        private GamePlayTimeManager playTimeManager;
        private string playTimeConfigPath = "";
        private System.Timers.Timer? playTimeSaveTimer;

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
            
            string appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameHelper");
            configPath = Path.Combine(appDataPath, "config.json");
            playTimeConfigPath = Path.Combine(appDataPath, "playtime.json");
            
            // 初始化游戏游玩时间管理器
            playTimeManager = new GamePlayTimeManager(appDataPath);
            
            // 加载配置
            LoadConfiguration();
            
            // 初始化游戏列表
            GameListControl.ItemsSource = Games;
            
            // 初始化WMI事件监听
            StartProcessMonitoring();
            
            // 初始化游玩时间保存定时器
            InitializePlayTimeTimer();
            
            // 初始化系统托盘
            InitializeSystemTray();
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

        private void LoadPlayTimeConfiguration()
        {
            // 游玩时间配置现在由GamePlayTimeManager管理
        }

        private void SavePlayTimeConfiguration()
        {
            // 游玩时间配置现在由GamePlayTimeManager管理
        }

        private void RefreshGameList()
        {
            Games.Clear();
            foreach (var game in monitoredGames.OrderBy(g => g.Key))
            {
                var gameItem = new GameItem 
                { 
                    Name = game.Key, 
                    IsEnabled = game.Value.IsEnabled, 
                    HDREnabled = game.Value.HDREnabled,
                    PlayTimeInfo = GetGamePlayTimeInfo(game.Key)
                };
                Games.Add(gameItem);
            }
        }

        private string GetGamePlayTimeInfo(string gameName)
        {
            var playTime = playTimeManager.GetGamePlayTime(gameName);
            var totalMinutes = playTime.TotalMinutes;
            
            if (totalMinutes <= 0)
                return "未游玩";
                
            var hours = totalMinutes / 60;
            var mins = totalMinutes % 60;
            
            var lastPlayed = playTime.LastPlayed;
            var lastPlayedText = lastPlayed != DateTime.MinValue ? $" (上次: {lastPlayed:yyyy-MM-dd})" : "";
            
            if (hours > 0)
                return $"{hours}小时{mins}分钟{lastPlayedText}";
            else
                return $"{mins}分钟{lastPlayedText}";
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
                        // 开始记录游玩时间
                        playTimeManager.StartGamePlayTimeTracking(processName);
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
                ShowNotification($"处理进程开始事件失败: {ex.Message}", ToolTipIcon.Warning);
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
                    // 停止记录游玩时间
                    playTimeManager.StopGamePlayTimeTracking(processName);
                    if (gameProcesses.Count == 0 && isHDREnabled)
                    {
                        DisableHDR();
                        ShowNotification($"[{eventTime}] 游戏 {processName} 已关闭，已关闭HDR", ToolTipIcon.Info);
                    }
                    else
                    {
                        ShowNotification($"[{eventTime}] 游戏 {processName} 已关闭", ToolTipIcon.Info);
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
                            // 记录游戏开始时间
                            playTimeManager.StartGamePlayTimeTracking(processName);
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

        private void UpdateGamePlayTimeInfo(string gameName)
        {
            var gameItem = Games.FirstOrDefault(g => g.Name.Equals(gameName, StringComparison.OrdinalIgnoreCase));
            if (gameItem != null)
            {
                gameItem.PlayTimeInfo = GetGamePlayTimeInfo(gameName);
            }
        }

        // 获取指定时间段内的游戏时长
        private long GetPlayTimeInPeriod(string gameName, DateTime startDate, DateTime endDate)
        {
            return playTimeManager.GetPlayTimeInPeriod(gameName, startDate, endDate);
        }

        // 获取过去一周的游戏时长
        public long GetWeeklyPlayTime(string gameName)
        {
            var now = DateTime.Now;
            var oneWeekAgo = now.AddDays(-7);
            return GetPlayTimeInPeriod(gameName, oneWeekAgo, now);
        }

        // 获取过去一个月的游戏时长
        public long GetMonthlyPlayTime(string gameName)
        {
            var now = DateTime.Now;
            var oneMonthAgo = now.AddMonths(-1);
            return GetPlayTimeInPeriod(gameName, oneMonthAgo, now);
        }

        // 获取过去一年的游戏时长
        public long GetYearlyPlayTime(string gameName)
        {
            var now = DateTime.Now;
            var oneYearAgo = now.AddYears(-1);
            return GetPlayTimeInPeriod(gameName, oneYearAgo, now);
        }

        // 获取指定日期范围的游戏会话次数
        private int GetSessionCountInPeriod(string gameName, DateTime startDate, DateTime endDate)
        {
            return playTimeManager.GetSessionCountInPeriod(gameName, startDate, endDate);
        }

        private void InitializePlayTimeTimer()
        {
            // 每30分钟保存一次游玩时间
            playTimeSaveTimer = new System.Timers.Timer(30 * 60 * 1000); // 30分钟
            playTimeSaveTimer.Elapsed += (sender, e) => 
            {
                SavePlayTimeConfiguration();
                // 更新所有游戏的游玩时间显示
                foreach (var game in Games)
                {
                    UpdateGamePlayTimeInfo(game.Name);
                }
            };
            playTimeSaveTimer.Start();
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

        private void ShowStatistics_Click(object sender, RoutedEventArgs e)
        {
            // 显示游戏统计窗口
            var statisticsWindow = new StatisticsWindow(playTimeManager);
            statisticsWindow.Show();
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
            // 停止所有正在运行的游戏的计时
            // 注意：由于我们使用了GamePlayTimeManager，这里的游戏开始时间记录在Manager内部
            // 我们需要获取所有当前正在运行的游戏并停止它们的计时
            // 这里简化处理，假设gameProcesses包含了所有正在运行的游戏
            foreach (var gameName in gameProcesses)
            {
                playTimeManager.StopGamePlayTimeTracking(gameName);
            }
            
            if (isHDREnabled)
            {
                DisableHDR();
            }
            
            processStartWatcher?.Stop();
            processStopWatcher?.Stop();
            playTimeSaveTimer?.Stop();
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
        private string _playTimeInfo = "未游玩";

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

        public string PlayTimeInfo
        {
            get => _playTimeInfo;
            set
            {
                _playTimeInfo = value;
                OnPropertyChanged(nameof(PlayTimeInfo));
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