using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TaleWorlds.Library;

namespace BanditMilitias.Intelligence.Neural
{
    // ═══════════════════════════════════════════════════════════════════
    //  NEURAL DATA EXPORTER — Ağırlık/Veri Import/Export + Konsol
    //
    //  Mevcut DevDataCollector ile entegre çalışır.
    //  Konsol komutları: militia.neural_status, neural_train, vb.
    // ═══════════════════════════════════════════════════════════════════

    public static class NeuralDataExporter
    {
        private static string? _exportDir;

        /// <summary>
        /// Export dizinini ayarla (DevDataCollector session dizini altında).
        /// </summary>
        public static void SetExportDirectory(string dir)
        {
            _exportDir = dir;
        }

        /// <summary>
        /// Deneyim buffer'ını CSV'ye yaz.
        /// </summary>
        public static bool ExportExperienceBuffer(ExperienceBuffer buffer, string filename = "experience_buffer.csv")
        {
            if (buffer == null || string.IsNullOrEmpty(_exportDir)) return false;

            try
            {
                // SECURITY: Prevent path traversal by ensuring filename is just a filename
                string safeFilename = Path.GetFileName(filename);
                if (string.IsNullOrEmpty(safeFilename) || safeFilename != filename)
                {
                    DebugLogger.Warning("NeuralExporter", $"Rejected suspicious filename: {filename}");
                    safeFilename = "experience_buffer.csv"; // Fallback to safe default
                }

                Directory.CreateDirectory(_exportDir);
                string path = Path.Combine(_exportDir, safeFilename);

                // FINAL SECURITY CHECK: Ensure the combined path is still inside the intended directory
                string fullPath = Path.GetFullPath(path);
                string fullExportDir = Path.GetFullPath(_exportDir);
                if (!fullPath.StartsWith(fullExportDir, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnauthorizedAccessException("Attempted path traversal detected.");
                }

                File.WriteAllText(path, buffer.ToCsv(), Encoding.UTF8);
                DebugLogger.Info("NeuralExporter", $"Experience buffer exported to {path}");
                return true;
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("NeuralExporter", $"Export failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Eğitim logunu CSV'ye yaz.
        /// </summary>
        public static void AppendTrainingLog(int batchNum, int samples, float loss, float confidence)
        {
            if (string.IsNullOrEmpty(_exportDir)) return;

            try
            {
                string path = Path.Combine(_exportDir, "neural_training_log.csv");
                Directory.CreateDirectory(_exportDir);

                if (!File.Exists(path))
                {
                    File.WriteAllText(path,
                        "DateTime,GameDay,BatchNum,Samples,Loss,Confidence\n",
                        Encoding.UTF8);
                }

                string gameDay = TaleWorlds.CampaignSystem.Campaign.Current != null
                    ? TaleWorlds.CampaignSystem.CampaignTime.Now.ToDays.ToString("F2")
                    : "0";

                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss},{gameDay}," +
                             $"{batchNum},{samples},{loss:F4},{confidence:F3}\n";

                File.AppendAllText(path, line, Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>
        /// Neural inference sonuçlarını logla.
        /// </summary>
        public static void AppendPredictionLog(string warlordId, string recommendedAction,
            float confidence, float[] probabilities)
        {
            if (string.IsNullOrEmpty(_exportDir)) return;

            try
            {
                string path = Path.Combine(_exportDir, "neural_predictions.csv");
                Directory.CreateDirectory(_exportDir);

                if (!File.Exists(path))
                {
                    File.WriteAllText(path,
                        "DateTime,GameDay,WarlordId,RecommendedAction,Confidence,Probabilities\n",
                        Encoding.UTF8);
                }

                string gameDay = TaleWorlds.CampaignSystem.Campaign.Current != null
                    ? TaleWorlds.CampaignSystem.CampaignTime.Now.ToDays.ToString("F2")
                    : "0";

                string probStr = probabilities != null
                    ? string.Join(";", probabilities)
                    : "";

                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss},{gameDay}," +
                             $"\"{warlordId}\",\"{recommendedAction}\",{confidence:F3}," +
                             $"\"{probStr}\"\n";

                File.AppendAllText(path, line, Encoding.UTF8);
            }
            catch { }
        }

        // ═══════════════════════════════════════════════════════════
        //  KONSOL KOMUTLARI
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// militia.neural_status — Neural ağ durumu, confidence, buffer size.
        /// </summary>
        [TaleWorlds.Library.CommandLineFunctionality.CommandLineArgumentFunction("neural_status", "militia")]
        public static string CommandNeuralStatus(List<string> args)
        {
            var advisor = NeuralAdvisor.Instance;
            if (advisor == null)
                return "[Neural] NeuralAdvisor henüz initialize edilmedi.";

            return advisor.GetDiagnostics();
        }

        /// <summary>
        /// militia.neural_train [batchCount] — Manuel eğitim tetikleme.
        /// Kullanım: militia.neural_train 10
        /// </summary>
        [TaleWorlds.Library.CommandLineFunctionality.CommandLineArgumentFunction("neural_train", "militia")]
        public static string CommandNeuralTrain(List<string> args)
        {
            var advisor = NeuralAdvisor.Instance;
            if (advisor == null)
                return "[Neural] NeuralAdvisor henüz initialize edilmedi.";

            int batches = 10;
            if (args != null && args.Count > 0)
            {
                int.TryParse(args[0], out batches);
                batches = Math.Max(1, Math.Min(1000, batches));
            }

            string result = advisor.TrainOffline(batches);

            // Log to file
            AppendTrainingLog(advisor.TotalTrainingBatches, batches * 32,
                advisor.LastTrainingLoss, advisor.GlobalConfidence);

            return result;
        }

        /// <summary>
        /// militia.neural_reset — Ağırlıkları sıfırla (dikkat!).
        /// Kullanım: militia.neural_reset confirm
        /// </summary>
        [TaleWorlds.Library.CommandLineFunctionality.CommandLineArgumentFunction("neural_reset", "militia")]
        public static string CommandNeuralReset(List<string> args)
        {
            bool confirmed = args != null && args.Count > 0 &&
                             args[0].Trim().Equals("confirm", StringComparison.OrdinalIgnoreCase);

            if (!confirmed)
            {
                return "[Neural] ⚠ Bu komut ağırlıkları SIFIRLAR!\n" +
                       "Onaylamak için: militia.neural_reset confirm";
            }

            var advisor = NeuralAdvisor.Instance;
            if (advisor == null)
                return "[Neural] NeuralAdvisor henüz initialize edilmedi.";

            // Yeniden initialize et
            advisor.Cleanup();
            var newAdvisor = NeuralAdvisor.CreateInstance();
            string weightsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Mount and Blade II Bannerlord", "Warlord_Logs", "BanditMilitias", "Neural");
            newAdvisor.Initialize(weightsDir, true);

            return "[Neural] Ağırlıklar sıfırlandı ve pre-trained başlangıç uygulandı.";
        }

        /// <summary>
        /// militia.neural_export — Ağırlıkları ve deneyimleri dışa aktar.
        /// </summary>
        [TaleWorlds.Library.CommandLineFunctionality.CommandLineArgumentFunction("neural_export", "militia")]
        public static string CommandNeuralExport(List<string> args)
        {
            var advisor = NeuralAdvisor.Instance;
            if (advisor == null)
                return "[Neural] NeuralAdvisor henüz initialize edilmedi.";

            // Ağırlıkları kaydet
            advisor.TrySaveWeights();

            // Deneyim buffer'ını export et
            var buffer = advisor.GetExperienceBuffer();
            bool exported = ExportExperienceBuffer(buffer);

            return $"[Neural] Export tamamlandı:\n" +
                   $"  Ağırlıklar: kaydedildi\n" +
                   $"  Deneyimler: {(exported ? "başarılı" : "başarısız")} ({buffer?.Count ?? 0} kayıt)\n" +
                   $"  Dizin: {_exportDir ?? "ayarlanmadı"}";
        }

        /// <summary>
        /// militia.neural_confidence — Mevcut confidence değerini göster.
        /// </summary>
        [TaleWorlds.Library.CommandLineFunctionality.CommandLineArgumentFunction("neural_confidence", "militia")]
        public static string CommandNeuralConfidence(List<string> args)
        {
            var advisor = NeuralAdvisor.Instance;
            if (advisor == null)
                return "[Neural] NeuralAdvisor henüz initialize edilmedi.";

            return $"[Neural] Global Confidence: {advisor.GlobalConfidence:F3}\n" +
                   $"  Tier 3 (Warlord):   max {0.30f * advisor.GlobalConfidence:F3}\n" +
                   $"  Tier 4 (Tanınmış):  max {0.60f * advisor.GlobalConfidence:F3}\n" +
                   $"  Tier 5 (Fatih):     max {1.00f * advisor.GlobalConfidence:F3}\n" +
                   $"  Training Batches:   {advisor.TotalTrainingBatches}\n" +
                   $"  Total Inferences:   {advisor.TotalInferences}";
        }

        /// <summary>
        /// militia.neural_toggle — Neural AI'ı aç/kapat.
        /// </summary>
        [TaleWorlds.Library.CommandLineFunctionality.CommandLineArgumentFunction("neural_toggle", "militia")]
        public static string CommandNeuralToggle(List<string> args)
        {
            var advisor = NeuralAdvisor.Instance;
            if (advisor == null)
                return "[Neural] NeuralAdvisor henüz initialize edilmedi.";

            bool currentState = advisor.IsEnabled;
            advisor.SetEnabled(!currentState);

            string newState = advisor.IsEnabled ? "AÇIK" : "KAPALI";
            return $"[Neural] Neural AI durumu: {newState}";
        }
    }
}
