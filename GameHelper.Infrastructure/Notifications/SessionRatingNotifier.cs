using GameHelper.Core.Abstractions;
using Microsoft.Extensions.Logging;
namespace GameHelper.Infrastructure.Notifications;

/// <summary>Decouples completion from the native tray pump; delivery is deliberately not persisted or retried.</summary>
public sealed class SessionRatingNotifier(Action<Guid> deliver, ILogger<SessionRatingNotifier> logger) : ISessionRatingNotifier
{
    public void Invite(Guid sessionId) => _ = Task.Run(() =>
    {
        try { deliver(sessionId); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not deliver session rating invitation"); }
    });
}
