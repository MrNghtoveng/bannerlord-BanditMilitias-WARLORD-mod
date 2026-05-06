using System;
using System.Diagnostics;
using TaleWorlds.Library;

namespace BanditMilitias.Diagnostics
{
    public sealed class PerformanceTimer : IDisposable
    {
        private readonly string _context;
        private readonly Stopwatch _stopwatch;

        public PerformanceTimer(string context)
        {
            _context = context;
            _stopwatch = Stopwatch.StartNew();
        }

        public void Dispose()
        {
            _stopwatch.Stop();
            if (_stopwatch.ElapsedMilliseconds > 10)
            {
                TaleWorlds.Library.Debug.Print($"[Performance] {_context} took {_stopwatch.ElapsedMilliseconds}ms");
            }
        }
    }
}
