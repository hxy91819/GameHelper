using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Core.Services;

/// <summary>构建完成的推送内容：文件集合 + 汇总元数据。</summary>
public sealed record StatsReport(
    IReadOnlyList<StatsUploadFile> Files,
    int SessionCount,
    int GameCount,
    long TotalMinutes,
    string ContentHash);
