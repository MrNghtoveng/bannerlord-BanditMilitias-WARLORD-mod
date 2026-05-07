using BanditMilitias.Core.Registry;
using BanditMilitias.Debug;
using BanditMilitias.Lifecycle;
using BanditMilitias.Intelligence.Neural;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using TaleWorlds.Library;

namespace BanditMilitias.Core.Events
{

    public class EventBus
    {

        private static readonly Lazy<EventBus> _instance = new(() => new EventBus());
        public static EventBus Instance => _instance.Value;

        private int _mainThreadId = -1;
        private NeuralBusGovernor? _governor;

        private EventBus() { }

        public void SetGovernor(NeuralBusGovernor? g) => _governor = g;

        // NervousSystem'ın high-load sync için Governor'a erişmesini sağlar.
        // Null dönebilir — çağıran null-check yapmalı.
        public NeuralBusGovernor? GetGovernor() => _governor;

        /// <summary>
        /// Must be called once from the main game thread during initialization.
        /// All ProcessQueue calls will be validated against this thread ID.
        /// </summary>
        public void CaptureMainThread()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        public bool IsOnMainThread => _mainThreadId <= 0 || Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        private void WarnIfOffThread(string caller)
        {
            if (_mainThreadId > 0 && Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            {
                DebugLogger.Warning("EventBus",
                    $"[THREAD-SAFETY] {caller} called from thread {Thread.CurrentThread.ManagedThreadId}, " +
                    $"expected main thread {_mainThreadId}. This may cause race conditions.");
            }
        }

        private interface IHandlerWrapper
        {
            void Invoke(IGameEvent gameEvent);
            bool IsMatch(Delegate otherData);
        }

        private void TraceSignal(IGameEvent gameEvent, int subscriberCount)
        {
            if (subscriberCount == 0)
            {
                if (Settings.Instance?.TestingMode == true)
                {
                    TaleWorlds.Library.Debug.Print($"[SignalTracer] DEAD SIGNAL: {gameEvent.GetType().Name} was fired but nobody listened!", 0, TaleWorlds.Library.Debug.DebugColor.Red);
                }
            }
        }

        private class ActionWrapper<T> : IHandlerWrapper where T : IGameEvent
        {
            private readonly Action<T> _handler;
            public ActionWrapper(Action<T> handler) => _handler = handler;
            public void Invoke(IGameEvent gameEvent)
            {
                _handler((T)gameEvent);
                ModuleRegistry.Instance.RecordActivity(_handler, "EventDispatch");
            }

            public bool IsMatch(Delegate otherData) => otherData.Equals(_handler);
        }

        private class FilteredWrapper<T> : IHandlerWrapper where T : IGameEvent
        {
            private readonly Action<T> _handler;
            private readonly Predicate<T> _filter;

            public FilteredWrapper(Action<T> handler, Predicate<T> filter)
            {
                _handler = handler;
                _filter = filter;
            }

            public void Invoke(IGameEvent gameEvent)
            {
                T typedEvent = (T)gameEvent;
                if (_filter(typedEvent))
                {
                    _handler(typedEvent);
                    ModuleRegistry.Instance.RecordActivity(_handler, "EventDispatch");
                }
            }
            public bool IsMatch(Delegate otherData) => otherData.Equals(_handler);
        }

        private bool IsDebugMode => Settings.Instance?.TestingMode == true;

        private readonly Stopwatch _processStopwatch = new Stopwatch();

        private readonly ConcurrentDictionary<Type, IHandlerWrapper[]> _subscribers = new();
        private readonly ConcurrentDictionary<Type, HashSet<string>> _subscriptionOwnership = new();
        private readonly object _writeLock = new object();

        private readonly ConcurrentQueue<IGameEvent> _deferredQueue = new();
        private int _deferredQueueCount = 0;
        private const int MAX_QUEUE_SIZE = 5000;
        private long _droppedDeferredCount = 0;

        private readonly ConcurrentDictionary<Type, ConcurrentQueue<IPoolableEvent>> _eventPool = new();
        private const int MAX_POOL_SIZE = 100;
        private volatile ModState _lifecycleState = ModState.Uninitialized;
        private int _sessionGeneration = 0;

        public T Get<T>() where T : IPoolableEvent, new()
        {
            if (_eventPool.TryGetValue(typeof(T), out var queue) && queue.TryDequeue(out var evt))
            {
                evt.Reset();
                return (T)evt;
            }
            return new T();
        }

        public void Return(IPoolableEvent evt)
        {
            if (evt == null) return;
            evt.Reset();

            var queue = _eventPool.GetOrAdd(evt.GetType(), _ => new ConcurrentQueue<IPoolableEvent>());
            if (queue.Count < MAX_POOL_SIZE)
            {
                queue.Enqueue(evt);
            }
        }

        public void Subscribe<T>(Action<T> handler) where T : IGameEvent
        {
            if (handler == null) return;
            if (AddSubscriber(typeof(T), new ActionWrapper<T>(handler), handler))
            {
                ModuleRegistry.Instance.RecordEventSubscription(handler, subscribed: true);
            }
        }

        public void Subscribe<T>(Action<T> handler, Predicate<T> filter) where T : IGameEvent
        {
            if (handler == null || filter == null) return;
            if (AddSubscriber(typeof(T), new FilteredWrapper<T>(handler, filter), handler))
            {
                ModuleRegistry.Instance.RecordEventSubscription(handler, subscribed: true);
            }
        }

        private bool AddSubscriber(Type type, IHandlerWrapper wrapper, Delegate handler)
        {
            lock (_writeLock)
            {
                string ownerKey = BuildOwnerKey(handler);
                HashSet<string> owners = _subscriptionOwnership.GetOrAdd(type, _ => new HashSet<string>(StringComparer.Ordinal));
                if (!owners.Add(ownerKey))
                {
                    return false;
                }

                _ = _subscribers.TryGetValue(type, out var existing);
                if (existing != null)
                {
                    foreach (var current in existing)
                    {
                        if (current.IsMatch(handler))
                        {
                            owners.Remove(ownerKey);
                            return false;
                        }
                    }
                }

                int len = existing?.Length ?? 0;
                var newArray = new IHandlerWrapper[len + 1];

                if (existing != null)
                {
                    Array.Copy(existing, newArray, len);
                }
                newArray[len] = wrapper;

                _subscribers[type] = newArray;
                return true;
            }
        }

        public void Unsubscribe<T>(Action<T> handler) where T : IGameEvent
        {
            if (handler == null) return;

            lock (_writeLock)
            {
                if (_subscribers.TryGetValue(typeof(T), out var existing) && existing != null)
                {


                    int removedCount = 0;
                    foreach (var h in existing)
                    {
                        if (h.IsMatch(handler)) removedCount++;
                    }

                    if (removedCount > 0)
                    {
                        if (_subscriptionOwnership.TryGetValue(typeof(T), out var owners))
                        {
                            _ = owners.Remove(BuildOwnerKey(handler));
                        }

                        if (existing.Length == removedCount)
                        {
                            _subscribers.TryRemove(typeof(T), out _);
                        }
                        else
                        {
                            var newArray = new IHandlerWrapper[existing.Length - removedCount];
                            int index = 0;
                            foreach (var h in existing)
                            {
                                if (!h.IsMatch(handler))
                                {
                                    newArray[index++] = h;
                                }
                            }
                            _subscribers[typeof(T)] = newArray;
                        }

                        ModuleRegistry.Instance.RecordEventSubscription(handler, subscribed: false);
                    }
                }
            }
        }

        public void ClearQueue()
        {
            while (_deferredQueue.TryDequeue(out var evt))
            {
                if (evt is IPoolableEvent poolable)
                {
                    Return(poolable);
                }
            }
            _ = Interlocked.Exchange(ref _deferredQueueCount, 0);
        }

        public void Clear()
        {
            lock (_writeLock)
            {
                _subscribers.Clear();
                _subscriptionOwnership.Clear();
                _eventPool.Clear();
                _ = Interlocked.Exchange(ref _deferredQueueCount, 0);
                _ = Interlocked.Exchange(ref _droppedDeferredCount, 0);

                while (_deferredQueue.TryDequeue(out var evt))
                {
                    if (evt is IPoolableEvent poolable) poolable.Reset();
                }
            }

            ModuleRegistry.Instance.ResetEventSubscriptions();

            if (IsDebugMode)
                InformationManager.DisplayMessage(new InformationMessage("[Cortex] Memory Cleared & Reset", Colors.Cyan));
        }

        public void Publish<T>(T gameEvent) where T : IGameEvent
        {
            if (gameEvent == null) return;

            // Subscriber kontrolü önce — subscriber yoksa Governor'a hiç gitme.
            if (!_subscribers.TryGetValue(typeof(T), out var snapshot) || snapshot == null || snapshot.Length == 0)
            {
                TraceSignal(gameEvent, 0);
                return;
            }

            if (_governor != null && _governor.ShouldSuppress(gameEvent))
                return;

            TraceSignal(gameEvent, snapshot.Length);

            foreach (var wrapper in snapshot)
            {
                wrapper.Invoke(gameEvent);
            }

        }

        private void PublishUntyped(IGameEvent gameEvent)
        {
            if (gameEvent == null) return;

            var eventType = gameEvent.GetType();
            var subscribers = _subscribers;

            // Subscriber kontrolü önce — subscriber yoksa Governor'a hiç gitme.
            if (!subscribers.TryGetValue(eventType, out var handlers) || handlers == null || handlers.Length == 0)
            {
                TraceSignal(gameEvent, 0);
                return;
            }

            if (_governor != null && _governor.ShouldSuppress(gameEvent))
                return;

            TraceSignal(gameEvent, handlers.Length);

            foreach (var handler in handlers)
            {
                handler.Invoke(gameEvent);
            }

        }

        public void PublishDeferred<T>(T gameEvent) where T : IGameEvent
        {
            if (gameEvent == null) return;
            if (!CanAcceptDeferredEvents())
            {
                _ = Interlocked.Increment(ref _droppedDeferredCount);
                return;
            }

            if (gameEvent is IPoolableEvent)
            {
                const string message =
                    "PublishDeferred does not support pooled events. Falling back to synchronous publish to prevent crash.";

                DebugLogger.Warning("EventBus", $"{message} Event={typeof(T).Name}");


                Publish(gameEvent);
                return;
            }

            int currentQueue = Volatile.Read(ref _deferredQueueCount);
            if (currentQueue >= MAX_QUEUE_SIZE)
            {


                bool isCritical   = gameEvent.Priority == EventPriority.Critical;
                bool isHigh       = gameEvent.Priority == EventPriority.High;
                bool allowOverflow = (isCritical && currentQueue < MAX_QUEUE_SIZE * 12 / 10)
                                  || (isHigh     && currentQueue < MAX_QUEUE_SIZE * 11 / 10);

                if (!allowOverflow)
                {
                    _ = Interlocked.Increment(ref _droppedDeferredCount);
                    if (IsDebugMode && (_droppedDeferredCount % 50 == 1))
                    {
                        DebugLogger.Warning("EventBus",
                            $"Deferred queue saturated. Dropped event {typeof(T).Name} (Priority={gameEvent.Priority}).");
                    }
                    return;
                }

                if (IsDebugMode)
                {
                    DebugLogger.Warning("EventBus",
                        $"Queue overflow but allowing {gameEvent.Priority} event {typeof(T).Name} (size={currentQueue}).");
                }
            }

            _deferredQueue.Enqueue(gameEvent);
            _ = Interlocked.Increment(ref _deferredQueueCount);
        }

        private int _isProcessingQueue = 0;

        public void ProcessQueue()
        {
            if (!CanPumpQueue())
                return;

            WarnIfOffThread("ProcessQueue");

            if (Interlocked.CompareExchange(ref _isProcessingQueue, 1, 0) != 0)
                return;

            try
            {
                // Ölçüm burada — sadece ProcessQueue başında, her event'te değil.
                _governor?.UpdateFrameMeasurement();
                double timeBudgetMs = _governor?.GetTimeBudgetMs() ?? 4.0;
                const int maxEventsPerPump = 64;

                _processStopwatch.Restart();
                int processedCount = 0;

                while (_deferredQueue.TryDequeue(out var gameEvent))
                {
                    int queueCount = Interlocked.Decrement(ref _deferredQueueCount);
                    if (queueCount < 0)
                    {
                        _ = Interlocked.Exchange(ref _deferredQueueCount, 0);
                    }

                    PublishUntyped(gameEvent);
                    processedCount++;

                    if (processedCount >= maxEventsPerPump)
                    {
                        break;
                    }

                    if (processedCount % 5 == 0)
                    {
                        double elapsedMs = _processStopwatch.Elapsed.TotalMilliseconds;
                        if (elapsedMs > timeBudgetMs)
                        {
                            break;
                        }
                    }
                }
            }
            finally
            {
                _ = Interlocked.Exchange(ref _isProcessingQueue, 0);
            }
        }

        public string GetQueueDiagnostics()
        {
            int q = Volatile.Read(ref _deferredQueueCount);
            long dropped = Interlocked.Read(ref _droppedDeferredCount);
            float fillPct = (float)q / MAX_QUEUE_SIZE * 100f;
            string health = fillPct >= 100f ? "DOLU" : fillPct >= 80f ? "YÜKSEK" : "OK";
            int callerThread = Thread.CurrentThread.ManagedThreadId;
            return $"Queue={q}/{MAX_QUEUE_SIZE} ({fillPct:F0}% [{health}]), " +
                   $"Dropped={dropped}, " +
                   $"PoolTypes={_eventPool.Count}, " +
                   $"Lifecycle={_lifecycleState}, Session={_sessionGeneration}, " +
                   $"MainThread={_mainThreadId}, CallerThread={callerThread}" +
                   (_governor != null ? $" | {_governor.GetDiagnostics()}" : "");
        }

        public void SetLifecycleState(ModState state)
        {
            _lifecycleState = state;
        }

        public void ResetForSessionEnd()
        {
            _ = Interlocked.Increment(ref _sessionGeneration);
            Clear();
            _lifecycleState = ModState.Ready;
        }

        public void ResetForModuleUnload()
        {
            _ = Interlocked.Increment(ref _sessionGeneration);
            Clear();
            _lifecycleState = ModState.Uninitialized;
        }

        private bool CanAcceptDeferredEvents()
        {
            return _lifecycleState is ModState.Ready
                or ModState.Dormant
                or ModState.Active
                or ModState.Degraded;
        }

        private bool CanPumpQueue()
        {
            return _lifecycleState is ModState.Ready
                or ModState.Dormant
                or ModState.Active
                or ModState.Degraded;
        }

        private static string BuildOwnerKey(Delegate handler)
        {
            object target = handler.Target ?? handler.Method.DeclaringType ?? typeof(EventBus);
            return $"{target.GetHashCode()}::{handler.Method.DeclaringType?.FullName}::{handler.Method.Name}";
        }
    }

    public enum EventPriority
    {
        Critical = 0,
        High = 1,
        Normal = 2,
        Low = 3
    }

    public interface IGameEvent
    {
        EventPriority Priority { get; }
        bool ShouldLog { get; }
        string GetDescription();
    }

    public interface IPoolableEvent : IGameEvent
    {
        void Reset();
    }
}
