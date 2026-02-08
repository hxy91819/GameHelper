using System;
using System.IO;

namespace GameHelper.Core.Utilities;

public static class AppDataPath
{
    public static string GetBaseDirectory()
    {
        var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdgConfigHome))
        {
            return xdgConfigHome;
        }

        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (!string.IsNullOrWhiteSpace(appData))
        {
            return appData;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    }

    public static string GetGameHelperDirectory() => Path.Combine(GetBaseDirectory(), "GameHelper");

    public static string GetConfigPath() => Path.Combine(GetGameHelperDirectory(), "config.yml");

    public static string GetPlaytimeCsvPath() => Path.Combine(GetGameHelperDirectory(), "playtime.csv");

    public static string GetPlaytimeJsonPath() => Path.Combine(GetGameHelperDirectory(), "playtime.json");
}
