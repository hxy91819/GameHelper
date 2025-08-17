using System.Collections.Generic;
using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions
{
    public interface IConfigProvider
    {
        IReadOnlyDictionary<string, GameConfig> Load();
        void Save(IReadOnlyDictionary<string, GameConfig> configs);
    }
}
