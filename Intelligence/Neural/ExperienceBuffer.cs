using System;
using System.Collections.Generic;
using System.Text;

namespace BanditMilitias.Intelligence.Neural
{


    public struct Experience
    {


        public float[] StateFeatures;


        public int ActionTaken;


        public float Reward;


        public float[] NextStateFeatures;


        public double Timestamp;


        public string Source;
    }


    public static class RewardFunction
    {


        public const float BATTLE_WON = 1.0f;
        public const float BATTLE_LOST = -0.5f;
        public const float BATTLE_WON_UNDERDOG = 1.5f;


        public const float RAID_SUCCESS = 0.7f;
        public const float RAID_FAILED = -0.3f;
        public const float EXTORT_SUCCESS = 0.5f;
        public const float EXTORT_FAILED = -0.2f;
        public const float GOLD_GAINED = 0.3f;
        public const float GOLD_LOST = -0.2f;


        public const float TIER_PROMOTED = 1.0f;
        public const float PARTY_GREW = 0.2f;
        public const float PARTY_SHRANK = -0.3f;
        public const float PARTY_DISBANDED = -1.0f;


        public const float RETREAT_SURVIVED = 0.4f;
        public const float AMBUSH_CAUGHT = -0.6f;
        public const float PATROL_SAFE = 0.1f;
        public const float DEFEND_HELD = 0.5f;


        public static float CalculateBattleReward(bool won, float ownStrength, float enemyStrength, float casualtyRatio)
        {
            float baseReward = won ? BATTLE_WON : BATTLE_LOST;


            if (won && enemyStrength > ownStrength * 1.5f)
            {
                float underdogMul = Math.Min(2.0f, enemyStrength / Math.Max(1f, ownStrength));
                baseReward *= underdogMul;
            }


            if (won && casualtyRatio > 0.5f)
            {
                baseReward *= (1f - casualtyRatio * 0.3f);
            }

            return Clamp(baseReward, -1.5f, 1.5f);
        }


        public static float CalculateEconomicReward(bool success, int goldChange, int totalGold)
        {
            float baseReward = success ? RAID_SUCCESS : RAID_FAILED;


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


    public class ExperienceBuffer
    {
        private readonly Experience[] _buffer;
        private int _writeIndex;
        private int _count;
        private readonly object _lock = new object();

        public int Capacity { get; }
        public int Count { get { lock (_lock) return _count; } }
        public bool IsFull { get { lock (_lock) return _count >= Capacity; } }


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


                    int realIndex = _count < Capacity ? i : (_writeIndex + i) % Capacity;
                    batch[idx++] = _buffer[realIndex];
                }

                return batch;
            }
        }


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
