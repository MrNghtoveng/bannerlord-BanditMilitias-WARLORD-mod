using BanditMilitias.Debug;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace BanditMilitias.Infrastructure
{
    // ── PIDController ─────────────────────────────────────────

    public class PIDController
    {
        private readonly float _kp;
        private readonly float _ki;
        private readonly float _kd;
        private readonly float _outputMin;
        private readonly float _outputMax;

        private float _integral;
        private float _previousError;
        private bool _hasPreviousError;

        public float Setpoint { get; set; }
        public float Output { get; private set; }

        public PIDController(float kp, float ki, float kd, float setpoint = 0f, float outputMin = -5f, float outputMax = 5f)
        {
            if (outputMin > outputMax)
            {
                throw new System.ArgumentException("Output minimum cannot exceed output maximum.");
            }

            _kp = kp;
            _ki = ki;
            _kd = kd;
            _outputMin = outputMin;
            _outputMax = outputMax;
            Setpoint = setpoint;
        }

        public void Update(float currentValue, float deltaTime)
        {
            if (deltaTime <= 0.0001f) return;

            float error = Setpoint - currentValue;

            float P = _kp * error;

            // BUG-K: Anti-Windup - Only integrate if output is not saturated or error is moving back
            bool saturated = Output >= _outputMax || Output <= _outputMin;
            bool reducingError = (Output > 0 && error < 0) || (Output < 0 && error > 0);

            if (!saturated || reducingError)
            {
                _integral = Clamp(_integral + error * deltaTime, -5.0f, 5.0f);
            }

            float I = _ki * _integral;

            float derivative = _hasPreviousError
                ? (error - _previousError) / deltaTime
                : 0f;
            float D = _kd * derivative;

            Output = P + I + D;

            Output = Clamp(Output, _outputMin, _outputMax);

            _previousError = error;
            _hasPreviousError = true;
        }

        public void Reset()
        {
            _integral = 0f;
            _previousError = 0f;
            _hasPreviousError = false;
            Output = 0f;
        }


        private static float Clamp(float value, float min, float max)
        {
            return (float)System.Math.Max(min, System.Math.Min(max, value));
        }
    }

    // ── CircularBuffer ─────────────────────────────────────────

    public class CircularBuffer<T> : IEnumerable<T>
    {
        private readonly T[] _buffer;
        private int _head;
        private int _tail;
        private int _count;
        private readonly object _lock = new object();

        public int Capacity => _buffer.Length;
        public int Count => _count;

        public CircularBuffer(int capacity)
        {
            if (capacity <= 0) throw new ArgumentException("Capacity must be positive", nameof(capacity));
            _buffer = new T[capacity];
            _head = 0;
            _tail = 0;
            _count = 0;
        }

        public void Add(T item)
        {
            lock (_lock)
            {
                if (_count == Capacity)
                {

                    _buffer[_head] = item;
                    _head = (_head + 1) % Capacity;
                    _tail = (_tail + 1) % Capacity;
                }
                else
                {

                    _buffer[_tail] = item;
                    _tail = (_tail + 1) % Capacity;
                    _count++;
                }
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _head = 0;
                _tail = 0;
                _count = 0;
                Array.Clear(_buffer, 0, Capacity);
            }
        }

        public IEnumerator<T> GetEnumerator()
        {

            int count, head;
            T[] snapshot;

            lock (_lock)
            {
                count = _count;
                head = _head;
                snapshot = new T[Capacity];
                Array.Copy(_buffer, snapshot, Capacity);
            }

            for (int i = 0; i < count; i++)
            {
                yield return snapshot[(head + i) % Capacity];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // ── LRUCache ─────────────────────────────────────────

    public class LRUCache<K, V> where K : notnull
    {
        private readonly int _capacity;
        private readonly Dictionary<K, LinkedListNode<CacheItem>> _cacheMap;
        private readonly LinkedList<CacheItem> _lruList;
        private readonly object _lock = new object();

        private struct CacheItem
        {
            public K Key;
            public V Value;
        }

        public LRUCache(int capacity)
        {
            if (capacity <= 0) throw new ArgumentException("Capacity must be positive");
            _capacity = capacity;
            _cacheMap = new Dictionary<K, LinkedListNode<CacheItem>>(capacity);
            _lruList = new LinkedList<CacheItem>();
        }

        public V? Get(K key)
        {
            lock (_lock)
            {
                if (_cacheMap.TryGetValue(key, out var node))
                {
                    V value = node.Value.Value;
                    _lruList.Remove(node);
                    _lruList.AddLast(node);
                    return value;
                }
                return default;
            }
        }

        public bool TryGet(K key, out V? value)
        {
            lock (_lock)
            {
                if (_cacheMap.TryGetValue(key, out var node))
                {
                    value = node.Value.Value;
                    _lruList.Remove(node);
                    _lruList.AddLast(node);
                    return true;
                }
                value = default;
                return false;
            }
        }

        public void Set(K key, V value)
        {
            lock (_lock)
            {
                if (_cacheMap.TryGetValue(key, out var node))
                {

                    _lruList.Remove(node);
                    node.Value = new CacheItem { Key = key, Value = value };
                    _lruList.AddLast(node);
                    _cacheMap[key] = node;
                }
                else
                {

                    if (_cacheMap.Count >= _capacity)
                    {
                        RemoveFirst();
                    }

                    var cacheItem = new CacheItem { Key = key, Value = value };
                    var newNode = new LinkedListNode<CacheItem>(cacheItem);
                    _lruList.AddLast(newNode);
                    _cacheMap[key] = newNode;
                }
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _cacheMap.Clear();
                _lruList.Clear();
            }
        }

        private void RemoveFirst()
        {

            LinkedListNode<CacheItem>? node = _lruList.First;
            if (node == null) return;

            _lruList.RemoveFirst();

            _ = _cacheMap.Remove(node.Value.Key);
        }
    }

    // ── ObjectPool ─────────────────────────────────────────

    public class ObjectPool<T> where T : class
    {
        private readonly ConcurrentBag<T> _objects;
        private readonly Func<T> _objectGenerator;
        private readonly Action<T>? _resetAction;

        public ObjectPool(Func<T> objectGenerator, int initialCapacity = 0, Action<T>? resetAction = null)
        {
            _objectGenerator = objectGenerator ?? throw new ArgumentNullException(nameof(objectGenerator));
            _objects = new ConcurrentBag<T>();
            _resetAction = resetAction;

            for (int i = 0; i < initialCapacity; i++)
            {
                _objects.Add(_objectGenerator());
            }
        }

        public T Get()
        {
            if (_objects.TryTake(out T? item))
            {
                return item;
            }
            return _objectGenerator();
        }

        public void Return(T item)
        {
            if (item == null) return;

            _resetAction?.Invoke(item);
            _objects.Add(item);
        }
    }

    /// <summary>
    /// TroopRoster nesneleri için thread-safe object pool.
    /// GC pressure'ı azaltır ve performansı artırır.
    /// </summary>
    public static class TroopRosterPool
    {
        private static readonly ConcurrentBag<TroopRoster> _pool = new();
        private static int _created = 0;
        private static int _rented = 0;
        private static int _returned = 0;
        private const int MAX_POOL_SIZE = 50;
        private const int WARN_THRESHOLD = 1000;

        public static int Created => _created;
        public static int Rented => _rented;
        public static int Returned => _returned;
        public static int PoolSize => _pool.Count;

        /// <summary>
        /// Havuzdan bir TroopRoster al veya yeni oluştur
        /// </summary>
        public static TroopRoster Rent()
        {
            _ = Interlocked.Increment(ref _rented);

            if (_pool.TryTake(out var roster))
            {
                // Reset ve reuse - tüm askerleri temizle
                roster.Clear();
                return roster;
            }

            // Pool boş - yeni oluştur
            _ = Interlocked.Increment(ref _created);

            if (_created % WARN_THRESHOLD == 0 && Settings.Instance?.TestingMode == true)
            {
                DebugLogger.Warning("TroopRosterPool",
                    $"Pool sık sık yeni nesne oluşturuyor. Oluşturulan: {_created}, Havuzda: {_pool.Count}");
            }

            return TroopRoster.CreateDummyTroopRoster();
        }

        /// <summary>
        /// TroopRoster'ı havuza geri ver
        /// </summary>
        public static void Return(TroopRoster roster)
        {
            if (roster == null) return;

            if (_pool.Count >= MAX_POOL_SIZE)
            {
                // Havuz dolu - GC'ye bırak
                return;
            }

            try
            {
                // Temizle ve havuza ekle
                roster.Clear();
                _pool.Add(roster);
                _ = Interlocked.Increment(ref _returned);
            }
            catch
            {
                // Hata durumunda havuza ekleme
            }
        }

        /// <summary>
        /// Havuzu temizle (save/load veya mod unload sırasında)
        /// </summary>
        public static void Clear()
        {
            while (_pool.TryTake(out _)) { }
            _ = Interlocked.Exchange(ref _created, 0);
            _ = Interlocked.Exchange(ref _rented, 0);
            _ = Interlocked.Exchange(ref _returned, 0);
        }

        /// <summary>
        /// Pool istatistiklerini döndür
        /// </summary>
        public static string GetDiagnostics()
        {
            return $"TroopRosterPool[Created: {_created}, Rented: {_rented}, Returned: {_returned}, Pool: {_pool.Count}]";
        }
    }

    // ── MathUtils ─────────────────────────────────────────

    public static class MathUtils
    {
        public const float Epsilon = 1e-5f;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Clamp(float value, float min, float max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Clamp01(float value)
        {
            if (value < 0f) return 0f;
            if (value > 1f) return 1f;
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * Clamp01(t);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float DistanceSquared(float x1, float y1, float x2, float y2)
        {
            float dx = x2 - x1;
            float dy = y2 - y1;
            return dx * dx + dy * dy;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float DistanceSquared(Vec2 a, Vec2 b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }

        public static Vec2 SafeNormalize(Vec2 v)
        {
            float lengthSq = v.X * v.X + v.Y * v.Y;
            if (lengthSq < 1e-08f) return Vec2.Zero;
            float invLength = 1.0f / (float)Math.Sqrt(lengthSq);
            return new Vec2(v.X * invLength, v.Y * invLength);
        }

        public static int PoissonRandom(float lambda)
        {
            if (lambda <= 0f) return 0;

            if (lambda > 10f)
            {

                float result = GaussianRandom(lambda, (float)Math.Sqrt(lambda));

                int floorValue = (int)Math.Floor(result);
                float fraction = result - floorValue;
                if (MBRandom.RandomFloat < fraction)
                    floorValue++;

                return Math.Max(0, floorValue);
            }

            double L = Math.Exp(-lambda);
            int k = 0;
            double p = 1.0;

            do
            {
                k++;
                p *= MBRandom.RandomFloat;
            } while (p > L && k < 100);

            return k - 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float GaussianRandom(float mean, float stdDev)
        {
            float u1 = 1.0f - MBRandom.RandomFloat;
            float u2 = 1.0f - MBRandom.RandomFloat;
            float randStdNormal = (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2));
            return mean + stdDev * randStdNormal;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int WeightedRandom(float[] weights)
        {
            if (weights == null || weights.Length == 0) return -1;
            float totalWeight = 0f;
            for (int i = 0; i < weights.Length; i++) { if (weights[i] > 0) totalWeight += weights[i]; }

            if (totalWeight <= 0f) return 0;

            float randomValue = MBRandom.RandomFloat * totalWeight;
            float cumulative = 0f;
            for (int i = 0; i < weights.Length; i++)
            {
                float w = weights[i];
                if (w <= 0f) continue;
                cumulative += w;
                if (randomValue <= cumulative) return i;
            }
            return weights.Length - 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float FastInvSqrt(float x)
        {
            return (x <= 1e-06f) ? 0f : 1.0f / (float)Math.Sqrt(x);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsInRange(Vec2 p1, Vec2 p2, float range)
        {
            return DistanceSquared(p1, p2) <= range * range;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float SafeAtan2(float y, float x) => (float)Math.Atan2(y, x);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float SafeSqrt(float val) => (val < 0) ? 0 : (float)Math.Sqrt(val);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ApproximatelyEqual(float a, float b, float epsilon = 0.0001f)
        {
            return Math.Abs(a - b) < epsilon;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Remap(float value, float fromMin, float fromMax, float toMin, float toMax)
        {
            float t = (value - fromMin) / (fromMax - fromMin);
            return Lerp(toMin, toMax, t);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec2 ClampMagnitude(Vec2 vector, float maxLength)
        {
            float sqrLen = vector.X * vector.X + vector.Y * vector.Y;
            if (sqrLen > maxLength * maxLength)
            {
                float invLen = 1.0f / (float)Math.Sqrt(sqrLen);
                return new Vec2(vector.X * invLen * maxLength, vector.Y * invLen * maxLength);
            }
            return vector;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Sigmoid(float value)
        {
            float k = (float)Math.Exp(value);
            return k / (1.0f + k);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float GetAngleDifference(Vec2 a, Vec2 b)
        {
            float angA = (float)Math.Atan2(a.Y, a.X);
            float angB = (float)Math.Atan2(b.Y, b.X);
            float diff = angB - angA;

            while (diff > Math.PI) diff -= (float)(2 * Math.PI);
            while (diff < -Math.PI) diff += (float)(2 * Math.PI);

            return diff;
        }

    }

}