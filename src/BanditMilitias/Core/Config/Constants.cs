using BanditMilitias.Debug;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;

namespace BanditMilitias.Core.Config
{
    // ── AIConstants ──────────────────────────────────────────


    public static class AIConstants
    {

        public const int WARLORD_INCOME_MIN = 100;
        public const int WARLORD_INCOME_MAX = 300;
        public const int WARLORD_HIDEOUT_BONUS = 200;

        public const int WARLORD_WAGE_BASE = 150;
        public const float WARLORD_WAGE_BOUNTY_FACTOR = 50f;

        public const int WARLORD_BANKRUPTCY_PENALTY = 500;

        public const int WARLORD_WEALTH_THRESHOLD = 5000;
        public const int WARLORD_INVESTMENT_COST = 1000;
        public const float WARLORD_REPUTATION_BOOST = 250f;

        public const int MAX_HEATMAP_SIZE = 1000;
        public const int HEATMAP_CLEANUP_BATCH = 200;

        public const float MAX_REPUTATION_THREAT = 2.0f;
        public const float THREAT_LEVEL_FROM_KILLS = 100f;

        public const float MAX_STRENGTH_THREAT = 1.0f;
        public const float THREAT_LEVEL_FROM_STRENGTH = 500f;

        public const int REPUTATION_HOTSPOT_VISITS = 20;
        public const int REPUTATION_PURSUE_KILLS = 10;
        public const float REPUTATION_PURSUE_HOURS = 72f;

        public const float PLAYER_AVOID_MULTIPLIER = 2.5f;

        public const int MAX_QUARANTINE_SIZE = 50;
        public const int MAX_REPAIR_ATTEMPTS = 5;
        public const float GRACE_PERIOD_HOURS = 12f;
    }
    // ── Constants ──────────────────────────────────────────

    public static class Constants
    {
        // Spawning
        public const float SPAWN_CHANCE_MIN = 0.55f;
        public const float SPAWN_CHANCE_MAX = 0.75f;
        public const int SPAWN_TROOP_MIN = 12;
        public const int SPAWN_TROOP_MAX = 24;
        public const int SPAWN_TROOP_HARD_MIN = 8;
        public const int SPAWN_TROOP_HARD_MAX = 50;
        public const float COOLDOWN_HOURS = 24f;
        public const float FAILURE_COOLDOWN_H = 6f;
        public const int ACTIVATION_DELAY_DAYS = 2;

        // Warlord economy
        public const int WARLORD_INCOME_BASE = 150;
        public const float WARLORD_INCOME_PER_MILITIA = 170f;
        public const float WARLORD_WEALTH_TAX = 0.005f;
        public const int WARLORD_POCKET_MONEY = 7_000;
        public const int WARLORD_BANKRUPTCY_THRESHOLD = 0;

        // Career progression tiers
        public const int TIER_ESKIYA = 0;
        public const int TIER_REBEL = 1;
        public const int TIER_WARLORD = 2;
        public const int TIER_MINOR_LORD = 3;
        public const int TIER_RECOGNIZED = 4;
        public const int TIER_FATIH = 5;

        // Bounty
        public const int BOUNTY_THRESHOLD_NOTICE = 1_500;
        public const int BOUNTY_THRESHOLD_HUNT = 4_500;
        public const int BOUNTY_THRESHOLD_ARMY = 12_000;
        public const int BOUNTY_DAILY_DECAY = 50;

        // Fear / Territory
        public const float FEAR_RAID_GAIN = 15f;
        public const float FEAR_DECAY_DAILY = 0.5f;
        public const float FEAR_MILITIA_KILLED_GAIN = -8f;
        public const float TERRITORY_RADIUS = 1_200f;

        // AI
        public const float AI_THREAT_RATIO_ENGAGE = 0.7f;
        public const float AI_THREAT_RATIO_FLEE = 1.6f;
        public const float AI_Q_LEARNING_RATE = 0.15f;
        public const float AI_Q_DISCOUNT = 0.9f;

        // Propaganda
        public const int PROPAGANDA_COST_DAILY = 150;
        public const float PROPAGANDA_LOYALTY_DELTA = 0.5f;

        // Save
        public const int SAVE_VERSION = 4;
        public const string SAVE_KEY_COOLDOWNS = "_bm_cooldowns";
        public const string SAVE_KEY_FAIL_CD = "_bm_fail_cooldowns";
        public const string SAVE_KEY_VERSION = "_bm_version";
    }
    // ── Globals ──────────────────────────────────────────

    public static class Globals
    {
        public const int MAX_BANDIT_TIER = 30;

        private static List<CharacterObject> _basicInfantry = new List<CharacterObject>();
        private static List<CharacterObject> _banditBosses = new List<CharacterObject>();
        private static Dictionary<string, List<CharacterObject>> _regionalBandits = new Dictionary<string, List<CharacterObject>>();

        public static IReadOnlyList<CharacterObject> BasicInfantry
        {
            get
            {
                EnsureInitialized();
                return _basicInfantry;
            }
            private set
            {
                if (value == null)
                {
                    _basicInfantry = new List<CharacterObject>();
                    return;
                }
                _basicInfantry = value as List<CharacterObject> ?? new List<CharacterObject>(value);
            }
        }

        public static IReadOnlyList<CharacterObject> BanditBosses
        {
            get
            {
                EnsureInitialized();
                return _banditBosses;
            }
        }

