using System;
using System.Text;

namespace GameHelper.ConsoleHost.Utilities
{
    internal static class ConsoleEncoding
    {
        private static bool _initialized;

        public static void EnsureUtf8()
        {
            if (_initialized)
            {
                return;
            }

            var utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

            try
            {
                if (Console.OutputEncoding.CodePage != utf8WithoutBom.CodePage)
                {
                    Console.OutputEncoding = utf8WithoutBom;
                }
            }
            catch
            {
                // Some hosts (e.g., redirected output) may not support changing the encoding.
            }

            try
            {
                if (Console.InputEncoding.CodePage != Encoding.UTF8.CodePage)
                {
                    Console.InputEncoding = Encoding.UTF8;
                }
            }
            catch
            {
                // Ignore failures when stdin is redirected or unavailable.
            }

            _initialized = true;
        }
    }
}
