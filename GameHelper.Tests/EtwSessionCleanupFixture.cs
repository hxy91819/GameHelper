using Xunit;

namespace GameHelper.Tests;

/// <summary>
/// Serializes ETW tests. Each test disposes only its own monitor; never sweep
/// GameHelper sessions because another one may belong to the user's live instance.
/// </summary>
[CollectionDefinition("ETW")]
public class EtwCollection { }
