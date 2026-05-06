using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.Progression;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BanditMilitias.Intelligence.Neural
{


    public struct NeuralAdvice
    {


        public float[] ActionProbabilities;


        public float Confidence;


        public int RecommendedAction;


        public bool IsValid;
    }


    public static class NeuralActionMap
    {


        public static readonly CommandType[] IndexToCommand = new[]
        {
            CommandType.Patrol,

            CommandType.Ambush,

            CommandType.CommandRaidVillage,

            CommandType.Hunt,

            CommandType.Defend,

            CommandType.Retreat,

            CommandType.CommandExtort,

            CommandType.Engage

        };

        public static int CommandToIndex(CommandType cmd)
        {
            for (int i = 0; i < IndexToCommand.Length; i++)
            {
                if (IndexToCommand[i] == cmd) return i;
            }
            return 0;

        }

        public static readonly int ActionCount = IndexToCommand.Length;
    }


    public class NeuralAdvisor
    {
        private static NeuralAdvisor? _instance;
        public static NeuralAdvisor? Instance => _instance;


        private FeedForwardNetwork _strategicNet = null!;


        private ExperienceBuffer _experienceBuffer = null!;


        private bool _isEnabled;
        private bool _isInitialized;


        private int _inferencesThisTick;
        private int _maxInferencesPerTick = 50;


        public int TotalInferences { get; private set; }
        public int TotalTrainingBatches { get; private set; }
        public float LastTrainingLoss { get; private set; }
        public float GlobalConfidence { get; private set; }


        private string _weightsDir = null!;
        private const string WEIGHTS_FILENAME = "neural_weights_strategic.json";


        private static readonly Dictionary<CareerTier, float> TierConfidenceCaps = new()
        {
            { CareerTier.Outlaw, 0.00f },
            { CareerTier.Rebel, 0.00f },
            { CareerTier.FamousBandit, 0.00f },
            { CareerTier.Warlord, 0.30f },
            { CareerTier.Recognized, 0.60f },
            { CareerTier.Conqueror, 1.00f }
        };


        public const int STRATEGIC_INPUT_SIZE = 12;
        public const int STRATEGIC_OUTPUT_SIZE = 8;


        private NeuralAdvisor() { }


        public static NeuralAdvisor CreateInstance()
        {
            if (_instance != null) return _instance;
            _instance = new NeuralAdvisor();
            return _instance;
        }


        public void Initialize(string weightsDir, bool enabled)
        {
            if (_isInitialized) return;

            _weightsDir = weightsDir;
            _isEnabled = enabled;


            _strategicNet = new FeedForwardNetwork(
                new[] { STRATEGIC_INPUT_SIZE, 24, 16, STRATEGIC_OUTPUT_SIZE },
                seed: 42

            );


            _experienceBuffer = new ExperienceBuffer(5000);


            TryLoadWeights();


            if (_strategicNet.TotalForwardPasses == 0)
            {
                ApplyPretrainedWeights();
            }

            _isInitialized = true;

            DebugLogger.Info("NeuralAdvisor",
                $"Initialized | Enabled={_isEnabled} | " +
                $"Params={_strategicNet.GetParameterCount()} | " +
                $"Buffer={_experienceBuffer.Capacity}");
        }

        private bool HasEligibleWarlord()
        {
            try
            {
                var warlordSystem = WarlordSystem.Instance;
                var careerSystem = WarlordCareerSystem.Instance;
                if (warlordSystem == null || careerSystem == null)
                    return false;

                return warlordSystem.GetAllWarlords().Any(w =>
                    w != null &&
                    w.IsAlive &&
                    careerSystem.GetTier(w.StringId) >= CareerTier.Warlord);
            }
            catch
            {
                return false;
            }
        }

        public void Cleanup()
        {
            if (!_isInitialized) return;


            if (_isEnabled && _strategicNet.TotalBackwardPasses > 0)
            {
                TrySaveWeights();
            }

            _isInitialized = false;
            _instance = null;
        }


        public NeuralAdvice GetStrategicAdvice(float[] stateFeatures, CareerTier warlordTier)
        {
            var advice = new NeuralAdvice { IsValid = false };


            if (!IsOperational) return advice;


            if (!TierConfidenceCaps.TryGetValue(warlordTier, out float maxConfidence))
                return advice;
            if (maxConfidence <= 0f) return advice;


            if (_inferencesThisTick >= _maxInferencesPerTick)
                return advice;


            if (stateFeatures == null || stateFeatures.Length != STRATEGIC_INPUT_SIZE)
                return advice;

            try
            {


                float[] output = _strategicNet.Forward(stateFeatures);


                advice.ActionProbabilities = new float[output.Length];
                Array.Copy(output, advice.ActionProbabilities, output.Length);
                advice.Confidence = Math.Min(maxConfidence, GlobalConfidence);
                advice.RecommendedAction = ArgMax(output);
                advice.IsValid = true;

                _inferencesThisTick++;
                TotalInferences++;
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("NeuralAdvisor", $"Inference failed: {ex.Message}");
            }

            return advice;
        }


        public static float BlendWithHeuristic(float heuristicScore, NeuralAdvice advice, int actionIndex)
        {
            if (!advice.IsValid || advice.ActionProbabilities == null)
                return heuristicScore;

            if (actionIndex < 0 || actionIndex >= advice.ActionProbabilities.Length)
                return heuristicScore;

            float neuralScore = advice.ActionProbabilities[actionIndex];
            float confidence = advice.Confidence;


            float blended = (1f - confidence) * heuristicScore +
                            confidence * (neuralScore * heuristicScore * 2f);

            return Math.Max(0f, blended);
        }


        public void OnTickReset()
        {
            _inferencesThisTick = 0;
        }


        public void SetMaxInferencesPerTick(int max)
        {
            if (max <= 0)
            {
                _maxInferencesPerTick = 0;
                return;
            }

            _maxInferencesPerTick = Math.Max(5, Math.Min(200, max));
        }


        public void RecordExperience(float[] stateFeatures, int actionTaken,
            float reward, float[] nextStateFeatures, string source = "")
        {
            if (!_isEnabled || !_isInitialized) return;

            _experienceBuffer.Add(new Experience
            {
                StateFeatures = stateFeatures,
                ActionTaken = actionTaken,
                Reward = reward,
                NextStateFeatures = nextStateFeatures,
                Timestamp = TaleWorlds.CampaignSystem.CampaignTime.Now.ToHours,
                Source = source
            });
        }


        public string TrainOffline(int numBatches, int batchSize = 32, float learningRate = 0.01f)
        {
            if (!_isInitialized || _experienceBuffer.Count < batchSize)
            {
                return $"[Neural] Yetersiz veri. Buffer: {_experienceBuffer.Count}/{batchSize} gerekli.";
            }

            var rng = new Random();
            float totalLoss = 0f;
            int totalSamples = 0;

            for (int batch = 0; batch < numBatches; batch++)
            {
                var experiences = _experienceBuffer.SampleBatch(batchSize, rng);

                foreach (var exp in experiences)
                {
                    if (exp.StateFeatures == null || exp.StateFeatures.Length != STRATEGIC_INPUT_SIZE)
                        continue;


                    _strategicNet.Forward(exp.StateFeatures);


                    float[] target = new float[STRATEGIC_OUTPUT_SIZE];
                    Array.Copy(_strategicNet.Layers[_strategicNet.Layers.Length - 1].LastOutput,
                               target, STRATEGIC_OUTPUT_SIZE);


                    if (exp.ActionTaken >= 0 && exp.ActionTaken < STRATEGIC_OUTPUT_SIZE)
                    {


                        if (exp.Reward > 0)
                        {


                            float boost = Math.Min(0.8f, exp.Reward * 0.5f);
                            for (int i = 0; i < STRATEGIC_OUTPUT_SIZE; i++)
                            {
                                target[i] = (i == exp.ActionTaken)
                                    ? target[i] + boost * (1f - target[i])
                                    : target[i] * (1f - boost);
                            }
                        }
                        else
                        {


                            float penalty = Math.Min(0.5f, Math.Abs(exp.Reward) * 0.3f);
                            float redistributed = target[exp.ActionTaken] * penalty / (STRATEGIC_OUTPUT_SIZE - 1);
                            target[exp.ActionTaken] *= (1f - penalty);
                            for (int i = 0; i < STRATEGIC_OUTPUT_SIZE; i++)
                            {
                                if (i != exp.ActionTaken) target[i] += redistributed;
                            }
                        }


                        NormalizeProbabilities(target);
                    }

                    float loss = _strategicNet.Backpropagate(target, learningRate);
                    totalLoss += loss;
                    totalSamples++;
                }
            }

            TotalTrainingBatches += numBatches;
            LastTrainingLoss = totalSamples > 0 ? totalLoss / totalSamples : 0f;


            UpdateGlobalConfidence();


            TrySaveWeights();

            return $"[Neural] Eğitim tamamlandı:\n" +
                   $"  Batch: {numBatches} × {batchSize}\n" +
                   $"  Samples: {totalSamples}\n" +
                   $"  Avg Loss: {LastTrainingLoss:F4}\n" +
                   $"  Confidence: {GlobalConfidence:F2}\n" +
                   $"  Total Forward: {_strategicNet.TotalForwardPasses}\n" +
                   $"  Total Backward: {_strategicNet.TotalBackwardPasses}";
        }


        public static float[] ExtractFeatures(StrategicContext ctx, Warlord warlord)
        {
            float[] features = new float[STRATEGIC_INPUT_SIZE];

            features[0] = Normalize(ctx.OwnCombatPower, 0f, 5000f);
            features[1] = Normalize(ctx.EnemyCombatPower, 0f, 5000f);
            features[2] = ctx.EnemyCombatPower > 0
                ? Math.Min(3f, ctx.OwnCombatPower / Math.Max(1f, ctx.EnemyCombatPower))
                : 1f;
            features[3] = Math.Min(1f, ctx.ThreatLevel);
            features[4] = Math.Min(1f, ctx.AverageRegionFear);
            features[5] = Normalize(ctx.WarlordGold, 0f, 50000f);


            int totalTroops = 0;
            float avgTier = 0f;
            if (warlord?.CommandedMilitias != null)
            {
                foreach (var militia in warlord.CommandedMilitias)
                {
                    if (militia?.MemberRoster == null || !militia.IsActive) continue;
                    totalTroops += militia.MemberRoster.TotalManCount;
                    for (int i = 0; i < militia.MemberRoster.Count; i++)
                    {
                        var el = militia.MemberRoster.GetElementCopyAtIndex(i);
                        if (el.Character != null)
                            avgTier += el.Character.Tier * el.Number;
                    }
                }
                if (totalTroops > 0) avgTier /= totalTroops;
            }

            features[6] = Normalize(totalTroops, 0f, 200f);
            features[7] = Normalize(avgTier, 0f, 6f);
            features[8] = Normalize((int)ctx.WarlordLevel, 0f, 4f);

            features[9] = ctx.HasActiveHunter ? 1f : 0f;

            features[10] = Normalize(ctx.WarlordBounty, 0f, 5000f);


            float playerDist = 100f;
            try
            {
                var mainParty = TaleWorlds.CampaignSystem.Party.MobileParty.MainParty;
                if (mainParty != null && warlord?.AssignedHideout != null)
                {
                    playerDist = CompatibilityLayer.GetPartyPosition(mainParty)
                        .Distance(CompatibilityLayer.GetSettlementPosition(warlord.AssignedHideout));
                }
            }
            catch { }
            features[11] = Normalize(playerDist, 0f, 100f);

            return features;
        }


        private static float Normalize(float value, float min, float max)
        {
            if (max <= min) return 0f;
            return Math.Max(0f, Math.Min(1f, (value - min) / (max - min)));
        }

        private static int ArgMax(float[] values)
        {
            int best = 0;
            float bestVal = float.MinValue;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] > bestVal)
                {
                    bestVal = values[i];
                    best = i;
                }
            }
            return best;
        }

        private static void NormalizeProbabilities(float[] probs)
        {
            float sum = 0f;
            for (int i = 0; i < probs.Length; i++)
            {
                probs[i] = Math.Max(0.001f, probs[i]);
                sum += probs[i];
            }
            if (sum > 0f)
            {
                float inv = 1f / sum;
                for (int i = 0; i < probs.Length; i++)
                    probs[i] *= inv;
            }
        }


        private void UpdateGlobalConfidence()
        {


            GlobalConfidence = (float)(1.0 - Math.Exp(-TotalTrainingBatches / 100.0));
            GlobalConfidence = Math.Min(1.0f, GlobalConfidence);
        }


        private void ApplyPretrainedWeights()
        {


            var syntheticExperiences = GenerateSyntheticExperiences();
            var rng = new Random(42);

            for (int epoch = 0; epoch < 5; epoch++)
            {
                foreach (var exp in syntheticExperiences)
                {
                    _strategicNet.Forward(exp.StateFeatures);

                    float[] target = new float[STRATEGIC_OUTPUT_SIZE];
                    target[exp.ActionTaken] = 0.7f;
                    float remaining = 0.3f / (STRATEGIC_OUTPUT_SIZE - 1);
                    for (int i = 0; i < STRATEGIC_OUTPUT_SIZE; i++)
                    {
                        if (i != exp.ActionTaken) target[i] = remaining;
                    }

                    _strategicNet.Backpropagate(target, 0.05f);
                }
            }

            _strategicNet.ResetStats();

            GlobalConfidence = 0.1f;


            DebugLogger.Info("NeuralAdvisor", $"Pre-trained with {syntheticExperiences.Count} synthetic samples");
        }


        private List<Experience> GenerateSyntheticExperiences()
        {
            var experiences = new List<Experience>();
            var rng = new Random(42);


            AddSyntheticScenario(experiences,
                power: 0.8f, enemyPower: 0.3f, threat: 0.1f, fear: 0.6f,
                gold: 0.5f, troops: 0.7f, tier: 0.6f,
                bestAction: NeuralActionMap.CommandToIndex(CommandType.CommandRaidVillage));


            AddSyntheticScenario(experiences,
                power: 0.2f, enemyPower: 0.8f, threat: 0.9f, fear: 0.2f,
                gold: 0.1f, troops: 0.2f, tier: 0.4f,
                bestAction: NeuralActionMap.CommandToIndex(CommandType.Retreat));


            AddSyntheticScenario(experiences,
                power: 0.5f, enemyPower: 0.5f, threat: 0.5f, fear: 0.4f,
                gold: 0.4f, troops: 0.5f, tier: 0.4f,
                bestAction: NeuralActionMap.CommandToIndex(CommandType.Ambush));


            AddSyntheticScenario(experiences,
                power: 0.5f, enemyPower: 0.4f, threat: 0.3f, fear: 0.7f,
                gold: 0.8f, troops: 0.6f, tier: 0.6f,
                bestAction: NeuralActionMap.CommandToIndex(CommandType.CommandExtort));


            AddSyntheticScenario(experiences,
                power: 0.9f, enemyPower: 0.6f, threat: 0.4f, fear: 0.8f,
                gold: 0.7f, troops: 0.9f, tier: 0.8f,
                bestAction: NeuralActionMap.CommandToIndex(CommandType.Hunt));


            AddSyntheticScenario(experiences,
                power: 0.6f, enemyPower: 0.7f, threat: 0.8f, fear: 0.3f,
                gold: 0.3f, troops: 0.5f, tier: 0.4f,
                bestAction: NeuralActionMap.CommandToIndex(CommandType.Defend));


            AddSyntheticScenario(experiences,
                power: 0.3f, enemyPower: 0.3f, threat: 0.2f, fear: 0.1f,
                gold: 0.1f, troops: 0.3f, tier: 0.2f,
                bestAction: NeuralActionMap.CommandToIndex(CommandType.Patrol));


            AddSyntheticScenario(experiences,
                power: 1.0f, enemyPower: 0.5f, threat: 0.3f, fear: 0.9f,
                gold: 0.9f, troops: 1.0f, tier: 1.0f,
                bestAction: NeuralActionMap.CommandToIndex(CommandType.Engage));


            AddSyntheticScenario(experiences,
                power: 0.25f, enemyPower: 0.20f, threat: 0.15f, fear: 0.05f,
                gold: 0.08f, troops: 0.22f, tier: 0.15f,
                bestAction: NeuralActionMap.CommandToIndex(CommandType.CommandRaidVillage));


            AddSyntheticScenario(experiences,
                power: 0.40f, enemyPower: 0.25f, threat: 0.10f, fear: 0.10f,
                gold: 0.12f, troops: 0.38f, tier: 0.20f,
                bestAction: NeuralActionMap.CommandToIndex(CommandType.CommandExtort));


            AddSyntheticScenario(experiences,
                power: 0.18f, enemyPower: 0.75f, threat: 0.85f, fear: 0.05f,
                gold: 0.06f, troops: 0.15f, tier: 0.10f,
                bestAction: NeuralActionMap.CommandToIndex(CommandType.Retreat));


            var augmented = new List<Experience>();
            foreach (var exp in experiences)
            {
                augmented.Add(exp);
                for (int v = 0; v < 3; v++)
                {
                    var noisy = exp;
                    noisy.StateFeatures = new float[STRATEGIC_INPUT_SIZE];
                    Array.Copy(exp.StateFeatures, noisy.StateFeatures, STRATEGIC_INPUT_SIZE);
                    for (int f = 0; f < STRATEGIC_INPUT_SIZE; f++)
                    {
                        noisy.StateFeatures[f] += (float)(rng.NextDouble() - 0.5) * 0.15f;
                        noisy.StateFeatures[f] = Math.Max(0f, Math.Min(1f, noisy.StateFeatures[f]));
                    }
                    augmented.Add(noisy);
                }
            }

            return augmented;
        }

        private static void AddSyntheticScenario(List<Experience> list,
            float power, float enemyPower, float threat, float fear,
            float gold, float troops, float tier, int bestAction,
            float bounty = 0.2f, float hasHunter = 0f, float playerDist = 0.5f)
        {
            list.Add(new Experience
            {
                StateFeatures = new float[]
                {
                    power, enemyPower,
                    enemyPower > 0 ? power / enemyPower : 1f,
                    threat, fear, gold, troops,
                    tier,

                    tier,

                    hasHunter,

                    bounty,

                    playerDist

                },
                ActionTaken = bestAction,
                Reward = 1.0f,
                Source = "synthetic"
            });
        }


        private void TryLoadWeights()
        {
            if (string.IsNullOrEmpty(_weightsDir)) return;
            string path = Path.Combine(_weightsDir, WEIGHTS_FILENAME);
            if (!File.Exists(path)) return;

            try
            {
                string json = File.ReadAllText(path);
                if (_strategicNet.DeserializeWeights(json))
                {
                    DebugLogger.Info("NeuralAdvisor", $"Weights loaded from {path}");
                }
                else
                {
                    DebugLogger.Warning("NeuralAdvisor", "Weight file format mismatch, using fresh weights");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("NeuralAdvisor", $"Failed to load weights: {ex.Message}");
            }
        }

        public void TrySaveWeights()
        {
            if (string.IsNullOrEmpty(_weightsDir) || _strategicNet == null) return;

            try
            {
                Directory.CreateDirectory(_weightsDir);
                string path = Path.Combine(_weightsDir, WEIGHTS_FILENAME);
                string json = _strategicNet.SerializeWeights();
                File.WriteAllText(path, json, System.Text.Encoding.UTF8);

                DebugLogger.Info("NeuralAdvisor", $"Weights saved to {path}");
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("NeuralAdvisor", $"Failed to save weights: {ex.Message}");
            }
        }


        public void SetEnabled(bool enabled)
        {
            _isEnabled = enabled;
        }

        public bool IsEnabled => _isEnabled && _isInitialized;
        public bool IsOperational => IsEnabled && _maxInferencesPerTick > 0 && HasEligibleWarlord();

        public ExperienceBuffer GetExperienceBuffer() => _experienceBuffer;

        public string GetDiagnostics()
        {
            if (!_isInitialized)
                return "[NeuralAdvisor] Not initialized";

            string sleepReason = !_isEnabled
                ? "disabled"
                : !HasEligibleWarlord()
                    ? "no eligible warlord"
                    : _maxInferencesPerTick <= 0
                        ? "budget suspended"
                        : "active";

            return $"[NeuralAdvisor]\n" +
                   $"  Enabled: {_isEnabled}\n" +
                   $"  Operational: {IsOperational} ({sleepReason})\n" +
                   $"  Network: {_strategicNet?.GetDiagnostics() ?? "null"}\n" +
                   $"  Confidence: {GlobalConfidence:F2}\n" +
                   $"  Inferences: {TotalInferences}\n" +
                   $"  Training Batches: {TotalTrainingBatches}\n" +
                   $"  Last Loss: {LastTrainingLoss:F4}\n" +
                   $"  Budget: {(IsOperational ? _inferencesThisTick : 0)}/{_maxInferencesPerTick} per tick\n" +
                   $"  {_experienceBuffer?.GetDiagnostics() ?? "no buffer"}\n" +
                   $"  Weights Dir: {_weightsDir ?? "not set"}";
        }
    }
}
