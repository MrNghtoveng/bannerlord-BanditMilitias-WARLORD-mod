// ============================================================
// Core/Neural/NeuralEventRouter.cs
// BanditMilitias — EventBus için Yapay Sinir Ağı Yönlendirici  v1.1
//
// SORUN (bu dosya olmadan):
//   • EventBus.Publish<T>() tüm aboneleri seri çağırır — yük körü.
//   • Yüksek CPU anında Low/Normal olaylar da tam işlenir, zaman çalar.
//   • Burst: tek tick'te 40+ aynı tür olay; hepsi işlenir = boşa gider.
//   • Aboneler yük bilmez; kör alıcı gibi her şeyi kabul eder.
//
// ÇÖZÜM — Sinir Ağı Yönlendirici:
//
//   NeuralEventRouter.Publish(evt)
//          ↓
//   [1] SynapticMap   → EventNeuronCategory ata
//   [2] EventNeuron   → ShouldFire? (Yük × Ağırlık × Eşik)
//          ↓ Evet                   ↓ Hayır
//   EventBus.Publish()          Ertele (Deferred) veya Düşür
//   [3] Lateral İnhibisyon → komşu nöronları söndür
//   [4] Sinaptik Yorgunluk → burst sonrası ağırlık azalır
//          ↓
//   Tick başı: TickReset() → bütçe + ağırlık iyileşir
//
// Nöron Kategorileri:
//   Sensory    → Tehdit, korku, bölge, oyuncu yakınlığı
//   Career     → Kariyer, tier, ittifak, ihanet, miras
//   Combat     → Savaş, kuşatma, yağma, emir, strateji
//   Spawn      → Milis doğum / ölüm / birleşme / çözülme
//   Logistics  → Lojistik, ekonomi, haraç, atölye
//   Autonomic  → Arka plan (doktrin, kriz, legacy, adapt.)
//
// Entegrasyon (tek satır):
//   NervousSystem.OnHourlyTick() başına:
//     NeuralEventRouter.Instance.OnHourlyTick();
//
//   Publish site'larda:
//     EventBus.Instance.Publish(x)  →  NeuralEventRouter.Instance.Publish(x)
//
// NOT: Debug/AIDecisionEvent çağrıları EventBus'ı doğrudan kullanmaya
//   devam eder — debug olayları neural filtreye girmez.
// ============================================================

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Systems.Territory;

namespace BanditMilitias.Core.Neural
{
    // ══════════════════════════════════════════════════════════════
    // 1. NÖRON KATEGORİSİ
    // ══════════════════════════════════════════════════════════════

    public enum EventNeuronCategory
    {
        Sensory    = 0,   // Tehdit, korku, bölge, oyuncu
        Career     = 1,   // Kariyer, tier, ittifak, ihanet
        Combat     = 2,   // Savaş, yağma, emir, strateji
        Spawn      = 3,   // Milis doğum / ölüm / birleşme
        Logistics  = 4,   // Lojistik, ekonomi, haraç, atölye
        Autonomic  = 5,   // Arka plan (kriz, doktrin, legacy)
    }

    // ══════════════════════════════════════════════════════════════
    // 2. NÖRON DURUMU
    // ══════════════════════════════════════════════════════════════

    public enum NeuronState
    {
        Resting,      // Normal çalışma
        Refractory,   // Az önce ateşlendi; kısa dinlenme
        Inhibited,    // Komşu yüksek-öncelikli ateşleme bastırdı
        Fatigued,     // Tick bütçesi doldu
    }

    // ══════════════════════════════════════════════════════════════
    // 3. OLAY NÖRONU
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Tek kategorinin olaylarını filtreleyen nöron.
    ///
    /// Ateşleme kararı:
    ///   score = priorityNorm * SynapticWeight
    ///   fire  = score >= EffectiveThreshold AND BudgetRemaining > 0
    ///
    /// Yük etkisi: EffectiveThreshold += LoadPenalty (yüksek yük anında)
    /// Lateral inhibisyon: Inhibit() çağrısı eşiği +0.40 iter
    /// Sinaptik yorgunluk: Burst ateşlemede SynapticWeight azalır;
    ///   her TickReset() sonrası 1.0'a doğru iyileşir (+0.20/tick).
    /// </summary>
    public sealed class EventNeuron
    {
        public EventNeuronCategory Category { get; }

