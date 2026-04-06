using BanditMilitias.Infrastructure;
using TaleWorlds.Library;

namespace BanditMilitias.Debug
{
    public static class DebugLogger
    {
        public static void Info(string category, string message)
            => Write("INFO", category, message, Colors.Cyan, false);

        public static void Warning(string category, string message)
            => Write("WARN", category, message, Colors.Yellow, true);

        public static void Error(string category, string message)
            => Write("ERROR", category, message, Colors.Red, true);

        public static void Log(string message)
            => Write("LOG", null, message, Colors.White, false);

        public static void TestLog(string message)
            => TestLog(message, Colors.Cyan);

        public static void TestLog(string message, Color color)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            try
            {
                FileLogger.Log($"[TEST] {message}");

                if (ShouldShowMessages())
                {
                    InformationManager.DisplayMessage(new InformationMessage(message, color));
                }
            }
            catch
            {
            }
        }

        private static void Write(string level, string? category, string message, Color color, bool alwaysFileLog)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            string formatted = string.IsNullOrEmpty(category)
                ? $"[{level}] {message}"
                : $"[{level}][{category}] {message}";

            try
            {
                if (alwaysFileLog || Settings.Instance?.EnableFileLogging == true || Settings.Instance?.TestingMode == true)
                {
                    FileLogger.Log(formatted);
                }

                if (ShouldShowMessages())
                {
                    InformationManager.DisplayMessage(new InformationMessage(formatted, color));
                }
            }
            catch
            {
            }
        }

        private static bool ShouldShowMessages()
            => Settings.Instance?.ShowTestMessages == true || Settings.Instance?.TestingMode == true;
    }
}
