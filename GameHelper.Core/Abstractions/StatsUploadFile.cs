using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

/// <summary>待上传到远端仓库的单个文件。</summary>
public sealed record StatsUploadFile(string RelativePath, string Content);
