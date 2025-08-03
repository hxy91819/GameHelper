using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GameHelper
{
    // 游戏会话模型
    public class GameSession
    {
        public DateTime StartTime { get; set; } = DateTime.MinValue;
        public DateTime EndTime { get; set; } = DateTime.MinValue;
        public long DurationMinutes { get; set; } = 0;
    }

    // 游玩时间统计模型
    public class GamePlayTime
    {
        public string GameName { get; set; } = "";
        public List<GameSession> Sessions { get; set; } = new List<GameSession>();
        
        // 为了向后兼容，保留这些属性，但它们将通过Sessions计算得出
        public long TotalMinutes 
        { 
            get { return Sessions.Sum(s => s.DurationMinutes); }
        }
        
        public DateTime LastPlayed 
        { 
            get { return Sessions.Count > 0 ? Sessions.Last().EndTime : DateTime.MinValue; }
        }
        
        public DateTime FirstPlayed 
        { 
            get { return Sessions.Count > 0 ? Sessions.First().StartTime : DateTime.Now; }
        }
    }

    // 游戏时长统计管理器
    public class GamePlayTimeManager
    {
        private Dictionary<string, GamePlayTime> gamePlayTimes = new Dictionary<string, GamePlayTime>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, DateTime> gameStartTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private string playTimeConfigPath = "";

        public GamePlayTimeManager(string configDirectory)
        {
            playTimeConfigPath = Path.Combine(configDirectory, "playtime.json");
            LoadPlayTimeConfiguration();
        }

        // 获取指定游戏的游玩时间信息
        public GamePlayTime GetGamePlayTime(string gameName)
        {
            if (gamePlayTimes.ContainsKey(gameName))
            {
                return gamePlayTimes[gameName];
            }
            
            return new GamePlayTime { GameName = gameName };
        }

        // 开始游戏时长跟踪
        public void StartGamePlayTimeTracking(string gameName)
        {
            if (!gameStartTimes.ContainsKey(gameName))
            {
                gameStartTimes[gameName] = DateTime.Now;
                
                // 初始化游戏游玩时间记录
                if (!gamePlayTimes.ContainsKey(gameName))
                {
                    gamePlayTimes[gameName] = new GamePlayTime 
                    { 
                        GameName = gameName
                    };
                }
            }
        }

        // 停止游戏时长跟踪
        public void StopGamePlayTimeTracking(string gameName)
        {
            if (gameStartTimes.ContainsKey(gameName))
            {
                var startTime = gameStartTimes[gameName];
                var endTime = DateTime.Now;
                var playDuration = endTime - startTime;
                
                // 计算游玩分钟数
                var minutesPlayed = (long)playDuration.TotalMinutes;
                
                // 添加新的游戏会话记录
                if (gamePlayTimes.ContainsKey(gameName))
                {
                    gamePlayTimes[gameName].Sessions.Add(new GameSession
                    {
                        StartTime = startTime,
                        EndTime = endTime,
                        DurationMinutes = minutesPlayed
                    });
                }
                
                // 移除开始时间记录
                gameStartTimes.Remove(gameName);
                
                // 保存游玩时间配置
                SavePlayTimeConfiguration();
            }
        }

        // 获取指定时间段内的游戏时长
        public long GetPlayTimeInPeriod(string gameName, DateTime startDate, DateTime endDate)
        {
            if (gamePlayTimes.ContainsKey(gameName))
            {
                var gamePlayTime = gamePlayTimes[gameName];
                return gamePlayTime.Sessions
                    .Where(s => s.StartTime >= startDate && s.StartTime <= endDate)
                    .Sum(s => s.DurationMinutes);
            }
            return 0;
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
        public int GetSessionCountInPeriod(string gameName, DateTime startDate, DateTime endDate)
        {
            if (gamePlayTimes.ContainsKey(gameName))
            {
                var gamePlayTime = gamePlayTimes[gameName];
                return gamePlayTime.Sessions
                    .Count(s => s.StartTime >= startDate && s.StartTime <= endDate);
            }
            return 0;
        }

        // 获取所有游戏的统计信息
        public Dictionary<string, GamePlayTime> GetAllGamePlayTimes()
        {
            return new Dictionary<string, GamePlayTime>(gamePlayTimes);
        }

        // 加载游玩时间配置
        private void LoadPlayTimeConfiguration()
        {
            if (File.Exists(playTimeConfigPath))
            {
                try
                {
                    var json = File.ReadAllText(playTimeConfigPath);
                    var config = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                    
                    if (config?.ContainsKey("games") == true)
                    {
                        var playTimes = JsonSerializer.Deserialize<GamePlayTime[]>(config["games"].ToString() ?? "");
                        if (playTimes != null)
                        {
                            gamePlayTimes = playTimes.ToDictionary(g => g.GameName, g => g, StringComparer.OrdinalIgnoreCase);
                        }
                    }
                }
                catch
                {
                    // 如果加载失败，使用空的字典
                }
            }
        }

        // 保存游玩时间配置
        private void SavePlayTimeConfiguration()
        {
            try
            {
                var playTimes = gamePlayTimes.Values.ToArray();
                var config = new Dictionary<string, object>
                {
                    ["games"] = playTimes
                };
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(playTimeConfigPath, json);
            }
            catch
            {
                // 如果保存失败，忽略错误
            }
        }

        // 格式化游戏时长显示
        public string FormatPlayTime(long minutes)
        {
            if (minutes <= 0)
                return "0分钟";
                
            var hours = minutes / 60;
            var mins = minutes % 60;
            
            if (hours > 0)
                return $"{hours}小时{mins}分钟";
            else
                return $"{mins}分钟";
        }
    }
}
