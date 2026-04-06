using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using System.Collections.Generic;
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

        public override void SyncData(IDataStore dataStore)
        {
            // WarlordCampaignBehavior için şu anlık özel bir veri saklanmıyor.
            // Kuyruk (Queue) günlük olarak OnDailyTick'te yenilendiği için serialize edilmesine gerek yok.
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
                    foreach (var party in warlord.CommandedMilitias)
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
            // Her saat başı, sadece BİRKAÇ partinin stratejisini (QiRL) hesapla. 
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
