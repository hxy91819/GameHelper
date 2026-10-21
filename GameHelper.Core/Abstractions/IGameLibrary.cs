using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

/// <summary>One library interface for in-process orchestration and the Windows desktop transport.</summary>
public interface IGameLibrary
{
    Task<GameLibraryResponse> ExecuteAsync(GameLibraryRequest request, CancellationToken cancellationToken = default);
}
