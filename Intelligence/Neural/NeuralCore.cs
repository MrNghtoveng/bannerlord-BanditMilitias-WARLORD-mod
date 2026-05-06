using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;

namespace BanditMilitias.Intelligence.Neural
{
    // ═══════════════════════════════════════════════════════════════════
    //  NEURAL CORE — El yazımı Feed-Forward Sinir Ağı Motoru
    //
    //  Sıfır dış bağımlılık (.NET 4.7.2 uyumlu).
    //  Hafif MLP: inference ~0.005ms, backward ~0.02ms per pass.
    //  GC-free hot path: float[] tabanlı, boxing yok.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Aktivasyon fonksiyonu türleri.
    /// </summary>
    public enum ActivationType
    {
        ReLU,
        Sigmoid,
        Tanh,
        Softmax,  // Sadece çıkış katmanı için
        Linear
    }

    /// <summary>
    /// Tek bir dense (tam bağlantılı) katman.
    /// weights[outputSize][inputSize], bias[outputSize]
    /// </summary>
    public class NeuralLayer
    {
        public float[][] Weights;
        public float[] Bias;
        public ActivationType Activation;

        // Forward pass önbelleği (backpropagation için)
        internal float[] LastInput;
        internal float[] LastPreActivation;
        internal float[] LastOutput;

        public int InputSize => Weights[0].Length;
        public int OutputSize => Weights.Length;

        public NeuralLayer(int inputSize, int outputSize, ActivationType activation)
        {
            Weights = new float[outputSize][];
            Bias = new float[outputSize];
            Activation = activation;

            LastInput = new float[inputSize];
            LastPreActivation = new float[outputSize];
            LastOutput = new float[outputSize];

            for (int o = 0; o < outputSize; o++)
            {
                Weights[o] = new float[inputSize];
            }
        }

        /// <summary>
        /// He (Kaiming) initialization — ReLU/Leaky ağlar için ideal.
        /// </summary>
        public void InitializeHe(Random rng)
        {
            float stddev = (float)Math.Sqrt(2.0 / InputSize);
            for (int o = 0; o < OutputSize; o++)
            {
                for (int i = 0; i < InputSize; i++)
                {
                    Weights[o][i] = (float)(NextGaussian(rng) * stddev);
                }
                Bias[o] = 0f;
            }
        }

        /// <summary>
        /// Xavier (Glorot) initialization — Sigmoid/Tanh ağlar için ideal.
        /// </summary>
        public void InitializeXavier(Random rng)
        {
            float stddev = (float)Math.Sqrt(2.0 / (InputSize + OutputSize));
            for (int o = 0; o < OutputSize; o++)
            {
                for (int i = 0; i < InputSize; i++)
                {
                    Weights[o][i] = (float)(NextGaussian(rng) * stddev);
                }
                Bias[o] = 0f;
            }
        }

        /// <summary>
        /// Forward pass: output = activation(W * input + bias)
        /// </summary>
        public float[] Forward(float[] input)
        {
            if (input.Length != InputSize)
                throw new ArgumentException($"Input size mismatch: expected {InputSize}, got {input.Length}");

            Array.Copy(input, LastInput, input.Length);

            for (int o = 0; o < OutputSize; o++)
            {
                float sum = Bias[o];
                var w = Weights[o];
                for (int i = 0; i < InputSize; i++)
                {
                    sum += w[i] * input[i];
                }
                LastPreActivation[o] = sum;
            }

            // Softmax ayrı hesaplanır (tüm nöronlar arası bağımlılık)
            if (Activation == ActivationType.Softmax)
            {
                ApplySoftmax(LastPreActivation, LastOutput);
            }
            else
            {
                for (int o = 0; o < OutputSize; o++)
                {
                    LastOutput[o] = Activate(LastPreActivation[o]);
                }
            }

            return LastOutput;
        }

        private float Activate(float x)
        {
            switch (Activation)
            {
                case ActivationType.ReLU: return x > 0f ? x : 0f;
                case ActivationType.Sigmoid: return 1f / (1f + (float)Math.Exp(-Clamp(x, -20f, 20f)));
                case ActivationType.Tanh: return (float)Math.Tanh(Clamp(x, -20f, 20f));
                case ActivationType.Linear: return x;
                default: return x;
            }
        }

        /// <summary>
        /// Aktivasyon fonksiyonunun türevi (backprop için).
        /// output = aktivasyon çıktısı, preAct = aktivasyon öncesi değer.
        /// </summary>
        internal float ActivationDerivative(float output, float preAct)
        {
            switch (Activation)
            {
                case ActivationType.ReLU: return preAct > 0f ? 1f : 0f;
                case ActivationType.Sigmoid: return output * (1f - output);
                case ActivationType.Tanh: return 1f - output * output;
                case ActivationType.Linear: return 1f;
                case ActivationType.Softmax: return 1f; // CrossEntropy ile birlikte kullanılır
                default: return 1f;
            }
        }

