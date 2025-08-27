namespace GameHelper.ConsoleHost.Utilities
{
    public static class DurationFormatter
    {
        // Format minutes as "N min" when < 60, otherwise as hours (e.g., "2 h" or "2.5 h")
        public static string Format(long minutes)
        {
            if (minutes < 60) return $"{minutes} min";
            if (minutes % 60 == 0) return $"{minutes / 60} h";
            return $"{(minutes / 60.0):0.0} h";
        }
    }
}