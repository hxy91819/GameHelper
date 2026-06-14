namespace GameHelper.Core.Models;

public sealed record SessionActivitySnapshot(
    IReadOnlySet<SessionActivityKey> Keys,
    IReadOnlyList<SessionActivityRecord> Records,
    string Source);