        // Eşik & Ağırlık
        public float BaseThreshold  { get; set; } = 0.30f;
        public float LoadPenalty    { get; set; } = 0.25f;
        public float SynapticWeight { get; private set; } = 1.0f;

        public float EffectiveThreshold =>
            BaseThreshold
            + (_isHighLoad  ? LoadPenalty : 0f)
            + (_isInhibited ? 0.40f       : 0f);

        // Bütçe
        public int  TickBudget      { get; set; }
        private int _budgetUsed;
        public int  BudgetRemaining => Math.Max(0, TickBudget - _budgetUsed);
        public bool IsFatigued      => BudgetRemaining == 0;

        // Dinlenme periyodu
        public long RefractoryMs    { get; set; } = 0;
        private long _lastFireMs;

        // Durum
        private bool _isHighLoad;
        private bool _isInhibited;

        public NeuronState State =>
            IsFatigued                                     ? NeuronState.Fatigued   :
            _isInhibited                                   ? NeuronState.Inhibited  :
            (RefractoryMs > 0 &&
             (NowMs() - _lastFireMs) < RefractoryMs)       ? NeuronState.Refractory :
            NeuronState.Resting;

        // Tanı
        private long _totalFired;
        private long _totalDeferred;
        private long _totalDropped;
        public long TotalFired    => _totalFired;
        public long TotalDeferred => _totalDeferred;
        public long TotalDropped  => _totalDropped;

        public EventNeuron(EventNeuronCategory category, int tickBudget)
        {
            Category   = category;
            TickBudget = tickBudget;
        }

        // ── Ateşleme kararı ───────────────────────────────────────

        public bool ShouldFire(EventPriority priority)
        {
            // Critical her zaman geçer
            if (priority == EventPriority.Critical) return true;

            var st = State;
            if (st == NeuronState.Fatigued)  return false;
            if (st == NeuronState.Inhibited) return priority == EventPriority.High;
            if (st == NeuronState.Refractory) return false;

            float priorityNorm = priority switch
            {
                EventPriority.Critical => 1.00f,
                EventPriority.High     => 0.75f,
                EventPriority.Normal   => 0.40f,
                EventPriority.Low      => 0.10f,
                _                      => 0.40f
            };

            return (priorityNorm * SynapticWeight) >= EffectiveThreshold;
        }

        public void RecordFire(EventPriority priority)
        {
            _budgetUsed++;
            _lastFireMs = NowMs();
            Interlocked.Increment(ref _totalFired);

            float fill    = (float)_budgetUsed / Math.Max(1, TickBudget);
            float fatigue = fill > 0.5f ? 0.05f : 0.01f;
            SynapticWeight = Math.Max(0.10f, SynapticWeight - fatigue);
        }

        public void RecordDeferred() => Interlocked.Increment(ref _totalDeferred);
        public void RecordDropped()  => Interlocked.Increment(ref _totalDropped);

        // Dış kontrol
        public void SetHighLoad(bool v)  => _isHighLoad  = v;
        public void Inhibit()            => _isInhibited = true;
        public void Disinhibit()         => _isInhibited = false;

        // Tick sıfırlama
        public void TickReset()
        {
            _budgetUsed  = 0;
            _isInhibited = false;
            SynapticWeight = Math.Min(1.0f, SynapticWeight + 0.20f);
        }

        public string GetDiagnostics() =>
            $"[Nöron/{Category,-10}] state={State,-10} " +
            $"w={SynapticWeight:F2} thr={EffectiveThreshold:F2} " +
            $"bütçe={BudgetRemaining}/{TickBudget} " +
            $"fired={_totalFired} ertele={_totalDeferred} düş={_totalDropped}";

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long NowMs() =>
            System.Diagnostics.Stopwatch.GetTimestamp() * 1000L /
            System.Diagnostics.Stopwatch.Frequency;
    }

