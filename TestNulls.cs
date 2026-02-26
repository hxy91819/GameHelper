using System;
using System.Collections.Generic;
using System.Linq;

namespace GameHelper.Core.Models
{
    public class GameConfig
    {
        public GameConfig Clone() => new GameConfig();
    }
}

class Program {
    static void Main() {
        List<GameHelper.Core.Models.GameConfig> list = new List<GameHelper.Core.Models.GameConfig> { null };
        try {
            var result = list?.Select(g => g.Clone()).ToList(); // Should throw NullReferenceException
            Console.WriteLine("Success with null element");
        } catch (Exception ex) {
            Console.WriteLine($"Failed with null element: {ex.GetType().Name}: {ex.Message}");
        }

        List<GameHelper.Core.Models.GameConfig> list2 = null;
        try {
            var result = list2?.Select(g => g.Clone()).ToList(); // Should be safe and return null
            Console.WriteLine(result == null ? "Result is null for null list" : "Result is not null for null list");
        } catch (Exception ex) {
            Console.WriteLine($"Failed with null list: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
