namespace GameHelper.Core.Abstractions;

/// <summary>Prepares HDR before launch, then transfers ownership to observed gameplay or expires the pending launch.</summary>
public interface IGameLaunchCoordinator
{
    void Launch(string dataKey);
    IReadOnlyDictionary<string, string> GetGameStates();
}
