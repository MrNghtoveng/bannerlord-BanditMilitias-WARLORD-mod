using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using BanditMilitias.Debug;
using TaleWorlds.CampaignSystem.Settlements;

namespace BanditMilitias.Infrastructure
{
    public static class ModActivationManager
    {
        private static CampaignTime? _activationDelayStartTime = null;
        private static bool _activationDelayStartLogged = false;
        private static bool _gameplayActivationSwitchClosed = false;
        private static bool _gameplayActivationSwitchLogged = false;
        private static bool _isGameFullyInitializedCache = false;

        public static void Reset()
        {
            _activationDelayStartTime = null;
            _activationDelayStartLogged = false;
            _gameplayActivationSwitchClosed = false;
            _gameplayActivationSwitchLogged = false;
            _isGameFullyInitializedCache = false;
        }

        public static bool TryGetActivationDelayState(out double startHours, out bool switchClosed)
        {
            switchClosed = _gameplayActivationSwitchClosed;
            if (_activationDelayStartTime.HasValue)
            {
                startHours = _activationDelayStartTime.Value.ToHours;
                return true;
            }

            startHours = -1d;
            return false;
        }

        public static void RestoreActivationDelayState(double startHours, bool switchClosed)
        {
            _activationDelayStartTime = startHours >= 0d
                ? CampaignTime.Hours((float)startHours)
                : null;
            _activationDelayStartLogged = _activationDelayStartTime.HasValue;
            _gameplayActivationSwitchClosed = switchClosed;
            _gameplayActivationSwitchLogged = switchClosed;
            _isGameFullyInitializedCache = false;
        }

        public static void InvalidateGameInitializationCache()
        {
            _isGameFullyInitializedCache = false;
        }

        private static bool IsCampaignMapActive()
        {
            var stateManager = TaleWorlds.Core.Game.Current?.GameStateManager;
            return stateManager?.ActiveState is TaleWorlds.CampaignSystem.GameState.MapState;
        }

        public static bool TryStartActivationDelayClock()
        {
            if (_activationDelayStartTime.HasValue) return true;
            if (Campaign.Current == null) return false;

            _activationDelayStartTime = CampaignTime.Now;

            if (!_activationDelayStartLogged)
            {
                _activationDelayStartLogged = true;
                try
                {
                    string mapReady = IsCampaignMapActive() ? "MapState=active" : "MapState=pending(early-start)";
                    FileLogger.Log(
                        $"ActivationDelayClock started at campaign day {CampaignTime.Now.ToDays:F2} " +
                        $"(mode={(SubModule.IsSandboxMode ? "Sandbox" : "Campaign")}, {mapReady}).");
                }
                catch
                {
                }
            }

            return true;
        }

        public static CampaignTime GetActivationDelayStartTime()
        {
            _ = TryStartActivationDelayClock();
            return _activationDelayStartTime ?? CampaignTime.Zero;
        }

        private static CampaignTime ResolveActivationDelayAnchor()
        {
            if (_activationDelayStartTime.HasValue)
                return _activationDelayStartTime.Value;

            CampaignTime campaignStart = CompatibilityLayer.GetCampaignStartTime();
            if (campaignStart != CampaignTime.Zero)
                return campaignStart;

            _ = TryStartActivationDelayClock();
            return _activationDelayStartTime ?? CampaignTime.Zero;
        }

        public static float GetActivationDelayElapsedDays()
        {
            if (Campaign.Current == null) return 0f;

            CampaignTime anchor = ResolveActivationDelayAnchor();
            if (anchor == CampaignTime.Zero) return 0f;

            float elapsedDays = (float)(CampaignTime.Now - anchor).ToDays;
            if (elapsedDays >= 0f) return elapsedDays;

            _activationDelayStartTime = CampaignTime.Now;
            return 0f;
        }

        public static bool HasActivationDelayElapsed(int requiredDays)
        {
            if (requiredDays <= 0) return true;
            return GetActivationDelayElapsedDays() >= requiredDays;
        }

        public static bool TryCloseGameplayActivationSwitch()
        {
            if (_gameplayActivationSwitchClosed) return true;

            int requiredDays = System.Math.Max(0, Settings.Instance?.ActivationDelay ?? 2);
            float elapsedDays = GetActivationDelayElapsedDays();
            if (elapsedDays < requiredDays) return false;

            _gameplayActivationSwitchClosed = true;

            if (!_gameplayActivationSwitchLogged)
            {
                _gameplayActivationSwitchLogged = true;
                try
                {
                    FileLogger.Log(
                        $"GameplayActivationSwitch CLOSED after {elapsedDays:F2} in-game days. " +
                        $"Anchor day {ResolveActivationDelayAnchor().ToDays:F2}, current day {CampaignTime.Now.ToDays:F2}.");
                }
                catch { }
            }

            return true;
        }

        public static bool IsGameplayActivationSwitchClosed()
        {
            if (Settings.Instance?.TestingMode == true && _gameplayActivationSwitchClosed)
                return true;

            return _gameplayActivationSwitchClosed || TryCloseGameplayActivationSwitch();
        }

        public static bool IsGameplayActivationDelayed()
        {
            return !IsGameplayActivationSwitchClosed();
        }

        public static void ForceEnergizeActivationSwitch()
        {
            _gameplayActivationSwitchClosed = true;
            FileLogger.Log("GameplayActivationSwitch FORCE CLOSED by external tool. System energized.");
        }

        public static bool IsGameFullyInitialized()
        {
            if (_isGameFullyInitializedCache) return true;

            try
            {
                var campaignType = Type.GetType("TaleWorlds.CampaignSystem.Campaign, TaleWorlds.CampaignSystem");
                if (campaignType == null) return false;

                if (Campaign.Current == null)
                    return false;

                if (TaleWorlds.Core.Game.Current?.GameStateManager?.ActiveState == null ||
                    !(TaleWorlds.Core.Game.Current.GameStateManager.ActiveState is TaleWorlds.CampaignSystem.GameState.MapState))
                    return false;

                if (Settlement.All == null || Settlement.All.Count == 0)
                    return false;

                _isGameFullyInitializedCache = true;
                return true;
            }
            catch (Exception)
            {
                // Relay to internal logger if possible, otherwise silent fail as per original
                return false;
            }
        }

        public static bool IsGameFullyInitializedCached => _isGameFullyInitializedCache;
    }
}
