using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Systems.Territory;

namespace BanditMilitias.Core.Neural
{


    public enum EventNeuronCategory
    {
        Sensory    = 0,

        Career     = 1,

        Combat     = 2,

        Spawn      = 3,

        Logistics  = 4,

        Autonomic  = 5,

    }


    public enum NeuronState
    {
        Resting,

        Refractory,

        Inhibited,

        Fatigued,

    }


    public sealed class EventNeuron
    {
        public EventNeuronCategory Category { get; }


        public float BaseThreshold  { get; set; } = 0.30f;
        public float LoadPenalty    { get; set; } = 0.25f;
        public float SynapticWeight { get; private set; } = 1.0f;

        public float EffectiveThreshold =>
            BaseThreshold
            + (_isHighLoad  ? LoadPenalty : 0f)
            + (_isInhibited ? 0.40f       : 0f);


        public int  TickBudget      { get; set; }
        private int _budgetUsed;
        public int  BudgetRemaining => Math.Max(0, TickBudget - _budgetUsed);
        public bool IsFatigued      => BudgetRemaining == 0;


        public long RefractoryMs    { get; set; } = 0;
        private long _lastFireMs;


        private bool _isHighLoad;
        private bool _isInhibited;

        public NeuronState State =>
            IsFatigued                                     ? NeuronState.Fatigued   :
            _isInhibited                                   ? NeuronState.Inhibited  :
            (RefractoryMs > 0 &&
             (NowMs() - _lastFireMs) < RefractoryMs)       ? NeuronState.Refractory :
            NeuronState.Resting;


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


