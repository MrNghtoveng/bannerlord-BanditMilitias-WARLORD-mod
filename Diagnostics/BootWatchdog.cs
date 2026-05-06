using BanditMilitias.Infrastructure;
using System;
using System.Diagnostics;

namespace BanditMilitias.Diagnostics
{
    public sealed class BootWatchdog
    {
        public bool WarnIfSlow(string operationName, Stopwatch stopwatch, long thresholdMs = 500)
        {
            if (stopwatch == null)
            {
                return false;
            }

            if (stopwatch.ElapsedMilliseconds <= thresholdMs)
            {
                return false;
            }

            FileLogger.LogWarning($"[BootWatchdog] {operationName} took {stopwatch.ElapsedMilliseconds}ms! Potential hang detected.");
            return true;
        }

        public T Measure<T>(string operationName, Func<T> action, long thresholdMs = 500)
        {
            var watch = Stopwatch.StartNew();
            T result = action();
            watch.Stop();
            _ = WarnIfSlow(operationName, watch, thresholdMs);
            return result;
        }
    }
}
