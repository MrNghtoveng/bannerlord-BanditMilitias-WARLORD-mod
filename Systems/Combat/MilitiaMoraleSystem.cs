using BanditMilitias.Components;
using BanditMilitias.Core.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.Seasonal;
using BanditMilitias.Systems.Progression;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace BanditMilitias.Systems.Combat
{


    [BanditMilitias.Core.Components.ModuleDependency(
        typeof(BanditMilitias.Intelligence.Strategic.WarlordSystem),
        typeof(BanditMilitias.Systems.Progression.WarlordCareerSystem),
        typeof(BanditMilitias.Systems.Seasonal.SeasonalEffectsSystem))]
    [BanditMilitias.Core.Components.AutoRegister(Priority = 45, IsCritical = false)]
    public class MilitiaMoraleSystem : MilitiaModuleBase
    {
        public override string ModuleName => "MilitiaMoraleSystem";
        public override bool IsEnabled => Settings.Instance?.EnableWarlords ?? true;
        public override int Priority => 45;

        private static readonly Lazy<MilitiaMoraleSystem> _instance =
            new Lazy<MilitiaMoraleSystem>(() => new MilitiaMoraleSystem());
        public static MilitiaMoraleSystem Instance => _instance.Value;

        private Dictionary<string, float> _partyMorale = new();
        private Dictionary<string, bool> _lastBattleResult = new();
        private Dictionary<string, int> _winStreak = new();

        private const float BASE_MORALE = 55f;
        private const float MORALE_WIN_GAIN = 8f;
        private const float MORALE_LOSS_PENALTY = 12f;
        private const float MORALE_DAILY_DECAY = 0.5f;

        private const float MORALE_MEAN = 50f;

        private const float DESERTION_THRESHOLD = 20f;

        private const float DESERTION_RISK_PER_DAY = 0.04f;

        private bool _initialized = false;

        private MilitiaMoraleSystem() { }

        public override void Initialize()
        {
            if (_initialized) return;
            CampaignEvents.MapEventEnded.AddNonSerializedListener(this, new Action<MapEvent>(OnMapEventEnded));
            _initialized = true;
            DebugLogger.Info("Morale", "MilitiaMoraleSystem initialized.");
        }

        public override void Cleanup()
        {
            if (!_initialized) return;
            CampaignEvents.MapEventEnded.RemoveNonSerializedListener(this, new Action<MapEvent>(OnMapEventEnded));
            _initialized = false;
        }

        public override void SyncData(IDataStore dataStore)
        {
            dataStore.SyncData("MilitiaPartyMorale_v1", ref _partyMorale);
            dataStore.SyncData("MilitiaLastBattle_v1", ref _lastBattleResult);
            dataStore.SyncData("MilitiaWinStreak_v1", ref _winStreak);
        }

        public override void OnDailyTick()
        {
            if (!IsEnabled || !_initialized) return;
            if (ModActivationManager.IsGameplayActivationDelayed()) return;

            foreach (var party in CompatibilityLayer.GetSafeMobileParties())
            {
                if (party.PartyComponent is not MilitiaPartyComponent comp) continue;

                string pid = party.StringId;
                float morale = GetMorale(pid);


                float seasonalFactor = GetSeasonalMoraleFactor();


                float tierFactor = GetWarlordTierFactor(comp.WarlordId);


                float target = BASE_MORALE + seasonalFactor + tierFactor;
                float delta = (target - morale) * 0.05f - MORALE_DAILY_DECAY;
                morale = MathF.Clamp(morale + delta, 0f, 100f);
                SetMorale(pid, morale);


                ApplyMoraleToParty(party, comp, morale);


                if (morale < DESERTION_THRESHOLD)
                    ProcessDesertion(party, morale);
            }

            CleanupStaleRecords();
        }

        private void OnMapEventEnded(MapEvent ev)
        {
            if (ev == null) return;


            ProcessMapEventSide(ev.AttackerSide, ev.WinningSide == BattleSideEnum.Attacker);
            ProcessMapEventSide(ev.DefenderSide, ev.WinningSide == BattleSideEnum.Defender);
        }

        private void ProcessMapEventSide(MapEventSide side, bool won)
        {


            foreach (var party in side.Parties)
            {
                if (party.Party?.MobileParty?.PartyComponent is not MilitiaPartyComponent) continue;
                string pid = party.Party.MobileParty.StringId;

                float current = GetMorale(pid);
                _lastBattleResult[pid] = won;

                if (won)
                {


                    int streak = _winStreak.TryGetValue(pid, out var s) ? s + 1 : 1;
                    _winStreak[pid] = streak;
                    float bonus = MORALE_WIN_GAIN * (1f + streak * 0.15f);

                    SetMorale(pid, MathF.Clamp(current + bonus, 0f, 100f));

                    if (Settings.Instance?.TestingMode == true)
                        DebugLogger.Info("Morale", $"{party.Party.MobileParty.Name}: +{bonus:F1} morale (streak={streak})");
                }
                else
                {


                    _winStreak[pid] = 0;
                    SetMorale(pid, MathF.Clamp(current - MORALE_LOSS_PENALTY, 0f, 100f));

                    if (Settings.Instance?.TestingMode == true)
                        DebugLogger.Info("Morale", $"{party.Party.MobileParty.Name}: -{MORALE_LOSS_PENALTY:F1} morale");
                }
            }
        }

        private void ApplyMoraleToParty(MobileParty party, MilitiaPartyComponent comp, float morale)
        {


            float aggressivenessFactor = 0.5f + (morale / 100f);


        }

        private void ProcessDesertion(MobileParty party, float morale)
        {
            if (MBRandom.RandomFloat > DESERTION_RISK_PER_DAY) return;

            int total = party.MemberRoster.TotalManCount;
            if (total < 10) return;

            int deserters = Math.Max(1, (int)(total * 0.03f * (1f - morale / DESERTION_THRESHOLD)));

            try
            {
                var leastLoyalTroop = party.MemberRoster.GetTroopRoster()
                    .Where(e => !e.Character.IsHero && e.Number > 0)
                    .OrderBy(e => e.Character.Tier)
                    .FirstOrDefault();

                if (leastLoyalTroop.Character != null)
                {
                    int actual = Math.Min(deserters, leastLoyalTroop.Number);
                    party.MemberRoster.AddToCounts(leastLoyalTroop.Character, -actual);

                    if (actual > 0)
                    {
                        InformationManager.DisplayMessage(new InformationMessage(
                            $"[Morale] {party.Name}: {actual} troops deserted due to low morale!",
                            Colors.Yellow));

                        DebugLogger.Info("Morale",
                            $"Desertion: {party.Name} -{actual} troops (morale={morale:F1})");
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("Morale", $"Desertion process error: {ex.Message}");
            }
        }

        private static float GetSeasonalMoraleFactor()
        {
            try
            {
                var season = Seasonal.SeasonalEffectsSystem.Instance.CurrentSeason;
                return season switch
                {
                    MilitiaSeason.Summer => +8f,

                    MilitiaSeason.Autumn => +5f,

                    MilitiaSeason.Winter => -15f,

                    MilitiaSeason.Spring => +2f,

                    _ => 0f
                };
            }
            catch { return 0f; }
        }

        private static float GetWarlordTierFactor(string? warlordId)
        {
            if (warlordId == null) return 0f;
            try
            {
                var warlord = WarlordSystem.Instance.GetWarlord(warlordId);
                if (warlord == null) return 0f;


                int tier = (int)WarlordCareerSystem.Instance.GetTier(warlord.StringId);
                return tier * 4f;
            }
            catch { return 0f; }
        }

        public float GetMorale(string partyId)
        {
            return _partyMorale.TryGetValue(partyId, out var m) ? m : BASE_MORALE;
        }

        private void SetMorale(string partyId, float value)
        {
            _partyMorale[partyId] = MathF.Clamp(value, 0f, 100f);
        }

        public string GetMoraleDescription(string partyId)
        {
            float m = GetMorale(partyId);
            return m switch
            {
                >= 80 => "Enthusiastic",
                >= 60 => "Determined",
                >= 40 => "Normal",
                >= 25 => "Tired",
                _ => "Broken"
            };
        }

        private void CleanupStaleRecords()
        {


            var activeIds = new HashSet<string>(
                CompatibilityLayer.GetSafeMobileParties()
                    .Where(p => p.PartyComponent is MilitiaPartyComponent)
                    .Select(p => p.StringId));

            var toRemove = _partyMorale.Keys.Where(k => !activeIds.Contains(k)).ToList();
            foreach (var key in toRemove)
            {
                _partyMorale.Remove(key);
                _lastBattleResult.Remove(key);
                _winStreak.Remove(key);
            }
        }

        public override string GetDiagnostics()
        {
            if (_partyMorale.Count == 0) return "MilitiaMorale: No registered parties.";

            float avg = _partyMorale.Values.Average();
            int high = _partyMorale.Values.Count(m => m >= 70);
            int low = _partyMorale.Values.Count(m => m < 30);

            return $"MilitiaMorale:\n" +
                   $"  Tracked parties: {_partyMorale.Count}\n" +
                   $"  Average morale: {avg:F1}\n" +
                   $"  High morale (70+): {high}\n" +
                   $"  Low morale (<30): {low}";
        }
    }
}


