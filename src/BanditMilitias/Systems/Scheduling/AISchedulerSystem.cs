using BanditMilitias.Core.Components;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.AI;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace BanditMilitias.Systems.Scheduling
{
    /// <summary>
    /// Milisya AI kararlarýný ve Spawn deðerlendirmelerini sýraya alýr ve her saatte en fazla
    /// <see cref="MaxTasksPerTick"/> kadar iþler.
    ///
    /// Tasarým notlarý:
    ///  - Campaign loop tamamen single-threaded -> ConcurrentQueue, volatile, lock yok.
    ///  - Acil görevler (savaþtaki partiler) doðrudan iþlenir, kuyruðu atlar.
    ///  - Düþük öncelikli (devriye) görevler kuyruða alýnýr; doðal staggering saðlar.
    ///  - Spawn deðerlendirmeleri de bütçeyi paylaþýr (yük dengeleme).
    /// </summary>
    [AutoRegister]
    public class AISchedulerSystem : MilitiaModuleBase
    {
        private static AISchedulerSystem? _instance;
        public static AISchedulerSystem Instance =>
            _instance ??= ModuleManager.Instance.GetModule<AISchedulerSystem>()
                          ?? new AISchedulerSystem();

        public override string ModuleName => "AIScheduler";
        public override bool IsEnabled => Settings.Instance?.EnableAIScheduler ?? true;
        public override int Priority => 90;

        private readonly Queue<MobileParty> _urgentQueue = new();
        private readonly Queue<MobileParty> _normalQueue = new();
        private readonly Queue<Settlement> _spawnQueue = new();
        private readonly HashSet<Settlement> _spawnQueueSet = new();

        private int _totalProcessed = 0;
        private int _urgentProcessed = 0;
        private int _spawnProcessed = 0;
        private int _skippedInactive = 0;
        private long _lastTickMs = 0;

        private int MaxTasksPerTick => Settings.Instance?.MaxAITasksPerTick ?? 20;

        public override void Initialize()
        {
            _instance = this;
            _urgentQueue.Clear();
            _normalQueue.Clear();
            _spawnQueue.Clear();
            _spawnQueueSet.Clear();
        }

        public override void Cleanup()
        {
            _urgentQueue.Clear();
            _normalQueue.Clear();
            _spawnQueue.Clear();
            _spawnQueueSet.Clear();
            _instance = null;
        }

        /// <summary>
        /// Bir partiyi AI karari kuyruguna ekler.
        /// <paramref name="urgent"/> true ise once islenir.
        /// </summary>
        public void EnqueueDecision(MobileParty party, bool urgent = false)
        {
            if (party == null || !party.IsActive) return;

            if (urgent) _urgentQueue.Enqueue(party);
            else _normalQueue.Enqueue(party);
        }

        public void EnqueueSpawnEvaluation(Settlement hideout)
        {
            if (hideout == null || !hideout.IsHideout) return;

            // O(1) gölge küme kontrolü — Queue.Contains O(n) yerine
            if (_spawnQueueSet.Add(hideout))
                _spawnQueue.Enqueue(hideout);
        }

        public override void OnHourlyTick()
        {
            if (!IsEnabled) return;
            if (CompatibilityLayer.IsGameplayActivationDelayed()) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int processed = 0;
            // FIX: IsHighLoad artık doğru scope adlarını kontrol ediyor (DiagnosticsSystem fix).
            // Yük yüksekse görev bütçesini yarıya indir — frame hiccup'larını önler.
            int limit = BanditMilitias.Systems.Diagnostics.DiagnosticsSystem.IsHighLoad
                ? MaxTasksPerTick / 2
                : MaxTasksPerTick;

            // 1. Önce acil AI görevleri
            while (_urgentQueue.Count > 0 && processed < limit)
            {
                var party = _urgentQueue.Dequeue();
                if (ProcessParty(party))
                {
                    processed++;
                    _urgentProcessed++;
                }
            }

            // 2. Normal AI görevleri
            while (_normalQueue.Count > 0 && processed < limit)
            {
                var party = _normalQueue.Dequeue();
                if (ProcessParty(party)) processed++;
            }

            // 3. Spawn deðerlendirmeleri (Eðer bütçe kaldýysa)
            var spawningSystem = ModuleManager.Instance.GetModule<Spawning.MilitiaSpawningSystem>();
            while (spawningSystem != null && _spawnQueue.Count > 0 && processed < limit)
            {
                var hideout = _spawnQueue.Dequeue();
                _spawnQueueSet.Remove(hideout);
                if (hideout != null)
                {
                    try
                    {
                        spawningSystem.ProcessSpawnEvaluation(hideout);
                        processed++;
                        _spawnProcessed++;
                    }
                    catch (System.Exception ex)
                    {
                        DebugLogger.Warning("AIScheduler", $"Spawn evaluation failed for {hideout.Name}: {ex.Message}");
                    }
                }
            }

            sw.Stop();
            _totalProcessed += processed;
            _lastTickMs = sw.ElapsedMilliseconds;

            // NEW: Zombi Kurtarma Düzeltmesi (Day 26091+ gibi uzun saveler için)
            RescueZombies();

            // NEW: Performance Analytics
            if (_lastTickMs > 2 || processed > 5)
            {
                // We use string constant since we might not have direct reference to BlackBox in all environments
                // Actually, let's use the Infrastructure to relay or just use the AIDataFactory directly if visible.
                try
                {
                    CompatibilityLayer.TryRelayExternalDiagnosticEvent(
                        "SchedulerPerf",
                        $"{{\"processed\":{processed}, \"durationMs\":{_lastTickMs}, \"spawnTask\":{_spawnProcessed}}}");

                    // NEW: DevDataCollector Micro-timing
                    BanditMilitias.Systems.Dev.DevDataCollector.Instance.RecordModuleTiming("AIScheduler", _lastTickMs, $"Processed={processed}");
                }
                catch { }
            }
        }

        public override void OnDailyTick()
        {
            CleanQueue(_urgentQueue);
            CleanQueue(_normalQueue);
        }

        public void OnPartyDestroyedCleanup(MobileParty party, PartyBase _)
        {
            if (party == null) return;

            RemoveParty(_urgentQueue, party);
            RemoveParty(_normalQueue, party);
        }

        public override string GetDiagnostics() =>
            $"AIScheduler: Urgent={_urgentQueue.Count} Normal={_normalQueue.Count} Spawn={_spawnQueue.Count} | " +
            $"Processed={_totalProcessed} (U={_urgentProcessed}, S={_spawnProcessed}) | " +
            $"Skipped={_skippedInactive} | LastTick={_lastTickMs}ms";

        private bool ProcessParty(MobileParty party)
        {
            if (party == null || !party.IsActive)
            {
                _skippedInactive++;
                return false;
            }

            try
            {
                CustomMilitiaAI.UpdateTacticalDecision(party);
                return true;
            }
            catch (System.Exception ex)
            {
                BanditMilitias.Debug.DebugLogger.Warning(
                    "AIScheduler", $"{party.Name} islenirken hata: {ex.Message}");
                return false;
            }
        }

        private static void CleanQueue(Queue<MobileParty> queue)
        {
            int count = queue.Count;
            for (int i = 0; i < count; i++)
            {
                var party = queue.Dequeue();
                if (party?.IsActive == true)
                    queue.Enqueue(party);
            }
        }

        private static void RemoveParty(Queue<MobileParty> queue, MobileParty party)
        {
            int count = queue.Count;
            for (int i = 0; i < count; i++)
            {
                var queuedParty = queue.Dequeue();
                if (queuedParty != null && queuedParty != party)
                    queue.Enqueue(queuedParty);
            }
        }

        private void RescueZombies()
        {
            if (TaleWorlds.CampaignSystem.Campaign.Current == null) return;
            var now = TaleWorlds.CampaignSystem.CampaignTime.Now;

            foreach (var party in MobileParty.All)
            {
                if (party?.IsActive == true && party.PartyComponent is Components.MilitiaPartyComponent comp)
                {
                    float overdueHours = comp.GetSleepOverdueHours();

                    // Zombi eşiği: 12 saatten fazla gecikmiş (negatif değerler)
                    if (overdueHours >= 6f)
                    {
                        comp.WakeUp();
                        comp.IsPriorityAIUpdate = true;

                        // APTAL MOD (Safe AI): Henüz emri yoksa eve yönlendir
                        var home = comp.GetHomeSettlement();
                        if (comp.CurrentOrder == null && home != null)
                        {
                            comp.CurrentOrder = new Intelligence.Strategic.StrategicCommand
                            {
                                Type = Intelligence.Strategic.CommandType.Patrol,
                                TargetLocation = BanditMilitias.Infrastructure.CompatibilityLayer.ToVec2(home.GatePosition),
                                Priority = 0.5f,
                                Reason = "Zombi Kurtarma: Güvenli Eve Dönüş (Aptal Mod)"
                            };
                            comp.OrderTimestamp = now;
                        }

                        // STAGGERING: CPU yükünü dağıtmak için rastgele 0.1-3 saat arası uyut
                        EnqueueDecision(party, urgent: true);
                    }
                }
            }
        }
    }
}
