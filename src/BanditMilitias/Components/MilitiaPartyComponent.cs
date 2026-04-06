using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Localization;
using TaleWorlds.SaveSystem;

namespace BanditMilitias.Components
{
    public class MilitiaPartyComponent : PartyComponent
    {
        [SaveableField(1)]
        private Settlement? _homeSettlement;

        [SaveableField(3)]
        private TextObject? _customName;

        [SaveableField(5)]  // BULGU #4 FIX: Eksik SaveableField eklendi - _currentOrder artık kaydediliyor
        private BanditMilitias.Intelligence.Strategic.StrategicCommand? _currentOrder;

        [SaveableField(6)]
        private CampaignTime _orderTimestamp = CampaignTime.Zero;

        [NonSerialized]
        public bool IsPriorityAIUpdate = false;

        public BanditMilitias.Intelligence.Strategic.BanditBrain Brain => BanditMilitias.Intelligence.Strategic.BanditBrain.Instance;
        public bool IsRaiderRole => Role == MilitiaRole.Raider;

        public BanditMilitias.Intelligence.Strategic.StrategicCommand? CurrentOrder
        {
            get => _currentOrder;
            set => _currentOrder = value;
        }

        public CampaignTime OrderTimestamp
        {
            get => _orderTimestamp;
            set => _orderTimestamp = value;
        }

        public MilitiaPartyComponent(Settlement homeSettlement, TextObject? customName = null)
        {
            if (homeSettlement == null)
                throw new ArgumentNullException(nameof(homeSettlement), "HomeSettlement cannot be null");

            _homeSettlement = homeSettlement;
            _customName = customName;
        }

        public override Hero? PartyOwner => null;

        public override TextObject Name => _customName ?? new TextObject("Haydut Milisleri");

        public override Settlement? HomeSettlement => _homeSettlement;

        public Settlement? GetHomeSettlement() => (_homeSettlement != null && _homeSettlement.IsActive)
            ? _homeSettlement
            : null;

        public Settlement? GetHomeSettlementRaw() => _homeSettlement;

        public void SetHomeSettlement(Settlement newHome)
        {
            if (newHome == null) return;
            _homeSettlement = newHome;
        }

        [SaveableField(2)]
        private Banner? _cachedBanner;

        public enum MilitiaRole
        {
            Raider = 0,
            Guardian = 1,
            Captain = 2,
            VeteranCaptain = 3
        }

        public enum WarlordState
        {
            Patrolling = 0,
            Raiding = 1,
            Restocking = 2,
            SellingPrisoners = 3,
            ReturningToHideout = 4
        }

        [SaveableField(4)]
        private MilitiaRole _role = MilitiaRole.Raider;

        [SaveableField(7)]
        private WarlordState _currentState = WarlordState.Patrolling;

        [SaveableField(8)]
        private int _gold = 0;

        public MilitiaRole Role
        {
            get => _role;
            set => _role = value;
        }

        public WarlordState CurrentState
        {
            get => _currentState;
            set => _currentState = value;
        }

        public int Gold
        {
            get => _gold;
            set => _gold = value;
        }

        [SaveableField(9)]
        private string? _warlordId;

        public string? WarlordId
        {
            get => _warlordId;
            set => _warlordId = value;
        }

        [SaveableField(10)]
        private int _daysAlive = 0;

        public int DaysAlive { get => _daysAlive; set => _daysAlive = value; }

        [SaveableField(11)]
        private int _battlesWon = 0;

        public int BattlesWon { get => _battlesWon; set => _battlesWon = value; }

        [SaveableField(12)]
        private int _battlesLost = 0;

        public int BattlesLost { get => _battlesLost; set => _battlesLost = value; }

        [SaveableField(13)]
        private int _totalKills = 0;

        public int TotalKills { get => _totalKills; set => _totalKills = value; }

        [SaveableField(14)]
        private bool _hasBeenPromotedToWarlord = false;

        public bool HasBeenPromotedToWarlord { get => _hasBeenPromotedToWarlord; set => _hasBeenPromotedToWarlord = value; }

        [SaveableField(15)]
        private System.Collections.Generic.Dictionary<string, float> _inheritedTactics = new();

