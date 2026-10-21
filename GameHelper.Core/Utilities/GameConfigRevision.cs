using System.Security.Cryptography;
using System.Text.Json;
using GameHelper.Core.Models;

namespace GameHelper.Core.Utilities;

/// <summary>Optimistic edit token, checked inside the catalog's atomic configuration change.</summary>
public static class GameConfigRevision
{
    public static string For(GameConfig game) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(game)));
}
