namespace GameHelper.Desktop;

internal sealed partial class ConsoleModeWindow
{
    private void OpenCollection(int index)
    {
        if (index == 4 && !privateCollection.IsUnlocked)
        {
            if (!privateCollection.HasPassword)
            {
                ShowNotice("私人收藏尚未设置密码。请先在桌面游戏库进入私人收藏，设置密码后再使用。");
                return;
            }
            OpenPasswordEntry();
            return;
        }
        OpenCollectionCore(index);
    }

    private void OpenCollectionCore(int index)
    {
        if ((tab == 4) != (index == 4)) { query = ""; launchState.Back(); }
        tab = index; detail = false; confirm = false;
        overlayKind = ConsoleOverlay.None; menuRows.Clear(); confirmationAction = null;
        interactionStatus = ""; settingsStatus = "";
        if (index != 4) privateCollection.Lock();
        Reload();
    }

    private bool CanRevealLaunch => tab == 4 || !model.ConsoleGames.Any(game => game.DataKey == launchState.Key && game.SavedAdult);
}
