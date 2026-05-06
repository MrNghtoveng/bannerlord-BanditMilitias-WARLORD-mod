using BanditMilitias.Infrastructure;
using System;
using System.Collections.Generic;

namespace BanditMilitias.Lifecycle
{
    public sealed class ModStateController
    {
        private static readonly Dictionary<ModState, HashSet<ModState>> ValidTransitions = new()
        {
            [ModState.Uninitialized] = new HashSet<ModState> { ModState.Loading },
            [ModState.Loading] = new HashSet<ModState> { ModState.Ready, ModState.Degraded, ModState.Failed },
            [ModState.Ready] = new HashSet<ModState> { ModState.Dormant, ModState.Degraded, ModState.Failed, ModState.Uninitialized },
            [ModState.Dormant] = new HashSet<ModState> { ModState.Active, ModState.Degraded, ModState.Failed, ModState.Ready },
            [ModState.Active] = new HashSet<ModState> { ModState.Dormant, ModState.Degraded, ModState.EmergencyStop, ModState.Uninitialized },
            [ModState.Degraded] = new HashSet<ModState> { ModState.Dormant, ModState.Active, ModState.Failed, ModState.EmergencyStop, ModState.Ready },
            [ModState.Failed] = new HashSet<ModState> { ModState.Uninitialized, ModState.EmergencyStop },
            [ModState.EmergencyStop] = new HashSet<ModState> { ModState.Uninitialized }
        };

        private readonly object _stateLock = new();
        private ModState _currentState = ModState.Uninitialized;

        private const int HISTORY_SIZE = 10;
        private readonly (ModState From, ModState To, string Reason, DateTime When)[] _transitionHistory
            = new (ModState, ModState, string, DateTime)[HISTORY_SIZE];
        private int _historyIndex = 0;

        public ModState CurrentState
        {
            get
            {
                lock (_stateLock)
                {
                    return _currentState;
                }
            }
        }

        public bool TryTransition(ModState newState, string reason = "")
        {
            lock (_stateLock)
            {
                ModState oldState = _currentState;
                if (oldState == newState)
                {
                    return true;
                }

                if (!ValidTransitions.TryGetValue(oldState, out HashSet<ModState>? allowed) || !allowed.Contains(newState))
                {
                    FileLogger.LogWarning($"[SubModule] Rejected state transition: {oldState} -> {newState}. Reason={reason}");
                    return false;
                }

                FileLogger.Log($"[SubModule] State Transition: {oldState} -> {newState}" +
                               (string.IsNullOrWhiteSpace(reason) ? string.Empty : $" | {reason}"));
                RecordTransition(oldState, newState, reason);
                _currentState = newState;
                return true;
            }
        }

        public void ForceState(ModState state, string reason = "")
        {
            lock (_stateLock)
            {
                FileLogger.Log($"[SubModule] Forced State Transition: {_currentState} -> {state}" +
                               (string.IsNullOrWhiteSpace(reason) ? string.Empty : $" | {reason}"));
                RecordTransition(_currentState, state, reason);
                _currentState = state;
            }
        }

        private void RecordTransition(ModState from, ModState to, string reason)
        {
            _transitionHistory[_historyIndex % HISTORY_SIZE] = (from, to, reason, DateTime.Now);
            _historyIndex++;
        }

        public string GetDiagnostics()
        {
            lock (_stateLock)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Current State: {_currentState}");
                sb.AppendLine("Recent Transitions:");
                int count = Math.Min(_historyIndex, HISTORY_SIZE);
                int start = _historyIndex >= HISTORY_SIZE ? _historyIndex % HISTORY_SIZE : 0;
                for (int i = 0; i < count; i++)
                {
                    int idx = (start + i) % HISTORY_SIZE;
                    var (from, to, reason, when) = _transitionHistory[idx];
                    sb.AppendLine($"  [{when:HH:mm:ss}] {from} -> {to} | {reason}");
                }
                return sb.ToString();
            }
        }
    }
}