    // ══════════════════════════════════════════════════════════════
    // 4. SINAPTIK HARİTA — Olay Tipi → Nöron Kategorisi
    // ══════════════════════════════════════════════════════════════

    internal static class SynapticMap
    {
        private static readonly Dictionary<Type, EventNeuronCategory> _map =
            new()
        {
            // ── Sensory ──────────────────────────────────────────
            { typeof(ThreatLevelChangedEvent),        EventNeuronCategory.Sensory   },
            { typeof(PlayerEnteredTerritoryEvent),    EventNeuronCategory.Sensory   },
            { typeof(TerritoryOpportunityEvent),      EventNeuronCategory.Sensory   },
            { typeof(VillageResistanceEvent),         EventNeuronCategory.Sensory   },

            // ── Career ───────────────────────────────────────────
            { typeof(CareerFatihPromotionEvent),      EventNeuronCategory.Career    },
            { typeof(CareerTierChangedEvent),         EventNeuronCategory.Career    },
            { typeof(AllianceOfferEvent),             EventNeuronCategory.Career    },
            { typeof(WarlordBetrayedEvent),           EventNeuronCategory.Career    },
            { typeof(WarlordLevelChangedEvent),       EventNeuronCategory.Career    },
            { typeof(WarlordBountyThresholdReachedEvent), EventNeuronCategory.Career },
            { typeof(WarlordAllianceFormedEvent),     EventNeuronCategory.Career    },
            { typeof(WarlordRivalryEscalatedEvent),   EventNeuronCategory.Career    },
            { typeof(WarlordBackstabEvent),           EventNeuronCategory.Career    },
            { typeof(WarlordFallenEvent),             EventNeuronCategory.Career    },

            // ── Combat ───────────────────────────────────────────
            { typeof(HideoutClearedEvent),            EventNeuronCategory.Combat    },
            { typeof(MilitiaRaidEvent),               EventNeuronCategory.Combat    },
            { typeof(MilitiaRaidCompletedEvent),      EventNeuronCategory.Combat    },
            { typeof(MilitiaBattleResultEvent),       EventNeuronCategory.Combat    },
            { typeof(StrategicCommandEvent),          EventNeuronCategory.Combat    },
            { typeof(CommandCompletionEvent),         EventNeuronCategory.Combat    },
            { typeof(SiegePreparationReadyEvent),     EventNeuronCategory.Combat    },

            // ── Spawn ────────────────────────────────────────────
            { typeof(MilitiaSpawnedEvent),            EventNeuronCategory.Spawn     },
            { typeof(MilitiaDisbandedEvent),          EventNeuronCategory.Spawn     },
            { typeof(MilitiaMergeEvent),              EventNeuronCategory.Spawn     },
            { typeof(MilitiaKilledEvent),             EventNeuronCategory.Spawn     },

            // ── Logistics ────────────────────────────────────────
            { typeof(TributeCollectedEvent),          EventNeuronCategory.Logistics },

            // ── Autonomic ────────────────────────────────────────
            { typeof(AdaptiveDoctrineShiftedEvent),   EventNeuronCategory.Autonomic },
            { typeof(CrisisStartedEvent),             EventNeuronCategory.Autonomic },
            { typeof(LegacyEchoActivatedEvent),       EventNeuronCategory.Autonomic },
            { typeof(StrategicAssessmentEvent),       EventNeuronCategory.Autonomic },
            // AIDecisionEvent → debug, NeuralRouter'a GİRMEZ (direkt EventBus)
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EventNeuronCategory Classify(Type t) =>
            _map.TryGetValue(t, out var cat) ? cat : EventNeuronCategory.Autonomic;

        public static void Register(Type t, EventNeuronCategory cat) => _map[t] = cat;
    }

    // ══════════════════════════════════════════════════════════════
    // 5. LATERAL İNHİBİSYON HARİTASI
    // ══════════════════════════════════════════════════════════════

