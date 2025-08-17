namespace GameHelper.Core.Models
{
    public class GameConfig
    {
        public string Name { get; set; } = string.Empty;
        public string? Alias { get; set; }
        public bool IsEnabled { get; set; } = true;
        public bool HDREnabled { get; set; } = true;
    }
}