        private static void ApplySoftmax(float[] preActivation, float[] output)
        {
            float max = float.MinValue;
            for (int i = 0; i < preActivation.Length; i++)
                if (preActivation[i] > max) max = preActivation[i];

            float sum = 0f;
            for (int i = 0; i < preActivation.Length; i++)
            {
                output[i] = (float)Math.Exp(Clamp(preActivation[i] - max, -20f, 20f));
                sum += output[i];
            }

            if (sum > 0f)
            {
                float invSum = 1f / sum;
                for (int i = 0; i < output.Length; i++)
                    output[i] *= invSum;
            }
        }

        private static float Clamp(float v, float min, float max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        private static double NextGaussian(Random rng)
        {
            // Box-Muller transform
            double u1 = 1.0 - rng.NextDouble();
            double u2 = 1.0 - rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        }
    }

    /// <summary>
    /// Çok katmanlı feed-forward sinir ağı (MLP).
    /// Forward pass, backpropagation, ağırlık serileştirme.
    /// </summary>
    public class FeedForwardNetwork
    {
        public NeuralLayer[] Layers { get; private set; }
        public int[] LayerSizes { get; private set; }

        // Momentum tabanlı SGD
        private float[][][] _velocityW;  // [layer][output][input]
        private float[][] _velocityB;    // [layer][output]
        private float _momentum = 0.9f;

        // İstatistikler
        public int TotalForwardPasses { get; private set; }
        public int TotalBackwardPasses { get; private set; }
        public float LastLoss { get; private set; }

        /// <summary>
        /// Ağ oluştur. Örnek: new FeedForwardNetwork(new[] {12, 24, 16, 8})
        /// Son katman otomatik olarak Softmax, ara katmanlar ReLU kullanır.
        /// </summary>
        public FeedForwardNetwork(int[] layerSizes, int? seed = null)
        {
            if (layerSizes == null || layerSizes.Length < 2)
                throw new ArgumentException("En az 2 katman gerekli (giriş + çıkış)");

            LayerSizes = layerSizes;
            Layers = new NeuralLayer[layerSizes.Length - 1];
            _velocityW = new float[Layers.Length][][];
            _velocityB = new float[Layers.Length][];

            var rng = seed.HasValue ? new Random(seed.Value) : new Random();

            for (int l = 0; l < Layers.Length; l++)
            {
                int inputSize = layerSizes[l];
                int outputSize = layerSizes[l + 1];
                bool isLastLayer = (l == Layers.Length - 1);

                var activation = isLastLayer ? ActivationType.Softmax : ActivationType.ReLU;
                Layers[l] = new NeuralLayer(inputSize, outputSize, activation);

                // Initialization
                if (activation == ActivationType.ReLU)
                    Layers[l].InitializeHe(rng);
                else
                    Layers[l].InitializeXavier(rng);

                // Momentum buffers
                _velocityW[l] = new float[outputSize][];
                _velocityB[l] = new float[outputSize];
                for (int o = 0; o < outputSize; o++)
                {
                    _velocityW[l][o] = new float[inputSize];
                }
            }
        }

        /// <summary>
        /// Forward pass: giriş → tüm katmanlardan geçir → çıkış.
        /// Thread-safe DEĞİL — tek thread'de kullan.
        /// </summary>
        public float[] Forward(float[] input)
        {
            float[] current = input;
            for (int l = 0; l < Layers.Length; l++)
            {
                current = Layers[l].Forward(current);
            }
            TotalForwardPasses++;
            return current;
        }

        /// <summary>
        /// Backpropagation + SGD with momentum.
        /// target: beklenen çıkış (one-hot veya soft labels).
        /// Döndürür: cross-entropy loss.
        /// </summary>
        public float Backpropagate(float[] target, float learningRate = 0.01f)
        {
            if (target.Length != Layers[Layers.Length - 1].OutputSize)
                throw new ArgumentException("Target size mismatch");

            // 1. Loss hesapla (Cross-Entropy for softmax output)
            var outputLayer = Layers[Layers.Length - 1];
            float loss = 0f;
            for (int i = 0; i < target.Length; i++)
            {
                float predicted = Math.Max(outputLayer.LastOutput[i], 1e-7f);
                loss -= target[i] * (float)Math.Log(predicted);
            }
            LastLoss = loss;

            // 2. Çıkış katmanı gradyanı (Softmax + CrossEntropy = basit formül)
            float[] delta = new float[outputLayer.OutputSize];
            for (int i = 0; i < delta.Length; i++)
            {
                delta[i] = outputLayer.LastOutput[i] - target[i];
            }

            // 3. Geri yayılım (son katmandan ilk katmana)
            for (int l = Layers.Length - 1; l >= 0; l--)
            {
                var layer = Layers[l];
                float[]? nextDelta = null;

                // Bir sonraki katman için gradyan hazırla (ilk katman hariç)
                if (l > 0)
                {
                    nextDelta = new float[layer.InputSize];
                    for (int i = 0; i < layer.InputSize; i++)
                    {
                        float sum = 0f;
                        for (int o = 0; o < layer.OutputSize; o++)
                        {
                            sum += layer.Weights[o][i] * delta[o];
                        }
                        // Bir önceki katmanın aktivasyon türevi
                        var prevLayer = Layers[l - 1];
                        nextDelta[i] = sum * prevLayer.ActivationDerivative(
                            prevLayer.LastOutput[i], prevLayer.LastPreActivation[i]);
                    }
                }

                // Ağırlık ve bias güncelleme (SGD + Momentum)
                for (int o = 0; o < layer.OutputSize; o++)
                {
                    for (int i = 0; i < layer.InputSize; i++)
                    {
                        float grad = delta[o] * layer.LastInput[i];
                        _velocityW[l][o][i] = _momentum * _velocityW[l][o][i] - learningRate * grad;
                        layer.Weights[o][i] += _velocityW[l][o][i];
                    }
                    _velocityB[l][o] = _momentum * _velocityB[l][o] - learningRate * delta[o];
                    layer.Bias[o] += _velocityB[l][o];
                }

                if (nextDelta != null)
                    delta = nextDelta;
            }

            TotalBackwardPasses++;
            return loss;
        }

