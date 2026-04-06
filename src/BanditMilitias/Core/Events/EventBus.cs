using BanditMilitias.Core.Registry;
using BanditMilitias.Debug;
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

        private EventBus() { }

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
                try
                {
                    _handler((T)gameEvent);
                    ModuleRegistry.Instance.RecordActivity(_handler, "EventDispatch");
                }
                catch (Exception ex)
                {
                    ModuleRegistry.Instance.RecordFailure(_handler, ex.Message);
                    throw;
                }
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
                    try
                    {
                        _handler(typedEvent);
                        ModuleRegistry.Instance.RecordActivity(_handler, "EventDispatch");
                    }
                    catch (Exception ex)
                    {
                        ModuleRegistry.Instance.RecordFailure(_handler, ex.Message);
                        throw;
                    }
                }
            }
            public bool IsMatch(Delegate otherData) => otherData.Equals(_handler);
        }

        private bool IsDebugMode => Settings.Instance?.TestingMode == true;

        private readonly Stopwatch _processStopwatch = new Stopwatch();

        private readonly ConcurrentDictionary<Type, IHandlerWrapper[]> _subscribers = new();
        private readonly object _writeLock = new object();

        private readonly ConcurrentQueue<IGameEvent> _deferredQueue = new();
        private int _deferredQueueCount = 0;
        private const int MAX_QUEUE_SIZE = 5000;
        private long _droppedDeferredCount = 0;

        private readonly ConcurrentDictionary<Type, ConcurrentQueue<IPoolableEvent>> _eventPool = new();
        private const int MAX_POOL_SIZE = 100;

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
                _ = _subscribers.TryGetValue(type, out var existing);
                if (existing != null)
                {
                    foreach (var current in existing)
                    {
                        if (current.IsMatch(handler))
                        {
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

                    var list = new List<IHandlerWrapper>(existing);
                    int removed = list.RemoveAll(w => w.IsMatch(handler));

                    if (removed > 0)
                    {
                        if (list.Count == 0)
                        {
                            _ = _subscribers.TryRemove(typeof(T), out _);
                        }
                        else
                        {
                            _subscribers[typeof(T)] = list.ToArray();
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

            if (!_subscribers.TryGetValue(typeof(T), out var snapshot) || snapshot == null || snapshot.Length == 0)
            {
                TraceSignal(gameEvent, 0);
                return;
            }

            TraceSignal(gameEvent, snapshot.Length);

            foreach (var wrapper in snapshot)
            {
                try
                {
                    wrapper.Invoke(gameEvent);
                }
                catch (Exception ex)
                {

                    DebugLogger.Error("EventBus", $"Handler error ({typeof(T).Name}): {ex.Message}");
                }
            }

        }

        private void PublishUntyped(IGameEvent gameEvent)
        {
            if (gameEvent == null) return;

            var eventType = gameEvent.GetType();

            var subscribers = _subscribers;

            if (subscribers.TryGetValue(eventType, out var handlers) && handlers != null && handlers.Length > 0)
            {

                TraceSignal(gameEvent, handlers.Length);

                foreach (var handler in handlers)
                {
                    try
                    {
                        handler.Invoke(gameEvent);
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Error("EventBus", $"Untyped handler error ({eventType.Name}): {ex.Message}");
                    }
                }
            }
            else
            {

                TraceSignal(gameEvent, 0);
            }

        }

        public void PublishDeferred<T>(T gameEvent) where T : IGameEvent
        {
            if (gameEvent == null) return;

            if (gameEvent is IPoolableEvent)
            {
                const string message =
                    "PublishDeferred does not support pooled events. Use a regular event instance or publish synchronously.";

                if (IsDebugMode)
                {
                    throw new InvalidOperationException(message);
                }

                DebugLogger.Error("EventBus", $"{message} Event={typeof(T).Name}");
                return;
            }

            int currentQueue = Volatile.Read(ref _deferredQueueCount);
            if (currentQueue >= MAX_QUEUE_SIZE)
            {
                _ = Interlocked.Increment(ref _droppedDeferredCount);
                if (IsDebugMode && (_droppedDeferredCount % 50 == 1))
                {
                    DebugLogger.Warning("EventBus",
                        $"Deferred queue saturated. Dropped event {typeof(T).Name} (Priority={gameEvent.Priority}).");
                }
                return;
            }

            _deferredQueue.Enqueue(gameEvent);
            _ = Interlocked.Increment(ref _deferredQueueCount);
        }

        private int _isProcessingQueue = 0;

        public void ProcessQueue()
        {

            if (Interlocked.CompareExchange(ref _isProcessingQueue, 1, 0) != 0)
                return;

            try
            {

                const double timeBudgetMs = 4.0;
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
            return $"Queue={Volatile.Read(ref _deferredQueueCount)}/{MAX_QUEUE_SIZE}, " +
                   $"Dropped={Interlocked.Read(ref _droppedDeferredCount)}";
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
