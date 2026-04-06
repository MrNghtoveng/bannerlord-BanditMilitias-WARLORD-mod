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
    /// <summary>
    /// Milis Morali Sistemi
    /// Her milisya partisine bağımsız moral puanı (0–100) atar.
    /// Yüksek moral: agresiflik artar, ganimete daha hızlı gider.
    /// Düşük moral: asker kaçma riski, savaştan kaçınma eğilimi.
    /// Etkileyen faktörler: W/L oranı, warlord tier, mevsim, son savaştan geçen süre.
    /// </summary>
    [AutoRegister]
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
        private const float MORALE_DAILY_DECAY = 0.5f;  // Her gün hafif azalma (ortalamaya doğru)
        private const float MORALE_MEAN = 50f;          // Uzun vadeli denge noktası
        private const float DESERTION_THRESHOLD = 20f;  // Bu altında kaçma riski
        private const float DESERTION_RISK_PER_DAY = 0.04f;

        private bool _initialized = false;

        private MilitiaMoraleSystem() { }

        public override void Initialize()
        {
            if (_initialized) return;
            CampaignEvents.MapEventEnded.AddNonSerializedListener(this, new Action<MapEvent>(OnMapEventEnded));
            _initialized = true;
            DebugLogger.Info("Morale", "MilitiaMoraleSystem başlatıldı.");
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
            if (CompatibilityLayer.IsGameplayActivationDelayed()) return;

            foreach (var party in CompatibilityLayer.GetSafeMobileParties())
            {
                if (party.PartyComponent is not MilitiaPartyComponent comp) continue;

                string pid = party.StringId;
                float morale = GetMorale(pid);

                // Mevsimsel etki
                float seasonalFactor = GetSeasonalMoraleFactor();

                // Warlord tier etkisi
                float tierFactor = GetWarlordTierFactor(comp.WarlordId);

                // Ortalamaya doğru yavaş çekim
                float target = BASE_MORALE + seasonalFactor + tierFactor;
                float delta = (target - morale) * 0.05f - MORALE_DAILY_DECAY;
                morale = MathF.Clamp(morale + delta, 0f, 100f);
                SetMorale(pid, morale);

                // Savaşa hazırlık güncelleme — agresiflik çarpanı
                ApplyMoraleToParty(party, comp, morale);

                // Kritik moral: asker kaçma riski
                if (morale < DESERTION_THRESHOLD)
                    ProcessDesertion(party, morale);
            }

            CleanupStaleRecords();
        }

        private void OnMapEventEnded(MapEvent ev)
        {
            if (ev == null) return;

            // Her iki taraftaki milisyaları güncelle
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
                    // Kazandı — moral artışı
                    int streak = _winStreak.TryGetValue(pid, out var s) ? s + 1 : 1;
                    _winStreak[pid] = streak;
                    float bonus = MORALE_WIN_GAIN * (1f + streak * 0.15f); // Seri bonusu
                    SetMorale(pid, MathF.Clamp(current + bonus, 0f, 100f));

                    if (Settings.Instance?.TestingMode == true)
                        DebugLogger.Info("Morale", $"{party.Party.MobileParty.Name}: +{bonus:F1} moral (seri={streak})");
                }
                else
                {
                    // Kaybetti — seri bozulur, moral düşer
                    _winStreak[pid] = 0;
                    SetMorale(pid, MathF.Clamp(current - MORALE_LOSS_PENALTY, 0f, 100f));

                    if (Settings.Instance?.TestingMode == true)
                        DebugLogger.Info("Morale", $"{party.Party.MobileParty.Name}: -{MORALE_LOSS_PENALTY:F1} moral");
                }
            }
        }

        private void ApplyMoraleToParty(MobileParty party, MilitiaPartyComponent comp, float morale)
        {
            // Morale → Aggressiveness çarpanı (0.5 – 1.5 arası)
            // Bu değer BanditBrain'in hedef seçimini etkileyebilir
            float aggressivenessFactor = 0.5f + (morale / 100f);

            // MilitiaPartyComponent'taki Aggressiveness alanı yok ama
            // custom field olarak WarlordId kullanıyoruz; yerine RoleData'ya not düşüyoruz
            // Gerçek etki: ScoringFunctions'da GetMorale() çağrılacak
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
                            $"[Moral] {party.Name}: {actual} asker moral bozukluğundan kaçtı!",
                            Colors.Yellow));

                        DebugLogger.Info("Morale",
                            $"Kaçma: {party.Name} -{actual} asker (moral={morale:F1})");
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("Morale", $"Kaçma işlemi hatası: {ex.Message}");
            }
        }

        private static float GetSeasonalMoraleFactor()
        {
            try
            {
                var season = Seasonal.SeasonalEffectsSystem.Instance.CurrentSeason;
                return season switch
                {
                    MilitiaSeason.Summer => +8f,   // Yaz: yüksek moral
                    MilitiaSeason.Autumn => +5f,   // Sonbahar: baskın sezonu, heyecanlı
                    MilitiaSeason.Winter => -15f,  // Kış: derin moral düşüşü
                    MilitiaSeason.Spring => +2f,   // İlkbahar: toparlanma
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
                // Her tier +4 moral
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
                >= 80 => "Coşkulu",
                >= 60 => "Kararlı",
                >= 40 => "Normal",
                >= 25 => "Yorgun",
                _ => "Çökmüş"
            };
        }

        private void CleanupStaleRecords()
        {
            // Artık var olmayan partilerin kayıtlarını temizle
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
            if (_partyMorale.Count == 0) return "MilitiaMorale: Kayıtlı parti yok.";

            float avg = _partyMorale.Values.Average();
            int high = _partyMorale.Values.Count(m => m >= 70);
            int low = _partyMorale.Values.Count(m => m < 30);

            return $"MilitiaMorale:\n" +
                   $"  Takip edilen parti: {_partyMorale.Count}\n" +
                   $"  Ortalama moral: {avg:F1}\n" +
                   $"  Yüksek moral (70+): {high}\n" +
                   $"  Düşük moral (<30): {low}";
        }
    }
}