        /// <summary>
        /// Toplam parametre sayısını döndürür.
        /// </summary>
        public int GetParameterCount()
        {
            int count = 0;
            for (int l = 0; l < Layers.Length; l++)
            {
                count += Layers[l].InputSize * Layers[l].OutputSize; // weights
                count += Layers[l].OutputSize; // bias
            }
            return count;
        }

        /// <summary>
        /// Ağırlıkları JSON string olarak serialize eder.
        /// </summary>
        public string SerializeWeights()
        {
            var data = new NetworkWeightData
            {
                LayerSizes = LayerSizes,
                TotalForwardPasses = TotalForwardPasses,
                TotalBackwardPasses = TotalBackwardPasses,
                Layers = new LayerWeightData[Layers.Length]
            };

            for (int l = 0; l < Layers.Length; l++)
            {
                data.Layers[l] = new LayerWeightData
                {
                    Weights = Layers[l].Weights,
                    Bias = Layers[l].Bias,
                    Activation = Layers[l].Activation.ToString()
                };
            }

            return JsonConvert.SerializeObject(data, Formatting.Indented);
        }

        /// <summary>
        /// JSON string'den ağırlıkları yükler.
        /// </summary>
        public bool DeserializeWeights(string json)
        {
            try
            {
                var data = JsonConvert.DeserializeObject<NetworkWeightData>(json);
                if (data == null || data.Layers == null) return false;
                if (data.LayerSizes == null || data.LayerSizes.Length != LayerSizes.Length)
                    return false;

                // Boyut kontrolü
                for (int i = 0; i < LayerSizes.Length; i++)
                {
                    if (data.LayerSizes[i] != LayerSizes[i]) return false;
                }

                for (int l = 0; l < Layers.Length; l++)
                {
                    if (data.Layers[l].Weights == null || data.Layers[l].Bias == null)
                        return false;
                    if (data.Layers[l].Weights.Length != Layers[l].OutputSize)
                        return false;

                    for (int o = 0; o < Layers[l].OutputSize; o++)
                    {
                        if (data.Layers[l].Weights[o].Length != Layers[l].InputSize)
                            return false;
                        Array.Copy(data.Layers[l].Weights[o], Layers[l].Weights[o], Layers[l].InputSize);
                    }
                    Array.Copy(data.Layers[l].Bias, Layers[l].Bias, Layers[l].OutputSize);
                }

                TotalForwardPasses = data.TotalForwardPasses;
                TotalBackwardPasses = data.TotalBackwardPasses;

                // Momentum buffer'ları sıfırla
                ResetMomentum();

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Momentum buffer'ları sıfırla (ağırlık yükleme sonrası).
        /// </summary>
        public void ResetMomentum()
        {
            for (int l = 0; l < Layers.Length; l++)
            {
                for (int o = 0; o < Layers[l].OutputSize; o++)
                {
                    Array.Clear(_velocityW[l][o], 0, _velocityW[l][o].Length);
                }
                Array.Clear(_velocityB[l], 0, _velocityB[l].Length);
            }
        }

        /// <summary>
        /// İstatistikleri sıfırla.
        /// </summary>
        public void ResetStats()
        {
            TotalForwardPasses = 0;
            TotalBackwardPasses = 0;
            LastLoss = 0f;
        }

        public string GetDiagnostics()
        {
            return $"FeedForwardNetwork [{string.Join("→", LayerSizes)}]\n" +
                   $"  Parameters: {GetParameterCount()}\n" +
                   $"  Forward: {TotalForwardPasses}  Backward: {TotalBackwardPasses}\n" +
                   $"  LastLoss: {LastLoss:F4}";
        }

        // ── Serialization DTOs ──────────────────────────────────────
        [Serializable]
        internal class NetworkWeightData
        {
            public int[] LayerSizes = null!;
            public int TotalForwardPasses;
            public int TotalBackwardPasses;
            public LayerWeightData[] Layers = null!;
        }

        [Serializable]
        internal class LayerWeightData
        {
            public float[][] Weights = null!;
            public float[] Bias = null!;
            public string Activation = null!;
        }
    }
}