        public System.Collections.Generic.Dictionary<string, float> InheritedTactics { get => _inheritedTactics; set => _inheritedTactics = value; }

        [SaveableField(16)]
        private CampaignTime _lastBattleTime = CampaignTime.Zero;

        public CampaignTime LastBattleTime { get => _lastBattleTime; set => _lastBattleTime = value; }

        [SaveableField(19)]
        private float _renown = 0f;

        public float Renown { get => _renown; set => _renown = value; }

        [SaveableField(20)]
        private float _equipmentQuality = 1.0f;

        public float EquipmentQuality { get => _equipmentQuality; set => _equipmentQuality = value; }

        // ── Bannerlord tarzı uyku modu ────────────────────────────
        // Parti bir karar verdikten sonra bu zamana kadar AI hesabı atlanır.
        // SaveableField 17: kayıt/yükleme arasında uyku korunur.
        [SaveableField(17)]
        private CampaignTime _nextThinkTime = CampaignTime.Zero;

        [SaveableField(18)]
        private BanditMilitias.Systems.Progression.LegitimacyLevel _bannerPrestigeLevel =
            BanditMilitias.Systems.Progression.LegitimacyLevel.Outlaw;

        public CampaignTime NextThinkTime
        {
            get => _nextThinkTime;
            set => _nextThinkTime = value;
        }

        /// <summary>
        /// Uyku modunu başlatır. <paramref name="hours"/> saatlik bekleme süresi sonuna kadar
        /// AI kararı atlanır. Savaş veya IsPriorityAIUpdate bu uyku modunu iptal eder.
        /// </summary>
        public void SleepFor(float hours)
        {
            float clampedHours = hours;
            if (clampedHours < 0f) clampedHours = 0f;
            if (clampedHours > 24f) clampedHours = 24f;

            if (clampedHours <= 0f)
            {
                WakeUp();
                return;
            }

            _nextThinkTime = CampaignTime.HoursFromNow(clampedHours);
        }

        /// <summary>Uyku modunu anında iptal eder — acil durum override'ı.</summary>
        public void WakeUp() => _nextThinkTime = CampaignTime.Now;

        public float GetSleepRemainingHours()
        {
            if (_nextThinkTime == CampaignTime.Zero || Campaign.Current == null)
                return 0f;

            float remaining = (float)(_nextThinkTime - CampaignTime.Now).ToHours;
            return remaining > 0f ? remaining : 0f;
        }

        public float GetSleepOverdueHours()
        {
            if (_nextThinkTime == CampaignTime.Zero || Campaign.Current == null)
                return 0f;

            float overdue = (float)(CampaignTime.Now - _nextThinkTime).ToHours;
            return overdue > 0f ? overdue : 0f;
        }

        public void InvalidateBannerCache()
        {
            _cachedBanner = null;
        }

        public BanditMilitias.Systems.Progression.LegitimacyLevel BannerPrestigeLevel => _bannerPrestigeLevel;

        public void SetBannerPrestigeLevel(BanditMilitias.Systems.Progression.LegitimacyLevel level)
        {
            if (_bannerPrestigeLevel == level)
                return;

            _bannerPrestigeLevel = level;
            InvalidateBannerCache();
        }

        public override Banner GetDefaultComponentBanner()
        {
            try
            {
                if (_bannerPrestigeLevel >= Systems.Progression.LegitimacyLevel.Warlord)
                {
                    _cachedBanner ??= Banner.CreateOneColoredBannerWithOneIcon(
                        new TaleWorlds.Library.Color(0.8f, 0.1f, 0.1f).ToUnsignedInteger(),
                        new TaleWorlds.Library.Color(1f, 0.84f, 0f).ToUnsignedInteger(),
                        5);
                    return _cachedBanner;
                }

                if (_cachedBanner != null) return _cachedBanner;

                if (_homeSettlement?.Banner != null)
                {
                    return _homeSettlement.Banner;
                }

                _cachedBanner = Banner.CreateRandomBanner();
                return _cachedBanner;
            }
            catch (Exception)
            {
                // _homeSettlement null/disposed durumunda güvenli fallback
                _cachedBanner = Banner.CreateRandomBanner();
                return _cachedBanner;
            }
        }
    }
}
