namespace GameHelper.Core.Models;

/// <summary>A native-independent menu row; a null action is informational.</summary>
public sealed record TrayMenuItem(string Text, TrayMenuAction? Action = null, bool Enabled = false,
    string? DataKey = null);
