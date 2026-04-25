using System;
using System.Collections.Generic;
using System.Text;

namespace BanditMilitias.Intelligence.Neural
{
    // ═══════════════════════════════════════════════════════════════════
    //  EXPERIENCE BUFFER — Ring Buffer Deneyim Deposu
    //
    //  Oyun içi karar → sonuç verilerini toplar.
    //  Offline eğitim için mini-batch sampling sağlar.
    //  CSV export ile DevDataCollector entegrasyonu.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Tek bir deneyim: durum → aksiyon → ödül → sonraki durum.
    /// </summary>
    public struct Experience
    {
        /// <summary>Karar anındaki durum (featurelar).</summary>
        public float[] StateFeatures;
        /// <summary>Seçilen aksiyon indeksi.</summary>
        public int ActionTaken;
        /// <summary>Sonuç ödülü (-1..+1).</summary>
        public float Reward;
        /// <summary>Aksiyondan sonraki durum.</summary>
        public float[] NextStateFeatures;
        /// <summary>Deneyim zaman damgası (game hours).</summary>
        public double Timestamp;
        /// <summary>Kaynağı belirten etiket (debug).</summary>
        public string Source;
    }

    /// <summary>
    /// Ödül fonksiyonu hesaplayıcısı.
    /// Farklı oyun olaylarını -1..+1 arası ödüle çevirir.
    /// </summary>
    public static class RewardFunction
    {
        // ── Savaş Sonuçları ──────────────────────────────────
        public const float BATTLE_WON = 1.0f;
        public const float BATTLE_LOST = -0.5f;
        public const float BATTLE_WON_UNDERDOG = 1.5f; // Güçsüz tarafın kazanması

        // ── Ekonomik Sonuçlar ────────────────────────────────
        public const float RAID_SUCCESS = 0.7f;
        public const float RAID_FAILED = -0.3f;
        public const float EXTORT_SUCCESS = 0.5f;
        public const float EXTORT_FAILED = -0.2f;
        public const float GOLD_GAINED = 0.3f;
        public const float GOLD_LOST = -0.2f;

        // ── Kariyer Sonuçları ────────────────────────────────
        public const float TIER_PROMOTED = 1.0f;
        public const float PARTY_GREW = 0.2f;
        public const float PARTY_SHRANK = -0.3f;
        public const float PARTY_DISBANDED = -1.0f;

        // ── Taktik Sonuçlar ──────────────────────────────────
        public const float RETREAT_SURVIVED = 0.4f;
        public const float AMBUSH_CAUGHT = -0.6f;
        public const float PATROL_SAFE = 0.1f;
        public const float DEFEND_HELD = 0.5f;

        /// <summary>
        /// Savaş sonucunu ödüle çevirir.
        /// </summary>
        public static float CalculateBattleReward(bool won, float ownStrength, float enemyStrength, float casualtyRatio)
        {
            float baseReward = won ? BATTLE_WON : BATTLE_LOST;

            // Underdog bonusu: düşman 1.5x daha güçlüyse
            if (won && enemyStrength > ownStrength * 1.5f)
            {
                float underdogMul = Math.Min(2.0f, enemyStrength / Math.Max(1f, ownStrength));
                baseReward *= underdogMul;
            }

            // Kayıp oranı cezası: çok kayıp verdiyse ödül azalır
            if (won && casualtyRatio > 0.5f)
            {
                baseReward *= (1f - casualtyRatio * 0.3f);
            }

            return Clamp(baseReward, -1.5f, 1.5f);
        }

        /// <summary>
        /// Ekonomik sonucu ödüle çevirir.
        /// </summary>
        public static float CalculateEconomicReward(bool success, int goldChange, int totalGold)
        {
            float baseReward = success ? RAID_SUCCESS : RAID_FAILED;

            // Yüksek kazanç bonusu
            if (success && goldChange > 0 && totalGold > 0)
            {
                float relativeGain = (float)goldChange / Math.Max(1f, totalGold);
                baseReward += Math.Min(0.3f, relativeGain);
            }

            return Clamp(baseReward, -1.0f, 1.0f);
        }

        private static float Clamp(float v, float min, float max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }
    }

