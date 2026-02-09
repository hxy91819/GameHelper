namespace GameHelper.Tests.Desktop;

internal static class RetryHelper
{
    public static void Execute(Action action, int maxRetries)
    {
        var attempts = 0;
        Exception? lastError = null;

        while (attempts <= maxRetries)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                attempts++;
                if (attempts > maxRetries)
                {
                    break;
                }

                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
        }

        throw new InvalidOperationException($"Action failed after {maxRetries + 1} attempts.", lastError);
    }
}
