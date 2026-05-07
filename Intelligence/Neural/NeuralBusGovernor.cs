// Intelligence/Neural/NeuralBusGovernor.cs
//
// Thalamic traffic controller for EventBus.
// EventBus iki noktadan bağlanır:
//   1. Publish / PublishUntyped → ShouldSuppress()
//   2. ProcessQueue             → UpdateFrameMeasurement() + GetTimeBudgetMs()
//
// Başlatma : EventBus.Instance.SetGovernor(new NeuralBusGovernor());
// Kapatma  : EventBus.Instance.SetGovernor(null);
//

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using BanditMilitias.Core.Events;

namespace BanditMilitias.Intelligence.Neural
{
    // ── Opsiyonel arayüz ──────────────────────────────────────────────────────
    // Eventler bu arayüzü implemente ederse governor anlam tabanlı throttle uygular.
    // Implemente etmeyenlere sadece flood koruması ve starvation bypass uygulanır.
    // Mevcut: ThreatLevelChangedEvent, WarlordRivalryEscalatedEvent,
    //         WarlordAllianceFormedEvent, AdaptiveDoctrineShiftedEvent
    public interface ISemanticEvent
    {
        // Olayın "önem büyüklüğü" — genellikle abs(yeni - eski) gibi bir delta.
        float GetSignificanceDelta();
    }

    // ── Config ────────────────────────────────────────────────────────────────
    public sealed class GovernorConfig
    {
        // Semantik eşik: bu delta'nın altındaki Normal/Low olaylar bastırılır.
        public float SemanticMinDelta { get; set; } = 0.02f;

        // Flood: bir tip 1 saniyede bu kadar event üretirse Normal/Low bastırılır.
        public int FloodThresholdPerSecond { get; set; } = 40;

        // High-load'da flood eşiği bu değere düşer.
        public int FloodThresholdHighLoad { get; set; } = 20;

        // Starvation: bir tip bu kadar kez üst üste bastırıldıktan sonra serbest geçer.
        // ~40 event/sn flood eşiğinde → 60 bastırma ≈ ~1.5 saniye gecikme.
        public int StarvationBypassAfter { get; set; } = 60;

        // Adaptive budget sınırları (ms).
        public double BudgetMinMs { get; set; } = 1.5;
        public double BudgetMaxMs { get; set; } = 7.0;

        // ProcessQueue'nun bir frame'in yüzde kaçını kullanabileceği.
        public double BudgetFrameFraction { get; set; } = 0.14;

        // Flood sayacının sıfırlanma aralığı (gerçek zaman, saniye).
        public double FloodWindowSeconds { get; set; } = 1.0;
    }

    // ── Governor ──────────────────────────────────────────────────────────────
    public sealed class NeuralBusGovernor
    {
        private readonly GovernorConfig _cfg;

        // Flood tracking: tip → son pencere bilgisi
        private readonly ConcurrentDictionary<Type, FloodEntry> _flood = new();

        // Semantic tracking: tip → son geçirilen significance değeri
        private readonly ConcurrentDictionary<Type, float> _lastAllowedSig = new();

        // Starvation tracking: tip → üst üste bastırılma sayısı
        private readonly ConcurrentDictionary<Type, int> _suppressStreak = new();

        // Diagnostic counters
        private long _totalSuppressedFlood;
        private long _totalSuppressedSemantic;
        private long _totalStarvationBypasses;

        // Adaptive budget — ölçüm ve okuma ayrılmış
        private readonly Stopwatch _frameWatch = Stopwatch.StartNew();
        private double _lastFrameMs = 4.0;
        private double _cachedBudgetMs = 4.0;

        // High-load bayrağı — NervousSystem tarafından set edilir
        private volatile bool _isHighLoad;

        public NeuralBusGovernor(GovernorConfig? config = null)
        {
            _cfg = config ?? new GovernorConfig();
        }

        // NervousSystem.OnHourlyTick içinden çağrılır — flood eşiğini senkronize eder.
        public void SetHighLoad(bool highLoad) => _isHighLoad = highLoad;

        // ── Frame ölçümü ──────────────────────────────────────────────────────
        // ProcessQueue'nun başında çağrılır — sadece o noktada timer sıfırlanır.
        // GetTimeBudgetMs() yan etkisiz okuma olarak kalır.
        public void UpdateFrameMeasurement()
        {
            double frameMs = _frameWatch.Elapsed.TotalMilliseconds;
            _frameWatch.Restart();

            // Ani spike'ları yumuşat: %80 eski, %20 yeni (EWA)
            _lastFrameMs = _lastFrameMs * 0.8 + frameMs * 0.2;

            double budget = _lastFrameMs * _cfg.BudgetFrameFraction;
            _cachedBudgetMs = Math.Max(_cfg.BudgetMinMs, Math.Min(budget, _cfg.BudgetMaxMs));
        }

        // ProcessQueue tarafından çağrılır — saf okuma, yan etkisi yok.
        public double GetTimeBudgetMs() => _cachedBudgetMs;

