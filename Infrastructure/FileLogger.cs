using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BanditMilitias.Infrastructure
{

    public static class FileLogger
    {
        private static readonly string LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Mount and Blade II Bannerlord",
            "Warlord_Logs",
            "BanditMilitias");

        private static readonly string LogPath = Path.Combine(LogDirectory, "BanditMilitias.log");

        private static readonly object _lockObject = new object();
        private static readonly ConcurrentQueue<string> _logQueue = new ConcurrentQueue<string>();
        private static int _isWriting = 0;
        private const int MAX_QUEUE_SIZE = 10000;

        // FIX-7: BOM-less UTF-8 — Windows'un varsayılan sistem encoding'i (UTF-8 BOM'lu)
        // SwarmCoordinator loglarında "Ambush→Defensive" yerine bozuk karakterler üretiyordu.
        private static readonly System.Text.Encoding Utf8NoBom = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        public static void Log(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            if (_logQueue.Count > MAX_QUEUE_SIZE) return; // MEMORY GUARD

            string timestampedMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n";
            _logQueue.Enqueue(timestampedMessage);

            if (Interlocked.CompareExchange(ref _isWriting, 1, 0) == 0)
            {
                _ = Task.Run(ProcessQueue);
            }
        }

        private static void ProcessQueue()
        {
            try
            {
                if (!Directory.Exists(LogDirectory))
                    _ = Directory.CreateDirectory(LogDirectory);

                var sb = new System.Text.StringBuilder();
                while (_logQueue.TryDequeue(out var msg))
                {
                    _ = sb.Append(msg);
                }

                if (sb.Length > 0)
                {
                    lock (_lockObject)
                    {
                        // FIX-7: BOM-less UTF-8 ile yaz — ok/arrow gibi özel karakterler düzgün görünür.
                        File.AppendAllText(LogPath, sb.ToString(), Utf8NoBom);
                    }
                }
            }
            catch (Exception ex)
            {
                // NOTE: We cannot log to FileLogger here as it would cause recursion.
                System.Console.Error.WriteLine($"[BanditMilitias] FileLogger.ProcessQueue failed: {ex.Message}");
            }
            finally
            {
                // HATA-BM-4 FIX: Önce kuyrukta eleman var mı kontrol et.
                // Eğer varsa _isWriting'i serbest bırakmadan devam et,
                // böylece iki paralel writer oluşması engellenir.
                if (!_logQueue.IsEmpty)
                {
                    _ = Task.Run(ProcessQueue);
                }
                else
                {
                    _ = Interlocked.Exchange(ref _isWriting, 0);
                    // Son kontrol: sıfırladıktan sonra yeni eleman gelmiş olabilir
                    if (!_logQueue.IsEmpty && Interlocked.CompareExchange(ref _isWriting, 1, 0) == 0)
                    {
                        _ = Task.Run(ProcessQueue);
                    }
                }
            }
        }

        public static void Clear()
        {
            try
            {
                lock (_lockObject)
                {
                    if (File.Exists(LogPath))
                    {
                        File.Delete(LogPath);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine($"[BanditMilitias] FileLogger.Clear failed: {ex.Message}");
            }
        }

        public static string GetLogPath() => LogPath;

        public static void LogError(string message)
        {
            Log($"[ERROR] {message}");
        }

        public static void LogWarning(string message)
        {
            Log($"[WARNING] {message}");
        }

        public static void LogSuccess(string message)
        {
            Log($"[SUCCESS] {message}");
        }

        public static void LogSection(string sectionName)
        {
            Log($"========== {sectionName} ==========");
        }
    }
}