        public static IReadOnlyDictionary<string, List<CharacterObject>> RegionalBandits
        {
            get
            {
                EnsureInitialized();
                return _regionalBandits;
            }
        }

        private static readonly object _initLock = new object();
        private static bool _isInitialized = false;
        private static int _initAttempts = 0;
        private const int MAX_INIT_ATTEMPTS = 5;

        public static bool IsInitialized => _isInitialized && _basicInfantry.Count > 0;
        public static int InitAttempts => _initAttempts;

        public static void Reset()
        {
            lock (_initLock)
            {
                _isInitialized = false;
                _initAttempts = 0;
                _basicInfantry = new List<CharacterObject>();
                _banditBosses = new List<CharacterObject>();
                _regionalBandits = new Dictionary<string, List<CharacterObject>>();
            }
        }

        private static void EnsureInitialized()
        {
            if (_isInitialized && _basicInfantry.Count > 0) return;

            lock (_initLock)
            {
                if (_isInitialized && _basicInfantry.Count > 0) return;

                InitializeInternal();
            }
        }

        public static void Initialize(bool force = false)
        {
            lock (_initLock)
            {
                if (!force && _isInitialized && _basicInfantry.Count > 0)
                {
                    return;
                }

                InitializeInternal();
            }
        }

        private static void InitializeInternal()
        {
            _initAttempts++;

            // KRİTİK FIX: CharacterObject.All null/empty kontrolü
            if (CharacterObject.All == null || !CharacterObject.All.Any())
            {
                if (Settings.Instance?.TestingMode == true)
                {
                    DebugLogger.Warning("Globals", $"Init attempt {_initAttempts}/{MAX_INIT_ATTEMPTS}: CharacterObject.All henüz hazır değil!");
                }

                // Max deneme aşıldıysa fallback moduna geç
                if (_initAttempts >= MAX_INIT_ATTEMPTS)
                {
                    TryFallbackInitialization();
                }
                return;
            }

            int banditCount = 0;
            var infantry = new List<CharacterObject>();
            var bosses = new List<CharacterObject>();
            var regional = new Dictionary<string, List<CharacterObject>>();

            var allBanditCandidates = new List<CharacterObject>();
            foreach (var character in CharacterObject.All)
            {
                if (character != null && character.Occupation == Occupation.Bandit && !character.IsHero && !character.IsTemplate)
                {
                    // Formasyon sınıfı geçerli mi kontrol et
                    if ((int)character.DefaultFormationClass >= 0 && (int)character.DefaultFormationClass <= 7)
                    {
                        allBanditCandidates.Add(character);
                    }
                }
            }

            foreach (var character in allBanditCandidates)
            {
                banditCount++;
                infantry.Add(character);

                if (character.StringId.Contains("boss") || character.Tier >= 4)
                {
                    bosses.Add(character);
                }

                string cultureKey = character.Culture?.StringId ?? "unknown";
                if (!regional.TryGetValue(cultureKey, out var cultureList))
                {
                    cultureList = new List<CharacterObject>();
                    regional[cultureKey] = cultureList;
                }
                cultureList.Add(character);
            }

            // Fallback: Looter ara
            if (infantry.Count == 0)
            {
                foreach (var character in CharacterObject.All)
                {
                    if (character != null && character.StringId.IndexOf("looter", System.StringComparison.OrdinalIgnoreCase) >= 0 && character.Occupation == Occupation.Bandit && !character.IsHero)
                    {
                        infantry.Add(character);
                    }
                }

                if (infantry.Count == 0)
                {
                    foreach (var character in CharacterObject.All)
                    {
                        if (character != null && character.Occupation == Occupation.Bandit && !character.IsHero)
                        {
                            infantry.Add(character);
                        }
                    }
                }

                if (Settings.Instance?.TestingMode == true)
                {
                    string fallbackMsg = infantry.Count > 0
                        ? "UYARI: Hiç haydut bulunamadı, son çare olarak Çapulcular eklendi."
                        : "KRİTİK: Hiç haydut askeri bulunamadı! Mod spawn yapamayacak!";

                    TaleWorlds.Library.InformationManager.DisplayMessage(new TaleWorlds.Library.InformationMessage(
                        fallbackMsg,
                        TaleWorlds.Library.Colors.Red));
                }
            }

            BasicInfantry = infantry;
            _banditBosses = bosses;
            _regionalBandits = regional;

            _isInitialized = infantry.Count > 0;

            if (_isInitialized && Settings.Instance?.TestingMode == true)
            {
                DebugLogger.Info("Globals", $"Initialized successfully: {infantry.Count} infantry, {bosses.Count} bosses");
            }
        }

        private static void TryFallbackInitialization()
        {
            // Son çare: Hardcoded looter ID'leri dene
            string[] fallbackIds = new[] { "looter", "bandit", "mountain_bandit", "forest_bandit", "sea_raider" };
            var infantry = new List<CharacterObject>();

            foreach (var id in fallbackIds)
            {
                try
                {
                    var character = CharacterObject.All?.FirstOrDefault(c =>
                        c != null && c.StringId.Contains(id) && c.Occupation == Occupation.Bandit);
                    if (character != null)
                    {
                        infantry.Add(character);
                    }
                }
                catch { }
            }

            if (infantry.Count > 0)
            {
                BasicInfantry = infantry;
                _isInitialized = true;
                DebugLogger.Warning("Globals", $"Fallback initialization succeeded with {infantry.Count} troops");
            }
        }
    }


    // ── AIConstants ─────────────────────────────────


    // ── Globals ─────────────────────────────────

}
