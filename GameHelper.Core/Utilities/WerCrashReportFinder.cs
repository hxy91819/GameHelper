using System;
using System.Globalization;
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
    /// Finds the most recent WER <c>AppCrash_&lt;executableName&gt;</c> report whose crash time is at or
    /// after <paramref name="afterUtc"/>. WER archives reports some time after the crash, so the
    /// crash timestamp inside <c>Report.wer</c> is used when available and the archive directory's
    /// creation time otherwise. Returns a human-readable hint (report id or path) or null when
    /// nothing is found or the lookup fails; never throws.
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

                var reportPath = Path.Combine(directory, "Report.wer");
                var crashTimeUtc = ReadEventTimeUtc(reportPath) ?? Directory.GetCreationTimeUtc(directory);
                if (crashTimeUtc < afterUtc || crashTimeUtc <= newestTime)
                {
                    continue;
                }

                newestTime = crashTimeUtc;
                newestDirectory = directory;
            }

            if (newestDirectory is null)
            {
                return null;
            }

            var identifier = ReadReportIdentifier(Path.Combine(newestDirectory, "Report.wer"));
            if (!string.IsNullOrWhiteSpace(identifier))
            {
                return $"ReportIdentifier={identifier} (dir {Path.GetFileName(newestDirectory)})";
            }

            return newestDirectory;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static DateTime? ReadEventTimeUtc(string reportPath)
    {
        try
        {
            if (!File.Exists(reportPath))
            {
                return null;
            }

            foreach (var rawLine in File.ReadLines(reportPath))
            {
                if (!rawLine.StartsWith("EventTime=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = rawLine["EventTime=".Length..].Trim();
                if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var fileTimeUtc)
                    && fileTimeUtc > 0
                    && fileTimeUtc <= DateTime.MaxValue.Ticks)
                {
                    return DateTime.FromFileTimeUtc(fileTimeUtc);
                }

                return null;
            }
        }
        catch (Exception)
        {
            // The report text is optional evidence; the archive time is still a usable fallback.
        }

        return null;
    }

    private static string? ReadReportIdentifier(string reportPath)
    {
        try
        {
            if (!File.Exists(reportPath))
            {
                return null;
            }

            foreach (var rawLine in File.ReadLines(reportPath))
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
