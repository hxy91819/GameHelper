using System;
using System.IO;

namespace GameHelper.Core.Utilities;

public static class AppDataPath
{
    /// <summary>
    /// 覆盖此环境变量可将整个数据目录（配置 + 游玩时长）重定向到指定目录，
    /// 用于测试实例在不影响用户真实数据的前提下加载相同结构的数据。
    /// </summary>
    public const string DataDirectoryEnvironmentVariable = "GAMEHELPER_DATA_DIR";

    public static string GetBaseDirectory()
    {
        var overrideDir = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            return overrideDir;
        }

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
