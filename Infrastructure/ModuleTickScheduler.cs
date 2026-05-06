using BanditMilitias.Core.Components;
using BanditMilitias.Lifecycle;
using System;
using System.Collections.Generic;

namespace BanditMilitias.Infrastructure
{
    public sealed class ModuleTickScheduler
    {
        private enum TickLane
        {
            Critical,
            Standard,
            Deferred
        }

        private readonly Dictionary<string, float> _accumulators = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<TickLane, float> _intervalByLane = new()
        {
            [TickLane.Critical] = 0f,
            [TickLane.Standard] = 0.10f,
            [TickLane.Deferred] = 0.35f
        };

        public bool ShouldRun(IMilitiaModule module, string moduleName, float dt, ModState state)
        {
            TickLane lane = ResolveLane(module, moduleName, state);
            float interval = _intervalByLane[lane];
            if (interval <= 0f)
            {
                return true;
            }

            string key = string.IsNullOrWhiteSpace(moduleName) ? module.GetType().Name : moduleName;
            float value = _accumulators.TryGetValue(key, out float existing) ? existing + dt : dt;
            if (value < interval)
            {
                _accumulators[key] = value;
                return false;
            }

            _accumulators[key] = 0f;
            return true;
        }

        public void ResetForSessionEnd()
        {
            _accumulators.Clear();
        }

        private static TickLane ResolveLane(IMilitiaModule module, string moduleName, ModState state)
        {
            if (module.IsCritical || state is ModState.Degraded or ModState.EmergencyStop)
            {
                return TickLane.Critical;
            }

            if (moduleName.IndexOf("Diagnostics", StringComparison.OrdinalIgnoreCase) >= 0 ||
                moduleName.IndexOf("Dev", StringComparison.OrdinalIgnoreCase) >= 0 ||
                moduleName.IndexOf("Telemetry", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return TickLane.Deferred;
            }

            return TickLane.Standard;
        }
    }
}
