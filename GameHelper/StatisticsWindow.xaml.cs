using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;

namespace GameHelper
{
    /// <summary>
    /// StatisticsWindow.xaml 的交互逻辑
    /// </summary>
    public partial class StatisticsWindow : Window
    {
        private GamePlayTimeManager _playTimeManager;
        
        public StatisticsWindow(GamePlayTimeManager playTimeManager)
        {
            InitializeComponent();
            _playTimeManager = playTimeManager;
            LoadStatistics();
        }
        
        private void LoadStatistics()
        {
            var allGamePlayTimes = _playTimeManager.GetAllGamePlayTimes();
            var gameNames = allGamePlayTimes.Keys.ToList();
            
            // 加载周统计
            var weeklyStats = new List<StatisticsItem>();
            foreach (var gameName in gameNames)
            {
                var playTime = _playTimeManager.GetWeeklyPlayTime(gameName);
                var sessionCount = _playTimeManager.GetSessionCountInPeriod(gameName, DateTime.Now.AddDays(-7), DateTime.Now);
                weeklyStats.Add(new StatisticsItem
                {
                    GameName = gameName,
                    PlayTime = _playTimeManager.FormatPlayTime(playTime),
                    SessionCount = sessionCount.ToString()
                });
            }
            WeeklyStatsGrid.ItemsSource = weeklyStats;
            
            // 加载月统计
            var monthlyStats = new List<StatisticsItem>();
            foreach (var gameName in gameNames)
            {
                var playTime = _playTimeManager.GetMonthlyPlayTime(gameName);
                var sessionCount = _playTimeManager.GetSessionCountInPeriod(gameName, DateTime.Now.AddMonths(-1), DateTime.Now);
                monthlyStats.Add(new StatisticsItem
                {
                    GameName = gameName,
                    PlayTime = _playTimeManager.FormatPlayTime(playTime),
                    SessionCount = sessionCount.ToString()
                });
            }
            MonthlyStatsGrid.ItemsSource = monthlyStats;
            
            // 加载年统计
            var yearlyStats = new List<StatisticsItem>();
            foreach (var gameName in gameNames)
            {
                var playTime = _playTimeManager.GetYearlyPlayTime(gameName);
                var sessionCount = _playTimeManager.GetSessionCountInPeriod(gameName, DateTime.Now.AddYears(-1), DateTime.Now);
                yearlyStats.Add(new StatisticsItem
                {
                    GameName = gameName,
                    PlayTime = _playTimeManager.FormatPlayTime(playTime),
                    SessionCount = sessionCount.ToString()
                });
            }
            YearlyStatsGrid.ItemsSource = yearlyStats;
        }
        
        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            LoadStatistics();
        }
        
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
    
    public class StatisticsItem : INotifyPropertyChanged
    {
        private string _gameName = "";
        private string _playTime = "";
        private string _sessionCount = "";
        
        public string GameName
        {
            get => _gameName;
            set
            {
                _gameName = value;
                OnPropertyChanged(nameof(GameName));
            }
        }
        
        public string PlayTime
        {
            get => _playTime;
            set
            {
                _playTime = value;
                OnPropertyChanged(nameof(PlayTime));
            }
        }
        
        public string SessionCount
        {
            get => _sessionCount;
            set
            {
                _sessionCount = value;
                OnPropertyChanged(nameof(SessionCount));
            }
        }
        
        public event PropertyChangedEventHandler? PropertyChanged;
        
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
