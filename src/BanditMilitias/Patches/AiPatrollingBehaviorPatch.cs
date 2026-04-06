using BanditMilitias.Components;
using BanditMilitias.Infrastructure;
using BanditMilitias.Systems.Scheduling;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;

namespace BanditMilitias.Patches
{
    [HarmonyPatch]
    internal static class AiPatrollingBehaviorPatch
    {
        private static readonly string[] TargetTypeNames =
        {
            "TaleWorlds.CampaignSystem.CampaignBehaviors.AiBehaviors.AiPatrollingBehavior",
            "TaleWorlds.CampaignSystem.CampaignBehaviors.AiBehaviors.AiLandBanditPatrollingBehavior",
            "TaleWorlds.CampaignSystem.CampaignBehaviors.AiBehaviors.AiBanditPatrollingBehavior"
        };

        private static MethodBase? ResolveTargetMethod(string typeName)
        {
            try
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null) return null;

                return AccessTools.Method(type, "AiHourlyTick",
                           new[] { typeof(MobileParty), typeof(PartyThinkParams) })
                       ?? AccessTools.Method(type, "AiHourlyTick");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var methods = new List<MethodBase>(TargetTypeNames.Length);
            foreach (var typeName in TargetTypeNames)
            {
                var method = ResolveTargetMethod(typeName);
                if (method != null)
                {
                    methods.Add(method);
                }
            }

            return methods.Distinct();
        }

        [HarmonyPrefix]
        static bool Prefix(MobileParty mobileParty, PartyThinkParams p)
        {
            if (Campaign.Current == null) return true;
            if (mobileParty == null) return true;
            if (mobileParty.PartyComponent is not MilitiaPartyComponent component) return true;
            if (!mobileParty.IsActive) return false;

            try
            {
                // Restocking/sell-prisoners states should not consume tactical decisions.
                if (component.CurrentState == MilitiaPartyComponent.WarlordState.Restocking ||
                    component.CurrentState == MilitiaPartyComponent.WarlordState.SellingPrisoners)
                    return false;

                if (Intelligence.AI.CustomMilitiaAI.IsPartyWounded(mobileParty))
                {
                    component.WakeUp();
                    _ = Intelligence.AI.CustomMilitiaAI.ExecuteCustomLogic(mobileParty);
                    return false;
                }

                if (!ShouldUpdateDecision(mobileParty, component))
                    return false;

                bool urgent = mobileParty.MapEvent != null || component.IsPriorityAIUpdate;

                var scheduler = ModuleManager.Instance.GetModule<AISchedulerSystem>();
                if (scheduler?.IsEnabled == true)
                    scheduler.EnqueueDecision(mobileParty, urgent);
                else
                    Intelligence.AI.CustomMilitiaAI.UpdateTacticalDecision(mobileParty);

                component.IsPriorityAIUpdate = false;

                if (!urgent)
                    component.SleepFor(GetSleepHours(component));
            }
            catch (Exception ex)
            {
                Log.Error($"[AiPatrolPatch] {mobileParty.Name}: {ex.Message}");
            }
            return false;
        }

        [HarmonyFinalizer]
        static Exception? Finalizer(Exception? __exception, MobileParty mobileParty)
        {
            if (__exception != null && mobileParty?.PartyComponent is MilitiaPartyComponent)
            {
                Log.Error($"[AiPatrolPatch] Vanilla crash bastirildi: {mobileParty.Name}: {__exception.Message}");
                return null;
            }
            return __exception;
        }

        private static bool ShouldUpdateDecision(MobileParty party, MilitiaPartyComponent component)
        {
            if (party.MapEvent != null)
            {
                component.WakeUp();
                return true;
            }

            if (component.IsPriorityAIUpdate)
            {
                component.WakeUp();
                return true;
            }

            if (component.NextThinkTime == CampaignTime.Zero)
                return true;

            if (component.GetSleepOverdueHours() >= 6f)
            {
                component.WakeUp();
                return true;
            }

            if (CampaignTime.Now < component.NextThinkTime)
                return false;

            int currentHour = (int)CampaignTime.Now.ToHours;
            int partyHash = Math.Abs(party.StringId.GetHashCode());
            return (partyHash % 3) == (currentHour % 3);
        }

        private static float GetSleepHours(MilitiaPartyComponent component)
        {
            return component.Role == MilitiaPartyComponent.MilitiaRole.Guardian ? 6f : 4f;
        }

        private static class Log
        {
            private static bool TestingMode => Settings.Instance?.TestingMode == true;

            public static void Warning(string msg)
            {
                if (TestingMode)
                    InformationManager.DisplayMessage(new InformationMessage(msg, Colors.Yellow));
                TryFileLog($"WARNING {msg}");
            }

            public static void Error(string msg)
            {
                InformationManager.DisplayMessage(
                    new InformationMessage($"[BanditMilitias] {msg}", Colors.Red));
                TryFileLog($"ERROR {msg}");
                TryAgentCrashGuardLog($"ERROR {msg}");
            }

            private static void TryFileLog(string msg)
            {
                try { BanditMilitias.Infrastructure.FileLogger.Log(msg); }
                catch (Exception ex)
                {
                    TaleWorlds.Library.Debug.Print(
                        $"[AiPatrolPatch] FileLogger mevcut degil: {ex.Message}");
                }
            }

            private static void TryAgentCrashGuardLog(string msg)
            {
                try
                {
                    var type = Type.GetType("AgentCrashGuard.DiagnosticLogger, AgentCrashGuard");
                    if (type != null)
                        _ = (AccessTools.Method(type, "Error")?.Invoke(null,
                            new object[] { "BanditMilitias_Suppressed", msg }));
                }
                catch { }
            }
        }
    }
}
