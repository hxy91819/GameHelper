using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;

namespace GameHelper.Desktop;

/// <summary>Transient editor: loading/cancelling never writes; failed saves retain both fields.</summary>
public sealed class SessionRatingEditorViewModel(IGameLibrary library, RatedSession session) : ObservableState
{
    private RatedSession _session = session;
    private int _stars = session.Rating?.Stars ?? 0;
    private string _thought = session.Rating?.Thought ?? "";
    private string _status = "";
    private bool _busy;
    public IReadOnlyList<int> StarOptions { get; } = [1, 2, 3, 4, 5];
    public int Stars { get => _stars; set => Set(ref _stars, value); }
    public string Thought { get => _thought; set => Set(ref _thought, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }
    public string Description => $"{_session.DataKey} · {(_session.End.Kind == DateTimeKind.Utc ? _session.End.ToLocalTime() : _session.End):yyyy-MM-dd HH:mm} · {_session.Minutes} 分钟";
    public async Task<bool> SaveAsync(bool clear = false)
    {
        if (Busy) return false;
        if (!clear && Stars is < 1 or > 5) { Status = "请选择 1–5 星。"; return false; }
        if (!clear && Thought.Length > SessionRatings.ThoughtLimit) { Status = $"想法最多 {SessionRatings.ThoughtLimit} 个字符。"; return false; }
        Busy = true;
        try
        {
            var response = await library.ExecuteAsync(new() { Operation = GameLibraryOperation.SaveSessionRating,
                SessionRating = new(_session.SessionId, clear ? null : Stars, clear ? null : Thought, _session.Revision) });
            if (!response.Success || response.RatedSession is null) throw new InvalidOperationException(response.Error ?? "评分保存失败。");
            _session = response.RatedSession;
            return true;
        }
        catch (Exception ex) { Status = ex.Message; return false; }
        finally { Busy = false; }
    }
    public async Task ReloadAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            var response = await library.ExecuteAsync(new() { Operation = GameLibraryOperation.GetSessionRating, SessionId = _session.SessionId });
            if (!response.Success || response.RatedSession is null) throw new InvalidOperationException(response.Error);
            _session = response.RatedSession; Stars = _session.Rating?.Stars ?? 0; Thought = _session.Rating?.Thought ?? ""; Status = "已重载最新评分。";
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { Busy = false; }
    }
}