    /// <summary>
    /// Sabit kapasiteli circular (ring) buffer.
    /// Dolduğunda en eski deneyimlerin üzerine yazar.
    /// </summary>
    public class ExperienceBuffer
    {
        private readonly Experience[] _buffer;
        private int _writeIndex;
        private int _count;
        private readonly object _lock = new object();

        public int Capacity { get; }
        public int Count { get { lock (_lock) return _count; } }
        public bool IsFull { get { lock (_lock) return _count >= Capacity; } }

        // İstatistikler
        public int TotalExperiencesAdded { get; private set; }
        public float AverageReward { get; private set; }
        private float _rewardSum;

        public ExperienceBuffer(int capacity = 5000)
        {
            Capacity = capacity;
            _buffer = new Experience[capacity];
            _writeIndex = 0;
            _count = 0;
        }

        /// <summary>
        /// Yeni deneyim ekle. Buffer doluysa en eskinin üzerine yazar.
        /// </summary>
        public void Add(Experience experience)
        {
            lock (_lock)
            {
                _buffer[_writeIndex] = experience;
                _writeIndex = (_writeIndex + 1) % Capacity;
                if (_count < Capacity) _count++;

                TotalExperiencesAdded++;
                _rewardSum += experience.Reward;
                AverageReward = _rewardSum / TotalExperiencesAdded;
            }
        }

        /// <summary>
        /// Rastgele mini-batch seç (eğitim için).
        /// Thread-safe.
        /// </summary>
        public Experience[] SampleBatch(int batchSize, Random? rng = null)
        {
            lock (_lock)
            {
                if (_count == 0) return Array.Empty<Experience>();
                if (batchSize > _count) batchSize = _count;

                rng = rng ?? new Random();
                var batch = new Experience[batchSize];
                var indices = new HashSet<int>();

                while (indices.Count < batchSize)
                {
                    indices.Add(rng.Next(_count));
                }

                int idx = 0;
                foreach (int i in indices)
                {
                    // Ring buffer'da gerçek indeks hesaplama
                    int realIndex = _count < Capacity ? i : (_writeIndex + i) % Capacity;
                    batch[idx++] = _buffer[realIndex];
                }

                return batch;
            }
        }

        /// <summary>
        /// Son N deneyimi al (analiz için).
        /// </summary>
        public Experience[] GetRecent(int count)
        {
            lock (_lock)
            {
                if (_count == 0) return Array.Empty<Experience>();
                if (count > _count) count = _count;

                var result = new Experience[count];
                for (int i = 0; i < count; i++)
                {
                    int idx = (_writeIndex - 1 - i + Capacity) % Capacity;
                    result[i] = _buffer[idx];
                }
                return result;
            }
        }

        /// <summary>
        /// Buffer'ı tamamen temizle.
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _writeIndex = 0;
                _count = 0;
                TotalExperiencesAdded = 0;
                _rewardSum = 0f;
                AverageReward = 0f;
            }
        }

        /// <summary>
        /// Tüm deneyimleri CSV formatında döndür.
        /// </summary>
        public string ToCsv()
        {
            lock (_lock)
            {
                var sb = new StringBuilder();
                sb.AppendLine("Timestamp,ActionTaken,Reward,Source,Features");

                for (int i = 0; i < _count; i++)
                {
                    int idx = _count < Capacity ? i : (_writeIndex + i) % Capacity;
                    var exp = _buffer[idx];

                    string features = exp.StateFeatures != null
                        ? string.Join(";", exp.StateFeatures)
                        : "";

                    sb.AppendLine($"{exp.Timestamp:F1},{exp.ActionTaken},{exp.Reward:F3}," +
                                 $"\"{exp.Source ?? ""}\",\"{features}\"");
                }

                return sb.ToString();
            }
        }

        public string GetDiagnostics()
        {
            lock (_lock)
            {
                return $"ExperienceBuffer:\n" +
                       $"  Capacity: {Capacity}\n" +
                       $"  Count: {_count} ({(_count * 100f / Capacity):F1}%)\n" +
                       $"  Total Added: {TotalExperiencesAdded}\n" +
                       $"  Average Reward: {AverageReward:F3}";
            }
        }
    }
}
