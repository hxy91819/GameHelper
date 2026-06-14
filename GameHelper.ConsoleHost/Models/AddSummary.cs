namespace GameHelper.ConsoleHost.Models
{
    public sealed class AddSummary
    {
        public int Added { get; set; }
        public int Updated { get; set; }
        public int Skipped { get; set; }
        public int DuplicatesRemoved { get; set; }
        public string ConfigPath { get; set; } = string.Empty;
    }
}
