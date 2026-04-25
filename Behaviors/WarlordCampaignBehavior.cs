using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using System.Collections.Generic;
using System.Linq;
using BanditMilitias.Intelligence.Strategic;

namespace BanditMilitias.Behaviors
{
    public class WarlordCampaignBehavior : CampaignBehaviorBase
    {
        private Queue<MobileParty> _partiesToCalculate = new Queue<MobileParty>();

        public override void RegisterEvents()
        {
            // Olay sızıntısını önlemek için önce mevcut abonelikleri temizle
            try
            {
                Infrastructure.MbEventExtensions.RemoveListenerSafe(CampaignEvents.HourlyTickEvent, this, OnHourlyTick);
                Infrastructure.MbEventExtensions.RemoveListenerSafe(CampaignEvents.DailyTickEvent, this, OnDailyTick);
            }
            catch { }

            CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, OnHourlyTick);
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, OnDailyTick);
        }

        // BUG-03 FIX: AI taktiksel hafızasını (warlord aktif parti listesi, kuyruk büyüklüğü)
        // kayıt dosyasına yaz.
        private List<string> _savedWarlordIds = new List<string>();
        private int _savedQueueSize = 0;

        public override void SyncData(IDataStore dataStore)
        {
            try
            {
                if (dataStore.IsSaving)
                {
                    // Aktif warlordları StringId listesi olarak sakla
                    _savedWarlordIds = WarlordSystem.Instance.GetAllWarlords()
                        .Where(w => w != null && w.IsAlive)
                        .Select(w => w.StringId)
                        .ToList();
                    _savedQueueSize = _partiesToCalculate.Count;
                }

                _ = dataStore.SyncData("BM_WarlordIds",    ref _savedWarlordIds);
                _ = dataStore.SyncData("BM_WarlordQueueSz", ref _savedQueueSize);

                if (dataStore.IsLoading)
                {
                    // Kuyruk yükleme sonrası sıfırlanır; OnDailyTick'te dolduruluyor.
                    _partiesToCalculate.Clear();
                    Debug.DebugLogger.Info("WarlordCampaignBehavior",
                        $"SyncData loaded: {_savedWarlordIds?.Count ?? 0} warlords persisted.");
                }
            }
            catch (Exception ex)
            {
                Debug.DebugLogger.Warning("WarlordCampaignBehavior", $"SyncData error: {ex.Message}");
            }
        }

        private void OnDailyTick()
        {
            _partiesToCalculate.Clear();
            
            // Tüm rütbelerdeki (Eskiya'dan Fatih'e) milisleri hesaplama kuyruğuna ekle.
            // StrategyEngine rütbeye göre kararlarını kendisi ölçeklendirecektir.
            foreach (var warlord in WarlordSystem.Instance.GetAllWarlords())
            {
                if (warlord != null && warlord.IsAlive)
                {
                    foreach (var party in warlord.CommandedMilitias.ToList()) // ✅ FIX: Snapshot to prevent concurrent modification
                    {
                        if (party != null && party.IsActive)
                        {
                            _partiesToCalculate.Enqueue(party);
                        }
                    }
                }
            }
        }

        private void OnHourlyTick()
        {
            // Her saat başı, sadece BİRKAÇ partinin stratejisini hesapla.  
            // Bu, tek çekirdekli motorun kilitlenmesini engeller.
            int calculationsPerTick = 3; 
            
            for (int i = 0; i < calculationsPerTick; i++)
            {
                if (_partiesToCalculate.Count > 0)
                {
                    MobileParty party = _partiesToCalculate.Dequeue();
                    if (party != null && party.IsActive)
                    {
                        // Strateji güncellemesini BURADA çalıştır (Asenkron ağır işlem).
                        StrategyEngine.UpdateWarlordStrategy(party); 
                    }
                }
            }
        }
    }
}