        public bool ShouldFire(EventPriority priority)
        {


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


        public void SetHighLoad(bool v)  => _isHighLoad  = v;
        public void Inhibit()            => _isInhibited = true;
        public void Disinhibit()         => _isInhibited = false;


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


    internal static class SynapticMap
    {
        private static readonly Dictionary<Type, EventNeuronCategory> _map =
            new()
        {


            { typeof(ThreatLevelChangedEvent),        EventNeuronCategory.Sensory   },
            { typeof(PlayerEnteredTerritoryEvent),    EventNeuronCategory.Sensory   },
            { typeof(TerritoryOpportunityEvent),      EventNeuronCategory.Sensory   },
            { typeof(VillageResistanceEvent),         EventNeuronCategory.Sensory   },


            { typeof(CareerConquerorPromotionEvent),  EventNeuronCategory.Career    },
            { typeof(CareerTierChangedEvent),         EventNeuronCategory.Career    },
            { typeof(AllianceOfferEvent),             EventNeuronCategory.Career    },
            { typeof(WarlordBetrayedEvent),           EventNeuronCategory.Career    },
            { typeof(WarlordLevelChangedEvent),       EventNeuronCategory.Career    },
            { typeof(WarlordBountyThresholdReachedEvent), EventNeuronCategory.Career },
            { typeof(WarlordAllianceFormedEvent),     EventNeuronCategory.Career    },
            { typeof(WarlordRivalryEscalatedEvent),   EventNeuronCategory.Career    },
            { typeof(WarlordBackstabEvent),           EventNeuronCategory.Career    },
            { typeof(WarlordFallenEvent),             EventNeuronCategory.Career    },


            { typeof(HideoutClearedEvent),            EventNeuronCategory.Combat    },
            { typeof(MilitiaRaidEvent),               EventNeuronCategory.Combat    },
            { typeof(MilitiaRaidCompletedEvent),      EventNeuronCategory.Combat    },
            { typeof(MilitiaBattleResultEvent),       EventNeuronCategory.Combat    },
            { typeof(StrategicCommandEvent),          EventNeuronCategory.Combat    },
            { typeof(CommandCompletionEvent),         EventNeuronCategory.Combat    },
            { typeof(SiegePreparationReadyEvent),     EventNeuronCategory.Combat    },


            { typeof(MilitiaSpawnedEvent),            EventNeuronCategory.Spawn     },
            { typeof(MilitiaDisbandedEvent),          EventNeuronCategory.Spawn     },
            { typeof(MilitiaMergeEvent),              EventNeuronCategory.Spawn     },
            { typeof(MilitiaKilledEvent),             EventNeuronCategory.Spawn     },


            { typeof(TributeCollectedEvent),          EventNeuronCategory.Logistics },


            { typeof(AdaptiveDoctrineShiftedEvent),   EventNeuronCategory.Autonomic },
            { typeof(CrisisStartedEvent),             EventNeuronCategory.Autonomic },
            { typeof(LegacyEchoActivatedEvent),       EventNeuronCategory.Autonomic },
            { typeof(StrategicAssessmentEvent),       EventNeuronCategory.Autonomic },


        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EventNeuronCategory Classify(Type t) =>
            _map.TryGetValue(t, out var cat) ? cat : EventNeuronCategory.Autonomic;

        public static void Register(Type t, EventNeuronCategory cat) => _map[t] = cat;
    }


    internal static class LateralInhibitionMap
    {
        private static readonly Dictionary<EventNeuronCategory, EventNeuronCategory[]> _inhibits = new()
        {


            { EventNeuronCategory.Combat,    new[] { EventNeuronCategory.Logistics, EventNeuronCategory.Autonomic } },


            { EventNeuronCategory.Sensory,   new[] { EventNeuronCategory.Autonomic } },


            { EventNeuronCategory.Career,    new[] { EventNeuronCategory.Logistics } },


            { EventNeuronCategory.Spawn,     new[] { EventNeuronCategory.Autonomic } },
        };

        public static EventNeuronCategory[] GetInhibited(EventNeuronCategory src) =>
            _inhibits.TryGetValue(src, out var t) ? t : Array.Empty<EventNeuronCategory>();
    }


    public sealed class NeuralEventRouter
    {


        private static readonly Lazy<NeuralEventRouter> _inst = new(() => new NeuralEventRouter());
        public static NeuralEventRouter Instance => _inst.Value;
        private NeuralEventRouter() { _initNeurons(); }


        private readonly EventNeuron[] _neurons = new EventNeuron[6];

        private void _initNeurons()
        {


            _neurons[(int)EventNeuronCategory.Sensory]   = new EventNeuron(EventNeuronCategory.Sensory,    60) { BaseThreshold = 0.15f, LoadPenalty = 0.15f };
            _neurons[(int)EventNeuronCategory.Career]    = new EventNeuron(EventNeuronCategory.Career,     20) { BaseThreshold = 0.25f, LoadPenalty = 0.25f };
            _neurons[(int)EventNeuronCategory.Combat]    = new EventNeuron(EventNeuronCategory.Combat,     50) { BaseThreshold = 0.15f, LoadPenalty = 0.20f };
            _neurons[(int)EventNeuronCategory.Spawn]     = new EventNeuron(EventNeuronCategory.Spawn,      40) { BaseThreshold = 0.20f, LoadPenalty = 0.25f };
            _neurons[(int)EventNeuronCategory.Logistics] = new EventNeuron(EventNeuronCategory.Logistics,  20) { BaseThreshold = 0.35f, LoadPenalty = 0.35f };
            _neurons[(int)EventNeuronCategory.Autonomic] = new EventNeuron(EventNeuronCategory.Autonomic,  15) { BaseThreshold = 0.40f, LoadPenalty = 0.40f };
        }


        private bool _isHighLoad;

        public void SetHighLoad(bool v)
        {
            _isHighLoad = v;
            foreach (var n in _neurons) n.SetHighLoad(v);
        }


        public void Publish<T>(T gameEvent) where T : IGameEvent
        {
            if (gameEvent == null) return;

            var cat    = SynapticMap.Classify(typeof(T));
            var neuron = _neurons[(int)cat];
            var prio   = gameEvent.Priority;

            if (neuron.ShouldFire(prio))
            {
                neuron.RecordFire(prio);
                BanditMilitias.Core.Events.EventBus.Instance.Publish(gameEvent);


                if (prio <= EventPriority.High)
                {
                    foreach (var inhibCat in LateralInhibitionMap.GetInhibited(cat))
                        _neurons[(int)inhibCat].Inhibit();
                }
            }
            else if (prio <= EventPriority.Normal)
            {


                if (gameEvent is IPoolableEvent || typeof(IPoolableEvent).IsAssignableFrom(typeof(T)))
                {
                    neuron.RecordFire(prio);
                    BanditMilitias.Core.Events.EventBus.Instance.Publish(gameEvent);
                }
                else
                {
                    neuron.RecordDeferred();
                    BanditMilitias.Core.Events.EventBus.Instance.PublishDeferred(gameEvent);
                }
            }
            else
            {


                neuron.RecordDropped();

                if (BanditMilitias.Settings.Instance?.TestingMode == true)
                    DebugLogger.Warning("NeuralRouter",
                        $"Dropped [{cat}] {typeof(T).Name} " +
                        $"(prio={prio}, w={neuron.SynapticWeight:F2}, state={neuron.State})");
            }
        }


        public void OnHourlyTick()
        {
            bool isHigh = SharedPercept.Current?.IsHighLoad ?? false;
            SetHighLoad(isHigh);

            if (isHigh)
            {


                _neurons[(int)EventNeuronCategory.Sensory].TickBudget   = 40;
                _neurons[(int)EventNeuronCategory.Career].TickBudget    = 10;
                _neurons[(int)EventNeuronCategory.Combat].TickBudget    = 25;
                _neurons[(int)EventNeuronCategory.Spawn].TickBudget     = 20;
                _neurons[(int)EventNeuronCategory.Logistics].TickBudget = 8;
                _neurons[(int)EventNeuronCategory.Autonomic].TickBudget = 5;
            }
            else
            {


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


        public void Reset()
        {
            _isHighLoad = false;
            _initNeurons();

        }


        public void RegisterCategory(Type eventType, EventNeuronCategory category) =>
            SynapticMap.Register(eventType, category);

        public EventNeuron GetNeuron(EventNeuronCategory category) =>
            _neurons[(int)category];


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
