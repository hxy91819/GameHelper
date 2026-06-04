namespace GameHelper.Tests.Desktop;

internal static class DesktopTestPolicy
{
    public static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ElementTimeout = TimeSpan.FromSeconds(10);
    public const int MaxRetries = 2;

    public static string ArtifactDirectory
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GameHelper",
                "UiTestArtifacts");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
