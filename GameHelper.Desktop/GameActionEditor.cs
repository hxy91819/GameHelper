using System.Collections.ObjectModel;
using System.ComponentModel;
using GameHelper.Core.Models;

namespace GameHelper.Desktop;

/// <summary>Editable ordered action list; changes participate in the owning game's draft.</summary>
public sealed class GameActionEditor
{
    private readonly Action _edited;
    public ObservableCollection<GameActionItem> Items { get; } = [];

    public GameActionEditor(IEnumerable<GameAction>? actions, Action edited)
    {
        _edited = edited;
        foreach (var action in actions ?? []) AddItem(action);
    }

    public GameActionItem Add()
    {
        var item = AddItem(new GameAction { When = "beforeLaunch", Type = "run" });
        _edited();
        return item;
    }

    public void Remove(GameActionItem item)
    {
        if (!Items.Remove(item)) return;
        item.PropertyChanged -= OnEdited;
        _edited();
    }

    public void Move(GameActionItem item, int offset)
    {
        var index = Items.IndexOf(item);
        if (index < 0 || index + offset < 0 || index + offset >= Items.Count) return;
        Items.Move(index, index + offset);
        _edited();
    }

    /// <summary>Copies drafts without validation so unfinished paths survive a UI restart.</summary>
    public List<GameAction>? Snapshot() => Items.Count == 0 ? null : Items.Select(item => item.ToAction()).ToList();

    public List<GameAction>? Save()
    {
        var actions = Snapshot();
        foreach (var action in actions ?? []) action.Validate();
        return actions;
    }

    private GameActionItem AddItem(GameAction action)
    {
        var item = new GameActionItem(action);
        item.PropertyChanged += OnEdited;
        Items.Add(item);
        return item;
    }

    private void OnEdited(object? sender, PropertyChangedEventArgs e) => _edited();
}
