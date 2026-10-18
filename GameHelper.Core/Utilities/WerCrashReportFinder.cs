using System;
using System.IO;

namespace GameHelper.Core.Utilities;

/// <summary>
/// Best-effort lookup for Windows Error Reporting crash reports. Used by the speed-attach
/// correlation logging so "game died right after Cheat Engine attached" gets evidence instead
/// of guesswork (the BepInEx injection race produced a process-wide crash with zero logs).
/// </summary>
public static class WerCrashReportFinder
{
    /// <summary>
    /// Finds the most recent WER <c>AppCrash_&lt;executableName&gt;</c> report archived after
    /// <paramref name="afterUtc"/>. Returns a human-readable hint (report id or path) or null
    /// when nothing is found or the lookup fails; never throws.
    /// </summary>
    public static string? FindLatest(string? programDataRoot, string? executableName, DateTime afterUtc)
    {
        if (string.IsNullOrWhiteSpace(programDataRoot) || string.IsNullOrWhiteSpace(executableName))
        {
            return null;
        }

        try
        {
            var archive = Path.Combine(programDataRoot, "Microsoft", "Windows", "WER", "ReportArchive");
            if (!Directory.Exists(archive))
            {
                return null;
            }

            const int graceMinutes = 2; // WER archives landing a bit after the actual crash.
            var searchFloor = afterUtc.AddMinutes(-graceMinutes);
            var prefix = "AppCrash_" + executableName;

            string? newestDirectory = null;
            var newestTime = DateTime.MinValue;
            foreach (var directory in Directory.EnumerateDirectories(archive))
            {
                var name = Path.GetFileName(directory);
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var created = Directory.GetCreationTimeUtc(directory);
                if (created < searchFloor || created <= newestTime)
                {
                    continue;
                }

                newestTime = created;
                newestDirectory = directory;
            }

            if (newestDirectory is null)
            {
                return null;
            }

            var reportPath = Path.Combine(newestDirectory, "Report.wer");
            if (File.Exists(reportPath))
            {
                var identifier = ReadReportIdentifier(reportPath);
                if (!string.IsNullOrWhiteSpace(identifier))
                {
                    return $"ReportIdentifier={identifier} (dir {Path.GetFileName(newestDirectory)})";
                }
            }

            return newestDirectory;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ReadReportIdentifier(string reportPath)
    {
        try
        {
            foreach (var rawLine in File.ReadAllLines(reportPath))
            {
                if (rawLine.StartsWith("ReportIdentifier=", StringComparison.OrdinalIgnoreCase))
                {
                    return rawLine["ReportIdentifier=".Length..].Trim();
                }
            }
        }
        catch (Exception)
        {
            // The report text is optional evidence; the directory path is already useful.
        }

        return null;
    }
}