namespace GameHelper.Core.Abstractions;

/// <summary>Sends an informational break notification and clears pending notifications on reset.</summary>
public interface IBreakReminderNotifier : IDisposable
{
    void Show(TimeSpan playtime, int breakMinutes, bool sound);
    void Clear();
}