    internal static class LateralInhibitionMap
    {
        private static readonly Dictionary<EventNeuronCategory, EventNeuronCategory[]> _inhibits = new()
        {
            // Savaş başlayınca: lojistik ve arka plan bekler
            { EventNeuronCategory.Combat,    new[] { EventNeuronCategory.Logistics, EventNeuronCategory.Autonomic } },
            // Tehdit değişince: arka plan bekler
            { EventNeuronCategory.Sensory,   new[] { EventNeuronCategory.Autonomic } },
            // Kariyer olayı: lojistik bekler
            { EventNeuronCategory.Career,    new[] { EventNeuronCategory.Logistics } },
            // Spawn: arka plan bekler
            { EventNeuronCategory.Spawn,     new[] { EventNeuronCategory.Autonomic } },
        };

        public static EventNeuronCategory[] GetInhibited(EventNeuronCategory src) =>
            _inhibits.TryGetValue(src, out var t) ? t : Array.Empty<EventNeuronCategory>();
    }

    // ══════════════════════════════════════════════════════════════
    // 6. NEURAL EVENT ROUTER — Ana Koordinatör
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// EventBus üzerinde çalışan yapay sinir ağı yönlendirici.
    ///
    /// Tüm sistemler EventBus.Instance.Publish(x) yerine
    /// NeuralEventRouter.Instance.Publish(x) kullanır.
    /// Debug/AIDecisionEvent çağrıları EventBus'ı doğrudan çağırır.
    ///
    /// NervousSystem.OnHourlyTick() başında:
    ///   NeuralEventRouter.Instance.OnHourlyTick();
    /// </summary>
    public sealed class NeuralEventRouter
    {
        // ── Singleton ─────────────────────────────────────────────
        private static readonly Lazy<NeuralEventRouter> _inst = new(() => new NeuralEventRouter());
        public static NeuralEventRouter Instance => _inst.Value;
        private NeuralEventRouter() { _initNeurons(); }

        // ── Nöronlar ─────────────────────────────────────────────
        private readonly EventNeuron[] _neurons = new EventNeuron[6];

        private void _initNeurons()
        {
            //                                                       Bütçe  Eşik   YükCeza
            _neurons[(int)EventNeuronCategory.Sensory]   = new EventNeuron(EventNeuronCategory.Sensory,    60) { BaseThreshold = 0.15f, LoadPenalty = 0.15f };
            _neurons[(int)EventNeuronCategory.Career]    = new EventNeuron(EventNeuronCategory.Career,     20) { BaseThreshold = 0.25f, LoadPenalty = 0.25f };
            _neurons[(int)EventNeuronCategory.Combat]    = new EventNeuron(EventNeuronCategory.Combat,     50) { BaseThreshold = 0.15f, LoadPenalty = 0.20f };
            _neurons[(int)EventNeuronCategory.Spawn]     = new EventNeuron(EventNeuronCategory.Spawn,      40) { BaseThreshold = 0.20f, LoadPenalty = 0.25f };
            _neurons[(int)EventNeuronCategory.Logistics] = new EventNeuron(EventNeuronCategory.Logistics,  20) { BaseThreshold = 0.35f, LoadPenalty = 0.35f };
            _neurons[(int)EventNeuronCategory.Autonomic] = new EventNeuron(EventNeuronCategory.Autonomic,  15) { BaseThreshold = 0.40f, LoadPenalty = 0.40f };
        }

        // ── Yük durumu ────────────────────────────────────────────
        private bool _isHighLoad;

        public void SetHighLoad(bool v)
        {
            _isHighLoad = v;
            foreach (var n in _neurons) n.SetHighLoad(v);
        }

        // ── Publish — Sinir Ağı Üzerinden ────────────────────────

