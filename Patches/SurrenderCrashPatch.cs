using HarmonyLib;
using System;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace BanditMilitias.Patches.SurrenderFix
{

    [HarmonyPatch(typeof(PlayerCaptivityCampaignBehavior), "CheckCaptivityChange")]
    [HarmonyPriority(Priority.VeryHigh)]

    internal static class SurrenderCrashPatch
    {
        /// <summary>
        /// Pre-flight guard: Harmony calls this before attempting to patch.
        /// Returns false if the target method no longer exists in this API version,
        /// causing Harmony to skip this patch gracefully — no exception, no Degraded mode.
        /// </summary>
        [HarmonyPrepare]
        static bool Prepare()
        {
            var targetMethod = HarmonyLib.AccessTools.Method(
                typeof(PlayerCaptivityCampaignBehavior), "CheckCaptivityChange");

            if (targetMethod == null)
            {
                BanditMilitias.Infrastructure.FileLogger.LogWarning(
                    "[SurrenderCrashPatch] Target method 'CheckCaptivityChange' not found in this " +
                    "API version — patch skipped gracefully. Vanilla captivity behavior will be used.");
                return false;
            }

            BanditMilitias.Infrastructure.FileLogger.Log(
                "[SurrenderCrashPatch] Target method verified — patch will be applied.");
            return true;
        }


        private static FieldInfo? _isPrisonerField;
        private static FieldInfo? _partyBelongedToAsPrisonerField;

        private static PropertyInfo? _isPrisonerProperty;
        private static PropertyInfo? _partyBelongedToAsPrisonerProperty;

        private static bool _reflectionValidated = false;
        private static bool _reflectionAvailable = false;
        private static string _apiVersion = "unknown";

        internal static void Initialize()
        {
            if (_reflectionValidated) return;
            _reflectionValidated = true;

            _isPrisonerField = typeof(Hero).GetField("_isPrisoner", BindingFlags.NonPublic | BindingFlags.Instance);
            _partyBelongedToAsPrisonerField = typeof(Hero).GetField("_partyBelongedToAsPrisoner", BindingFlags.NonPublic | BindingFlags.Instance);
            _isPrisonerProperty = typeof(Hero).GetProperty("IsPrisoner", BindingFlags.Public | BindingFlags.Instance);
            _partyBelongedToAsPrisonerProperty = typeof(Hero).GetProperty("PartyBelongedToAsPrisoner", BindingFlags.Public | BindingFlags.Instance);

            bool hasFields = _isPrisonerField != null && _partyBelongedToAsPrisonerField != null;
            bool hasProperties = _isPrisonerProperty != null && _partyBelongedToAsPrisonerProperty != null;

            if (hasProperties)
            {
                _apiVersion = "v1.3.x (Property-based)";

                bool hasIsPrisonerSetter = _isPrisonerProperty!.GetSetMethod(true) != null;
                bool hasPartySetter = _partyBelongedToAsPrisonerProperty!.GetSetMethod(true) != null;

                _reflectionAvailable = hasIsPrisonerSetter && hasPartySetter;

                if (!_reflectionAvailable)
                {

                    if (hasFields)
                    {
                        _apiVersion += " (Field Fallback)";
                        _reflectionAvailable = true;
                        Log.Info($"[SurrenderFix] Properties found but lacked setters - falling back to fields on {_apiVersion}");
                    }
                    else
                    {
                        Log.Warning("[SurrenderFix] Properties found but not writable - using public API fallback");
                    }
                }
                else
                {
                    Log.Info($"[SurrenderFix] API Version: {_apiVersion} - Full reflection available (via private setters)");
                }
            }
            else if (hasFields)
            {
                _apiVersion = "v1.3.x / v1.2.9 or earlier (Field-based)";
                _reflectionAvailable = true;
                Log.Info($"[SurrenderFix] API Version: {_apiVersion} - Full reflection available (via private fields)");
            }
            else
            {
                _apiVersion = "Unknown (Public API only)";
                _reflectionAvailable = false;
                Log.Warning($"[SurrenderFix] API Version: {_apiVersion} - Limited to public API (No writable properties or fields found)");
            }
        }

        private static CampaignTime _lastExecutionTime = CampaignTime.Zero;
        private static CampaignTime _captivityStartTime = CampaignTime.Zero;
        private static bool _captivityTracked;
        private static int _consecutiveFailures;

        private const float MIN_EXECUTION_INTERVAL_HOURS = 0.5f;
        private const float INITIAL_CAPTIVITY_GRACE_HOURS = 6f;
        private const float MAX_CUSTOM_DT_HOURS = 1.0f;
        private const int MAX_CONSECUTIVE_FAILURES = 3;


        private const float MIN_ESCAPE_ELIGIBILITY_HOURS = 1f;

        internal static void ResetState()
        {


            _lastExecutionTime = Campaign.Current != null ? CampaignTime.Now : CampaignTime.Zero;
            _captivityStartTime = CampaignTime.Zero;

            _captivityTracked = false;
            _consecutiveFailures = 0;
            Log.Info("[SurrenderFix] State reset for new game session");
        }

        internal static bool ReflectionAccessAvailable => _reflectionAvailable;

        internal static string GetDiagnostics()
        {
            if (!_reflectionValidated)
            {
                Initialize();
            }

            return $"SurrenderFix: API={_apiVersion}, ReflectionAccess={_reflectionAvailable}, " +
                   $"Tracked={_captivityTracked}, ConsecutiveFailures={_consecutiveFailures}, " +
                   $"Standalone BanditMilitias debugging mode";
        }


        internal static bool HasAgentCrashGuardCaptivityPatch() => false;

        internal static bool HasAgentCrashGuardDestroyPartyPatch() => false;

        internal static string GetCaptivityOverlayStatus()
        {
            if (Hero.MainHero == null || !Hero.MainHero.IsPrisoner)
            {
                return "Prisoner: No";
            }

            float captivityAgeHours = GetCaptivityAgeHours();
            float captivityAgeDays = captivityAgeHours / 24f;
            float lockRemainingHours = Math.Max(0f, MIN_ESCAPE_ELIGIBILITY_HOURS - captivityAgeHours);
            string captorName = Hero.MainHero.PartyBelongedToAsPrisoner?.Name?.ToString()
                                ?? Hero.MainHero.CurrentSettlement?.Name?.ToString()
                                ?? "Unknown";

            if (lockRemainingHours > 0f)
            {
                return $"Prisoner: {captivityAgeDays:F1}d | Lock: {lockRemainingHours:F0}h | Captor: {captorName}";
            }

            return $"Prisoner: {captivityAgeDays:F1}d | Escape: Active | Captor: {captorName}";
        }

        [HarmonyPrefix]
        static bool Prefix(float dt)
        {
            // Ensure reflection is initialized on first call
            if (!_reflectionValidated)
                Initialize();

            if (Hero.MainHero == null)
            {
                Log.Warning("[SurrenderFix] Hero.MainHero is null - aborting");
                return true;
            }

            if (Campaign.Current == null || !Campaign.Current.GameStarted)
            {
                Log.Warning("[SurrenderFix] Campaign not started - aborting");
                return true;
            }

            if (!Hero.MainHero.IsPrisoner)
            {
                _captivityTracked = false;
                _captivityStartTime = CampaignTime.Zero;


                _lastExecutionTime = CampaignTime.Now;
                return true;
            }

            if (!_captivityTracked)
            {
                _captivityTracked = true;
                _captivityStartTime = CampaignTime.Now;
                _lastExecutionTime = CampaignTime.Now;
                Log.Info("[SurrenderFix] Captivity tracking initialized.");
                return true;
            }

            float hoursElapsed = (float)(CampaignTime.Now - _lastExecutionTime).ToHours;
            if (hoursElapsed < MIN_EXECUTION_INTERVAL_HOURS)
                return false;

            float effectiveHours = Math.Min(hoursElapsed, MAX_CUSTOM_DT_HOURS);

            // Yeni klasör fix: exception handling around ProcessCaptivity
            bool runVanilla;
            try
            {
                runVanilla = ProcessCaptivity(effectiveHours);
                _consecutiveFailures = 0;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                Log.Warning(string.Format("[SurrenderFix] ProcessCaptivity exception (failure #{0}): {1}", _consecutiveFailures, ex.Message));

                if (_consecutiveFailures >= MAX_CONSECUTIVE_FAILURES)
                {
                    _consecutiveFailures = 0;
                    EmergencyRelease();
                }

                runVanilla = false;
            }

            _lastExecutionTime = CampaignTime.Now;
            return runVanilla;
        }



        private static bool ProcessCaptivity(float dt)
        {
            var captorParty = Hero.MainHero.PartyBelongedToAsPrisoner;

            if (captorParty == null)
            {
                if (Hero.MainHero.CurrentSettlement != null)
                {
                    Log.Info($"[SurrenderFix] Prisoner in settlement: {Hero.MainHero.CurrentSettlement.Name}");
                    return true;
                }

                float captivityAgeHours = GetCaptivityAgeHours();
                if (captivityAgeHours < INITIAL_CAPTIVITY_GRACE_HOURS)
                {
                    Log.Warning($"[SurrenderFix] Missing captor reference in initial grace window ({captivityAgeHours:F2}h) - waiting");
                    return true;
                }

                Log.Warning("[SurrenderFix] Prisoner with no captor party or settlement - releasing");
                SafeRelease("no captor reference");
                return false;
            }

            var validation = ValidateCaptor(captorParty);

            if (!validation.IsUsable)
            {
                float captivityAgeHours = GetCaptivityAgeHours();
                if (captivityAgeHours < INITIAL_CAPTIVITY_GRACE_HOURS)
                {
                    Log.Warning($"[SurrenderFix] Captor temporarily unusable ({validation.Reason}) in grace window ({captivityAgeHours:F2}h) - waiting");
                    return true;
                }

                Log.Warning($"[SurrenderFix] Captor unusable: {validation.Reason} - releasing");
                SafeRelease(validation.Reason);
                return false;
            }

            if (validation.NeedsCustomHandling)
            {
                Log.Info($"[SurrenderFix] Using custom handling for: {captorParty.Name}");
                return HandleMilitiaCaptivity(dt, validation);
            }


            bool vanillaFullySafe = validation.Leader != null
                && validation.MapFaction != null
                && !validation.IsMilitia;

            if (!vanillaFullySafe)
            {
                Log.Info("[SurrenderFix] Using vanilla handling (skipped - unsafe captor state)");
                return false;
            }

            Log.Info("[SurrenderFix] Using vanilla handling");
            return true;
        }

        private static CaptorInfo ValidateCaptor(PartyBase party)
        {
            var info = new CaptorInfo { OriginalParty = party };

            if (!party.IsMobile)
            {
                info.IsUsable = party.Settlement?.IsActive == true;
                info.Reason = info.IsUsable ? "ok-settlement" : "inactive settlement";
                return info;
            }

            var mobile = party.MobileParty;
            if (mobile == null)
            {
                info.Reason = "MobileParty is null despite IsMobile=true";
                return info;
            }

            if (!mobile.IsActive)
            {
                info.Reason = "MobileParty is not active";
                return info;
            }

            info.Mobile = mobile;
            info.MapFaction = party.MapFaction;
            info.Leader = party.LeaderHero;

            Log.Info($"[SurrenderFix] Validating captor: {party.Name}, " +
                    $"Leader: {info.Leader?.Name?.ToString() ?? "null"}, " +
                    $"Faction: {info.MapFaction?.Name?.ToString() ?? "null"}");

            if (mobile.PartyComponent is BanditMilitias.Components.MilitiaPartyComponent militiaComp)
            {
                info.IsMilitia = true;
                info.HomeSettlement = militiaComp.GetHomeSettlement();
                info.IsComponentValid = info.HomeSettlement?.IsActive == true;
                Log.Info($"[SurrenderFix] Militia detected, Home: {info.HomeSettlement?.Name?.ToString() ?? "null"}");
            }

            bool vanillaSafe = info.Leader != null && info.MapFaction != null;

            info.IsUsable = true;

            info.NeedsCustomHandling = info.IsMilitia || !vanillaSafe;
            info.Reason = "ok";
            return info;
        }
        private static bool HandleMilitiaCaptivity(float dt, CaptorInfo captor)
        {
            float captivityAgeHours = GetCaptivityAgeHours();
            if (captivityAgeHours < MIN_ESCAPE_ELIGIBILITY_HOURS)
            {
                Log.Info($"[SurrenderFix] Escape locked during initial captivity ({captivityAgeHours:F1}/{MIN_ESCAPE_ELIGIBILITY_HOURS:F0}h)");
                return false;
            }

            float escapeChancePerHour = ComputeEscapeChance(captor, captivityAgeHours);
            float tickChance = 1f - TaleWorlds.Library.MathF.Pow(1f - escapeChancePerHour, dt);

            Log.Info($"[SurrenderFix] Escape chance this tick: {tickChance * 100:F2}%");

            if (MBRandom.RandomFloat < tickChance)
            {
                Log.Info("[SurrenderFix] Militia escape succeeded!");
                ExecuteEscape(captor);
            }

            return false;
        }

        private static float GetCaptivityAgeHours()
        {
            if (!_captivityTracked || _captivityStartTime == CampaignTime.Zero)
                return 0f;

            return (float)(CampaignTime.Now - _captivityStartTime).ToHours;
        }

        private static float ComputeEscapeChance(CaptorInfo captor, float captivityAgeHours)
        {


            float chance = 0.025f;

            int roguery = Hero.MainHero.GetSkillValue(DefaultSkills.Roguery);
            chance += roguery * 0.00025f;

            if (captor.IsMilitia) chance *= 1.25f;
            if (!captor.IsComponentValid) chance *= 2.0f;
            if (captor.Leader == null) chance *= 1.25f;
            if (Campaign.Current?.IsNight == true) chance *= 1.10f;

            float ageAfterLock = Math.Max(0f, captivityAgeHours - MIN_ESCAPE_ELIGIBILITY_HOURS);
            if (ageAfterLock < 24f)
            {
                chance *= 0.25f;
            }
            else if (ageAfterLock < 72f)
            {
                chance *= 0.60f;
            }
            else if (ageAfterLock < 168f)
            {
                chance *= 1.00f;
            }
            else
            {
                chance *= 1.80f;
            }

            return Math.Min(chance, 0.20f);
        }

        private static void ExecuteEscape(CaptorInfo captor)
        {
            EndCaptivityAction.ApplyByEscape(Hero.MainHero, null);
            Log.Info("[SurrenderFix] Player escaped from captivity (Teleportation disabled).");

            ApplyEscapeSideEffects(captor);

        }

        private static void ApplyEscapeSideEffects(CaptorInfo captor)
        {
            Hero.MainHero.AddSkillXp(DefaultSkills.Roguery, 50f);

            if (captor.MapFaction != null &&
                !captor.MapFaction.IsMinorFaction &&
                captor.MapFaction.Leader != null)
            {
                ChangeRelationAction.ApplyPlayerRelation(
                    captor.MapFaction.Leader,
                    -5,
                    affectRelatives: false,
                    showQuickNotification: false);
            }
        }

        private static void SafeRelease(string reason)
        {
            Log.Info($"[SurrenderFix] SafeRelease ({reason})");
            
            if (Hero.MainHero?.IsPrisoner == true)
            {
                EndCaptivityAction.ApplyByEscape(Hero.MainHero, null);
                Log.Info("[SurrenderFix] SafeRelease succeeded");
            }
        }

        private static void TryForceRelease()
        {
            Log.Warning($"[SurrenderFix] TryForceRelease - API Version: {_apiVersion}");

            if (Hero.MainHero == null) return;

            bool released = false;

            if (_isPrisonerProperty != null)
            {
                var setter = _isPrisonerProperty.GetSetMethod(true);
                if (setter != null)
                {
                    _ = setter.Invoke(Hero.MainHero, new object[] { false });
                    released = true;
                    Log.Info("[SurrenderFix] Property setter used (v1.3.x / v1.2.10+ path)");
                }
            }

            if (!released && _isPrisonerField != null)
            {
                _isPrisonerField.SetValue(Hero.MainHero, false);
                released = true;
                Log.Info("[SurrenderFix] Field setter used (v1.2.9- path)");
            }

            bool partyCleared = false;
            if (_partyBelongedToAsPrisonerProperty != null)
            {
                var partySetter = _partyBelongedToAsPrisonerProperty.GetSetMethod(true);
                if (partySetter != null)
                {
                    _ = partySetter.Invoke(Hero.MainHero, new object[] { null! });
                    partyCleared = true;
                }
            }

            if (!partyCleared && _partyBelongedToAsPrisonerField != null)
            {
                _partyBelongedToAsPrisonerField.SetValue(Hero.MainHero, null);
                partyCleared = true;
            }

            if (!released)
            {
                Log.Warning("[SurrenderFix] No writable property/field worked - using public API fallback");
                EndCaptivityAction.ApplyByEscape(Hero.MainHero, null);
                Log.Info("[SurrenderFix] Public API fallback applied");
            }
            else
            {
                Log.Info("[SurrenderFix] Reflection release applied successfully");
            }
        }

        private static void EmergencyRelease()
        {
            SafeRelease("emergency — consecutive failures exceeded");
            InformationManager.DisplayMessage(new InformationMessage(
                "[BanditMilitias] Captivity system error — emergency escape executed.",
                Colors.Red));
        }

        private struct CaptorInfo
        {
            public PartyBase OriginalParty;
            public MobileParty? Mobile;
            public IFaction? MapFaction;
            public Hero? Leader;

            public bool IsMilitia;
            public bool IsComponentValid;
            public Settlement? HomeSettlement;

            public bool IsUsable;
            public bool NeedsCustomHandling;
            public string Reason;
        }

        private static class Log
        {
            private static bool TestingMode =>
                BanditMilitias.Settings.Instance?.TestingMode == true;

            public static void Info(string msg)
            {
                if (TestingMode)

                {
                    TryFileLog(msg);
                }
            }

            public static void Warning(string msg)
            {

                TryFileLog($"WARNING: {msg}");
            }

            public static void Error(string msg)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    $"[BanditMilitias] {msg}", Colors.Red));
                TryFileLog($"ERROR: {msg}");
            }

            private static void TryFileLog(string msg)
            {
                BanditMilitias.Infrastructure.FileLogger.Log(msg);
            }
        }
    }

    [HarmonyPatch]
    [HarmonyPriority(Priority.High)]
    internal static class PreventCaptorDestructionPatch
    {
        [HarmonyPrepare]
        static bool Prepare()
        {
            var target = TargetMethod();
            if (target == null)
            {
                BanditMilitias.Infrastructure.FileLogger.LogWarning("[PreventCaptorDestructionPatch] Target method 'DestroyPartyAction.Apply' not found. Patch skipped.");
                return false;
            }
            return true;
        }

        [HarmonyTargetMethod]
        static System.Reflection.MethodBase? TargetMethod()
        {
            var type = typeof(DestroyPartyAction);
            return HarmonyLib.AccessTools.Method(type, "Apply", new Type[] { typeof(PartyBase), typeof(MobileParty) })
                ?? HarmonyLib.AccessTools.Method(type, "Apply", new Type[] { typeof(MobileParty), typeof(MobileParty) })
                ?? HarmonyLib.AccessTools.Method(type, "Apply");
        }

        // Yeni klasör fix: parameter type changed from MobileParty? to PartyBase?
        // DestroyPartyAction.Apply's first parameter is PartyBase in v1.2.x+.
        // Using MobileParty? would cause Harmony injection mismatch → always null.
        [HarmonyPrefix]
        static bool Prefix(PartyBase? destroyedParty)
        {
            var destroyedMobileParty = destroyedParty?.MobileParty;

            if (SurrenderCrashPatch.HasAgentCrashGuardDestroyPartyPatch())
            {
                Log.Info("[SurrenderFix] AgentCrashGuard destroy-party safeguard detected; deferring duplicate release logic");
                return true;
            }

            if (destroyedMobileParty == null) return true;
            if (Hero.MainHero?.IsPrisoner != true) return true;

            var captorParty = Hero.MainHero.PartyBelongedToAsPrisoner;
            if (captorParty?.MobileParty != destroyedMobileParty) return true;

            Log.Info($"[SurrenderFix] Captor party '{destroyedMobileParty.Name}' about to be destroyed - releasing player first");

            EndCaptivityAction.ApplyByEscape(Hero.MainHero, null);
            Log.Info("[SurrenderFix] Player released successfully before captor destruction");

            return true;
        }

        private static class Log
        {
            private static bool TestingMode =>
                BanditMilitias.Settings.Instance?.TestingMode == true;

            public static void Info(string msg)
            {
                BanditMilitias.Infrastructure.FileLogger.Log(msg);
            }

            public static void Error(string msg)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    $"[BanditMilitias] {msg}", Colors.Red));
                BanditMilitias.Infrastructure.FileLogger.Log($"ERROR: {msg}");
            }
        }
    }
}
