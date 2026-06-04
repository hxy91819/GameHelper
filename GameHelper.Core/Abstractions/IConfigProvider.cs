using System.Collections.Generic;
using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions
{
    /// <summary>
    /// Loads and saves game configuration entries used by the automation service.
    /// Keys are internal entry identifiers (EntryId).
    /// </summary>
    public interface IConfigProvider
    {
        /// <summary>
        /// Loads configuration from the backing store and returns a dictionary keyed by <see cref="GameConfig.EntryId"/>.
        /// </summary>
        IReadOnlyDictionary<string, GameConfig> Load();

        /// <summary>
        /// Persists the provided configuration to the backing store.
        /// </summary>
        void Save(IReadOnlyDictionary<string, GameConfig> configs);
    }
}
