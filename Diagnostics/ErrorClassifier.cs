using System;

namespace BanditMilitias.Diagnostics
{
    public enum BootErrorSeverity
    {
        Low,
        Medium,
        High,
        Critical
    }

    public static class ErrorClassifier
    {
        public static BootErrorSeverity Classify(string context, Exception ex)
        {
            if (ex is OutOfMemoryException or AccessViolationException)
            {
                return BootErrorSeverity.Critical;
            }

            if (context.IndexOf("OnGameStart", StringComparison.OrdinalIgnoreCase) >= 0 ||
                context.IndexOf("Deferred", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return BootErrorSeverity.High;
            }

            if (context.IndexOf("Harmony", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return BootErrorSeverity.Medium;
            }

            return BootErrorSeverity.Low;
        }
    }
}
