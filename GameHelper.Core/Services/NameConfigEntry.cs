using GameHelper.Core.Models;

namespace GameHelper.Core.Services;

/// <summary>
/// 用于 L2 模糊匹配的名称-配置映射条目。
/// 包含归一化的可执行文件名、大写形式（用于 FuzzySharp 匹配）及对应配置。
/// </summary>
internal sealed record NameConfigEntry(string ExecutableName, string ExecutableNameUpper, GameConfig Config);
