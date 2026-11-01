namespace GameHelper.Desktop;

internal sealed partial class ConsoleModeWindow
{
    private void OpenCollection(int index)
    {
        if ((tab == 4) != (index == 4)) { query = ""; launchState.Back(); }
        tab = index; detail = false; confirm = false;
        overlayKind = ConsoleOverlay.None; menuRows.Clear(); confirmationAction = null;
        interactionStatus = ""; settingsStatus = "";
        Reload();
    }

    private bool CanRevealLaunch => tab == 4 || !model.ConsoleGames.Any(game => game.DataKey == launchState.Key && game.SavedAdult);
}
