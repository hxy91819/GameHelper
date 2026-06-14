namespace GameHelper.Core.Models;

public readonly record struct SessionActivityKey(string Game, DateTime Start, DateTime End, long DurationMinutes);
