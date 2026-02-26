using System;
using System.Collections.Generic;
using System.Linq;

class Program {
    static void Main() {
        List<string> list = null;
        var result = list?.Select(x => x).ToList();
        Console.WriteLine(result == null ? "Result is null" : "Result is not null");
    }
}
