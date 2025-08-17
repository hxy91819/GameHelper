namespace GameHelper.Core.Abstractions
{
    public interface IPlayTimeService
    {
        void StartTracking(string gameName);
        void StopTracking(string gameName);
    }
}