        // ── Ana karar noktası ─────────────────────────────────────────────────
        // EventBus.Publish ve PublishUntyped tarafından çağrılır.
        // true  → eventi bastır
        // false → normal akışa devam et
        public bool ShouldSuppress(IGameEvent gameEvent)
        {
            if (gameEvent == null) return false;

            // Critical eventler hiçbir zaman bastırılmaz.
            if (gameEvent.Priority == EventPriority.Critical) return false;

            var type = gameEvent.GetType();

            // ── 1. Flood koruması (starvation'dan önce gelir) ─────────────────
            // Flood varsa: High geçer, Normal/Low bastırılır.
            // Flood olan bir modülün starvation bypass'ını kötüye kullanmaması için
            // bu kontrol starvation'dan önce yapılır.
            int floodThreshold = _isHighLoad
                ? _cfg.FloodThresholdHighLoad
                : _cfg.FloodThresholdPerSecond;

            if (IsFlooding(type, floodThreshold))
            {
                if (gameEvent.Priority > EventPriority.High) // Normal veya Low
                {
                    IncrementStreak(type);
                    System.Threading.Interlocked.Increment(ref _totalSuppressedFlood);
                    return true;
                }
                // High: flood sırasında geçer ama streak ilerler
                IncrementStreak(type);
                return false;
            }

            // ── 2. Starvation bypass ──────────────────────────────────────────
            // Flood yokken çok uzun süredir bastırılan tipler zorla geçer.
            if (_suppressStreak.TryGetValue(type, out int streak) &&
                streak >= _cfg.StarvationBypassAfter)
            {
                _suppressStreak[type] = 0;
                _lastAllowedSig.TryRemove(type, out _); // semantic baseline sıfırla
                System.Threading.Interlocked.Increment(ref _totalStarvationBypasses);
                return false; // serbest geçiş
            }

            // ── 3. Semantik throttle ──────────────────────────────────────────
            // Yalnızca Normal/Low ve ISemanticEvent implemente edenlere uygulanır.
            if (gameEvent.Priority > EventPriority.High &&
                gameEvent is ISemanticEvent semantic)
            {
                float sig = semantic.GetSignificanceDelta();
                if (_lastAllowedSig.TryGetValue(type, out float lastSig))
                {
                    if (Math.Abs(sig - lastSig) < _cfg.SemanticMinDelta)
                    {
                        IncrementStreak(type);
                        System.Threading.Interlocked.Increment(ref _totalSuppressedSemantic);
                        return true;
                    }
                }
                _lastAllowedSig[type] = sig;
            }

            // Geçti → streak sıfırla
            _suppressStreak.TryRemove(type, out _);
            return false;
        }

        // Session bitişinde state'i sıfırla — Governor yeni oturuma kirli state taşımasın.
        public void Reset()
        {
            _flood.Clear();
            _lastAllowedSig.Clear();
            _suppressStreak.Clear();
            System.Threading.Interlocked.Exchange(ref _totalSuppressedFlood, 0);
            System.Threading.Interlocked.Exchange(ref _totalSuppressedSemantic, 0);
            System.Threading.Interlocked.Exchange(ref _totalStarvationBypasses, 0);
            _lastFrameMs   = 4.0;
            _cachedBudgetMs = 4.0;
            _isHighLoad    = false;
            _frameWatch.Restart();
        }

        // ── Diagnostics ───────────────────────────────────────────────────────
        public string GetDiagnostics()
        {
            long flood    = System.Threading.Interlocked.Read(ref _totalSuppressedFlood);
            long semantic = System.Threading.Interlocked.Read(ref _totalSuppressedSemantic);
            long bypass   = System.Threading.Interlocked.Read(ref _totalStarvationBypasses);
            // _cachedBudgetMs saf okuma — timer'a dokunmaz
            return $"[Governor] bastırılan: flood={flood} semantik={semantic} | " +
                   $"starvation_bypass={bypass} | " +
                   $"frameBudget={_cachedBudgetMs:F1}ms | " +
                   $"highLoad={_isHighLoad}";
        }

        // ── Yardımcı metodlar ─────────────────────────────────────────────────
        private bool IsFlooding(Type type, int threshold)
        {
            double nowSec = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

            // AddOrUpdate atomik güncelleme — sahte while(true) CAS döngüsü yok
            _flood.AddOrUpdate(
                type,
                _ => new FloodEntry { WindowStart = nowSec, Count = 1 },
                (_, cur) =>
                {
                    if (nowSec - cur.WindowStart >= _cfg.FloodWindowSeconds)
                        return new FloodEntry { WindowStart = nowSec, Count = 1 };
                    return new FloodEntry { WindowStart = cur.WindowStart, Count = cur.Count + 1 };
                });

            var result = _flood[type];
            return result.Count > threshold
                && nowSec - result.WindowStart < _cfg.FloodWindowSeconds;
        }

        private void IncrementStreak(Type type)
            => _suppressStreak.AddOrUpdate(type, 1, (_, v) => v + 1);

        // Struct — ConcurrentDictionary ile value-copy semantiği güvenli.
        private struct FloodEntry
        {
            public double WindowStart;
            public int    Count;
        }
    }
}
