using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Localization;
using BanditMilitias.Systems.WarlordLegitimacy;
using BanditMilitias.Infrastructure;

namespace BanditMilitias.Components
{
    /// <summary>
    /// Runtime-only component for militia parties. 
    /// This class is no longer saved directly to prevent save-game corruption when the mod is removed.
    /// Persistent data is managed by MilitiaBehavior via MilitiaData.
    /// </summary>
    public class MilitiaPartyComponent : PartyComponent
    {
        private Settlement? _homeSettlement;
        private TextObject? _customName;
        private BanditMilitias.Intelligence.Strategic.StrategicCommand? _currentOrder;
        private CampaignTime _orderTimestamp = CampaignTime.Zero;
        private Banner? _cachedBanner;
        private MilitiaRole _role = MilitiaRole.Raider;
        private WarlordState _currentState = WarlordState.Patrolling;
        private int _gold = 0;
        private string? _warlordId;
        private int _daysAlive = 0;
        private int _battlesWon = 0;
        private int _battlesLost = 0;
        private int _totalKills = 0;
        private bool _hasBeenPromotedToWarlord = false;
        private bool _isWatcher = false;
        private System.Collections.Generic.Dictionary<string, float> _inheritedTactics = new();
        private CampaignTime _lastBattleTime = CampaignTime.Zero;
        private float _renown = 0f;
        private float _equipmentQuality = 1.0f;
        private CampaignTime _nextThinkTime = CampaignTime.Zero;
        private LegitimacyLevel _bannerPrestigeLevel = LegitimacyLevel.Outlaw;
        private Intelligence.Strategic.Warlord? _assignedWarlord;

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
        public override TextObject Name => _customName ?? new TextObject("Bandit Militias");
        public override Settlement? HomeSettlement => _homeSettlement;

        public Settlement? GetHomeSettlement() => (_homeSettlement != null && _homeSettlement.IsActive) ? _homeSettlement : null;
        public Settlement? GetHomeSettlementRaw() => _homeSettlement;

        public void SetHomeSettlement(Settlement newHome)
        {
            if (newHome == null) return;
            _homeSettlement = newHome;
        }

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

        public MilitiaRole Role { get => _role; set => _role = value; }
        public WarlordState CurrentState { get => _currentState; set => _currentState = value; }
        public int Gold { get => _gold; set => _gold = value; }
        public string? WarlordId { get => _warlordId; set => _warlordId = value; }
        public int DaysAlive { get => _daysAlive; set => _daysAlive = value; }
        public int BattlesWon { get => _battlesWon; set => _battlesWon = value; }
        public int BattlesLost { get => _battlesLost; set => _battlesLost = value; }
        public int TotalKills { get => _totalKills; set => _totalKills = value; }
        public bool HasBeenPromotedToWarlord { get => _hasBeenPromotedToWarlord; set => _hasBeenPromotedToWarlord = value; }
        public bool IsWatcher { get => _isWatcher; set => _isWatcher = value; }
        public System.Collections.Generic.Dictionary<string, float> InheritedTactics { get => _inheritedTactics ??= new(); set => _inheritedTactics = value; }
        public CampaignTime LastBattleTime { get => _lastBattleTime; set => _lastBattleTime = value; }
        public float Renown { get => _renown; set => _renown = value; }
        public float EquipmentQuality { get => _equipmentQuality; set => _equipmentQuality = value; }
        public CampaignTime NextThinkTime { get => _nextThinkTime; set => _nextThinkTime = value; }
        public LegitimacyLevel BannerPrestigeLevel => _bannerPrestigeLevel;
        public Intelligence.Strategic.Warlord? AssignedWarlord { get => _assignedWarlord; set => _assignedWarlord = value; }

        public void SleepFor(float hours)
        {
            float clampedHours = hours;
            if (clampedHours > 24f) clampedHours = 24f;
            if (clampedHours <= 0f) { WakeUp(); return; }
            _nextThinkTime = CampaignTime.HoursFromNow(clampedHours);
        }

