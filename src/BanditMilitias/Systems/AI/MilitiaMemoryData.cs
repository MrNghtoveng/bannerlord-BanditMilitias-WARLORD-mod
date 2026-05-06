using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace BanditMilitias.Systems.AI
{
    /// <summary>
    /// Klasik yerleşke hafızası. Milislerin "burası güvenli" veya "burası tehlikeli" dediği statik noktalar.
    /// </summary>
    public class KnownSettlementMemory
    {
        public string SettlementId { get; set; } = string.Empty;
        public float DangerScore { get; set; } // 0 (Güvenli) - 100 (Ölümcül)
        public CampaignTime LastVisited { get; set; }
        public CampaignTime LastRaid { get; set; }
        public bool HasGarrison { get; set; }
        public int ActiveMilitiaCount { get; set; }
        
        // Adaptif veri: Eğer yerleşke artık yoksa (mod silinmişse) bu true döner.
        public bool IsOrphaned => Settlement.Find(SettlementId) == null;
    }

    /// <summary>
    /// Stratejik hafıza (Pusu noktaları, dar geçitler).
    /// </summary>
    public class StrategicLocationMemory
    {
        public Vec2 Position { get; set; }
        public float StrategicValue { get; set; }
        public string Description { get; set; } = string.Empty;
        public CampaignTime DiscoveryTime { get; set; }
    }

    /// <summary>
    /// Tehdit hafızası (Oyuncu ve Lordlar).
    /// </summary>
    public class ThreatMemory
    {
        public string EntityId { get; set; } = string.Empty;
        public Vec2 LastKnownPosition { get; set; }
        public CampaignTime LastSpottedTime { get; set; }
        public float ReportedStrength { get; set; }
        public bool IsPlayer { get; set; }
    }

    /// <summary>
    /// Hafıza Sistemi tarafından kullanılan ana veri konteyneri.
    /// </summary>
    public class MilitiaMemoryData
    {
        public List<KnownSettlementMemory> Settlements { get; set; } = new();
        public List<StrategicLocationMemory> Hotspots { get; set; } = new();
        public List<ThreatMemory> ActiveThreats { get; set; } = new();
        
        public CampaignTime LastFullScan { get; set; }
        public int Version { get; set; } = 1;
    }
}