        /// <summary>
        /// Olayı sinir ağı katmanından geçirerek yayınlar.
        ///
        /// Critical → her zaman geçer.
        /// High/Normal → ShouldFire false ise Deferred kuyruğuna.
        /// Low + yüksek yük → düşürülür (dropped).
        /// Lateral inhibisyon: Critical/High ateşlemesi komşuları baskılar.
        /// </summary>
        public void Publish<T>(T gameEvent) where T : IGameEvent
        {
            if (gameEvent == null) return;

            var cat    = SynapticMap.Classify(typeof(T));
            var neuron = _neurons[(int)cat];
            var prio   = gameEvent.Priority;

            if (neuron.ShouldFire(prio))
            {
                neuron.RecordFire(prio);
                EventBus.Instance.Publish(gameEvent);

                // Lateral inhibisyon: yalnızca Critical/High tetikler
                if (prio <= EventPriority.High)
                {
                    foreach (var inhibCat in LateralInhibitionMap.GetInhibited(cat))
                        _neurons[(int)inhibCat].Inhibit();
                }
            }
            else if (prio <= EventPriority.Normal)
            {
                // CRASH-PREVENTION: Pooled event'leri ASLA erteleme (PublishDeferred desteklemez)
                // IPoolableEvent arayüzünü hem direkt hem de Type bazlı kontrol et (daha güvenli)
                if (gameEvent is IPoolableEvent || typeof(IPoolableEvent).IsAssignableFrom(typeof(T)))
                {
                    neuron.RecordFire(prio);
                    EventBus.Instance.Publish(gameEvent);
                }
                else
                {
                    neuron.RecordDeferred();
                    EventBus.Instance.PublishDeferred(gameEvent);
                }
            }
            else
            {
                // Low öncelikli + eşik aşılamadı → düşür
                neuron.RecordDropped();

                if (BanditMilitias.Settings.Instance?.TestingMode == true)
                    DebugLogger.Warning("NeuralRouter",
                        $"Dropped [{cat}] {typeof(T).Name} " +
                        $"(prio={prio}, w={neuron.SynapticWeight:F2}, state={neuron.State})");
            }
        }

        // ── Tick Entegrasyonu ─────────────────────────────────────

        /// <summary>
        /// NervousSystem.OnHourlyTick() başında çağrılır.
        /// SharedPercept yükünü okur, bütçeleri ayarlar, nöronları sıfırlar.
        /// </summary>
        public void OnHourlyTick()
        {
            bool isHigh = SharedPercept.Current?.IsHighLoad ?? false;
            SetHighLoad(isHigh);

            if (isHigh)
            {
                // Yüksek yük: bütçeleri yarıya indir
                _neurons[(int)EventNeuronCategory.Sensory].TickBudget   = 40;
                _neurons[(int)EventNeuronCategory.Career].TickBudget    = 10;
                _neurons[(int)EventNeuronCategory.Combat].TickBudget    = 25;
                _neurons[(int)EventNeuronCategory.Spawn].TickBudget     = 20;
                _neurons[(int)EventNeuronCategory.Logistics].TickBudget = 8;
                _neurons[(int)EventNeuronCategory.Autonomic].TickBudget = 5;
            }
            else
            {
                // Normal yük: varsayılan bütçeler
                _neurons[(int)EventNeuronCategory.Sensory].TickBudget   = 60;
                _neurons[(int)EventNeuronCategory.Career].TickBudget    = 20;
                _neurons[(int)EventNeuronCategory.Combat].TickBudget    = 50;
                _neurons[(int)EventNeuronCategory.Spawn].TickBudget     = 40;
                _neurons[(int)EventNeuronCategory.Logistics].TickBudget = 20;
                _neurons[(int)EventNeuronCategory.Autonomic].TickBudget = 15;
            }

            foreach (var n in _neurons)
                n.TickReset();
        }

        /// <summary>
        /// Oyun kapanırken nöron sayaçlarını sıfırla.
        /// SubModule.OnGameEnd() içinde EventBus.Clear()'dan önce çağrılır.
        /// </summary>
        public void Reset()
        {
            _isHighLoad = false;
            _initNeurons();   // taze nöronlar → tüm sayaçlar sıfır
        }

        // ── Genişletilebilirlik ───────────────────────────────────

        public void RegisterCategory(Type eventType, EventNeuronCategory category) =>
            SynapticMap.Register(eventType, category);

        public EventNeuron GetNeuron(EventNeuronCategory category) =>
            _neurons[(int)category];

        // ── Tanı ─────────────────────────────────────────────────

        public string GetDiagnostics()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== NeuralEventRouter (yük={(_isHighLoad ? "YÜKSEK" : "normal")}) ===");
            foreach (var n in _neurons)
                sb.AppendLine(n.GetDiagnostics());
            return sb.ToString();
        }
    }
}