        public void WakeUp() => _nextThinkTime = CampaignTime.Now;

        public float GetSleepRemainingHours()
        {
            if (_nextThinkTime == CampaignTime.Zero || Campaign.Current == null) return 0f;
            float remaining = (float)(_nextThinkTime - CampaignTime.Now).ToHours;
            return Math.Max(0f, remaining);
        }

        public float GetSleepOverdueHours()
        {
            if (_nextThinkTime == CampaignTime.Zero || Campaign.Current == null) return 0f;
            float overdue = (float)(CampaignTime.Now - _nextThinkTime).ToHours;
            return Math.Max(0f, overdue);
        }

        public void InvalidateBannerCache() => _cachedBanner = null;

        public void SetBannerPrestigeLevel(LegitimacyLevel level)
        {
            if (_bannerPrestigeLevel == level) return;
            _bannerPrestigeLevel = level;
            InvalidateBannerCache();
        }

        public override Banner GetDefaultComponentBanner()
        {
            try
            {
                if (Settings.Instance?.EnableBanners == false)
                    return _homeSettlement?.Banner ?? Banner.CreateRandomBanner();

                if (_bannerPrestigeLevel >= LegitimacyLevel.Warlord)
                {
                    _cachedBanner ??= Banner.CreateOneColoredBannerWithOneIcon(
                        new TaleWorlds.Library.Color(0.8f, 0.1f, 0.1f).ToUnsignedInteger(),
                        new TaleWorlds.Library.Color(1f, 0.84f, 0f).ToUnsignedInteger(),
                        5);
                    return _cachedBanner;
                }

                if (_cachedBanner != null) return _cachedBanner;
                if (_homeSettlement?.Banner != null) return _homeSettlement.Banner;

                _cachedBanner = Banner.CreateRandomBanner();
                return _cachedBanner;
            }
            catch (Exception)
            {
                return _cachedBanner = Banner.CreateRandomBanner();
            }
        }

        /// <summary>
        /// Populates this runtime component from persistent data.
        /// </summary>
        public void LoadFromData(MilitiaData data)
        {
            _role = (MilitiaRole)data.Role;
            _currentState = (WarlordState)data.CurrentState;
            _gold = data.Gold;
            _warlordId = data.WarlordId;
            _daysAlive = data.DaysAlive;
            _battlesWon = data.BattlesWon;
            _battlesLost = data.BattlesLost;
            _totalKills = data.TotalKills;
            _hasBeenPromotedToWarlord = data.HasBeenPromotedToWarlord;
            _isWatcher = data.IsWatcher;
            _renown = data.Renown;
            _equipmentQuality = data.EquipmentQuality;
            _nextThinkTime = CampaignTime.Hours((float)data.NextThinkTimeHours);
            _bannerPrestigeLevel = (LegitimacyLevel)data.BannerPrestigeLevel;
            _inheritedTactics = data.InheritedTactics ?? new();
            
            if (!string.IsNullOrEmpty(data.CustomName))
                _customName = new TextObject(data.CustomName);
        }

        /// <summary>
        /// Captures runtime state into persistent data.
        /// </summary>
        public MilitiaData SaveToData()
        {
            return new MilitiaData
            {
                HomeSettlementId = _homeSettlement?.StringId ?? "",
                CustomName = _customName?.ToString(),
                Role = (int)_role,
                CurrentState = (int)_currentState,
                Gold = _gold,
                WarlordId = _warlordId,
                DaysAlive = _daysAlive,
                BattlesWon = _battlesWon,
                BattlesLost = _battlesLost,
                TotalKills = _totalKills,
                HasBeenPromotedToWarlord = _hasBeenPromotedToWarlord,
                IsWatcher = _isWatcher,
                Renown = _renown,
                EquipmentQuality = _equipmentQuality,
                NextThinkTimeHours = _nextThinkTime.ToHours,
                BannerPrestigeLevel = (int)_bannerPrestigeLevel,
                InheritedTactics = _inheritedTactics
            };
        }
    }
}

