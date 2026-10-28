namespace GameHelper.Infrastructure.Storage.Sqlite.Legacy;

/// <summary>Immutable source inventory, including absent formal files; originals live beside this manifest.</summary>
public sealed record ImportManifest(string MigrationId, int CodecVersion, string ProgramVersion,
    DateTimeOffset CreatedAtUtc, IReadOnlyList<ImportSource> Sources);

/// <summary>A source identity is fixed for one inventory, not a content deduplication key.</summary>
public sealed record ImportSource(string SourceId, string Path, string? ConfigPath, string Kind,
    bool Exists, string Sha256, long SizeBytes, string BackupName);

/// <summary>Prepared, validated candidate. Preparing never changes userdata.db or any legacy source.</summary>
public sealed record CandidateImport(string CandidatePath, string ManifestPath, int Accepted, int Skipped);
