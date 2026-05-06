using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.SaveSystem;
using BanditMilitias.Systems.WarlordLegitimacy;

namespace BanditMilitias.Infrastructure
{
    /// <summary>
    /// Persistent state for a militia party. 
    /// Stored in a behavior dictionary instead of a custom PartyComponent to ensure save-game independence.
    /// </summary>
    public struct MilitiaData
    {
        public string HomeSettlementId;
        public string? CustomName;
        public int Role;
        public int CurrentState;
        public int Gold;
        public string? WarlordId;
        public int DaysAlive;
        public int BattlesWon;
        public int BattlesLost;
        public int TotalKills;
        public bool HasBeenPromotedToWarlord;
        public bool IsWatcher;
        public float Renown;
        public float EquipmentQuality;
        public double NextThinkTimeHours;
        public int BannerPrestigeLevel;
        public Dictionary<string, float>? InheritedTactics;

        public static MilitiaData Create(string homeSettlementId, string? customName = null)
        {
            return new MilitiaData
            {
                HomeSettlementId = homeSettlementId,
                CustomName = customName,
                Role = 0, // Raider
                CurrentState = 0, // Patrolling
                Gold = 0,
                InheritedTactics = new Dictionary<string, float>(),
                EquipmentQuality = 1.0f,
                NextThinkTimeHours = 0d
            };
        }
    }
}
