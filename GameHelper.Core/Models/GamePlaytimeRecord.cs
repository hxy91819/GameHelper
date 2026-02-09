namespace GameHelper.Core.Models;

public sealed class GamePlaytimeRecord
{
    public string GameName { get; set; } = string.Empty;

    public List<PlaySession> Sessions { get; set; } = new();
}
