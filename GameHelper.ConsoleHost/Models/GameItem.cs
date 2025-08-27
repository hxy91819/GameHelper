using System;
using System.Collections.Generic;

namespace GameHelper.ConsoleHost.Models
{
    // DTOs for JSON mapping used by stats command
    public sealed class GameItem
    {
        public string GameName { get; set; } = string.Empty;
        public List<GameSession> Sessions { get; set; } = new();
    }

    public sealed class GameSession
    {
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public long DurationMinutes { get; set; }
    }

    public sealed class AddSummary
    {
        public int Added { get; set; }
        public int Updated { get; set; }
        public int Skipped { get; set; }
        public int DuplicatesRemoved { get; set; }
        public string ConfigPath { get; set; } = string.Empty;
    }
}