using System;

namespace GameHelper.ConsoleHost.Services;

internal static class FileDropIpcProtocol
{
    public const string PipeName = "GameHelper.ConsoleHost.FileDrop";
}

internal sealed class DropAddRequest
{
    public string[] Paths { get; set; } = Array.Empty<string>();

    public string? ConfigOverride { get; set; }
}

internal sealed class DropAddResponse
{
    public bool Success { get; set; }

    public int Added { get; set; }

    public int Updated { get; set; }

    public int Skipped { get; set; }

    public int DuplicatesRemoved { get; set; }

    public string ConfigPath { get; set; } = string.Empty;

    public string? Error { get; set; }
}
