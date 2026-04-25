using BanditMilitias.Debug;
using System;
using System.Linq;
using TaleWorlds.CampaignSystem;

namespace BanditMilitias.Infrastructure
{
    public static class ClanCache
    {
        private static Clan? _lootersClan;
        private static Clan? _fallbackBanditClan;
        private static bool _initialized = false;
        private static int _initAttempts = 0;
        private const int MAX_INIT_ATTEMPTS = 10;
        private static readonly object _retryOwner = new object();
        private static bool _retryScheduled = false;

        public static bool IsInitialized => _initialized;
        public static int InitAttempts => _initAttempts;

        public static void Initialize()
        {
            if (_initialized) return;

            _initAttempts++;

            // KRİTİK FIX: Clan.All null/empty kontrolü
            if (Clan.All == null || !Clan.All.Any())
            {
                if (Settings.Instance?.TestingMode == true && _initAttempts <= 3)
                {
                    TaleWorlds.Library.InformationManager.DisplayMessage(
                        new TaleWorlds.Library.InformationMessage(
                            $"[ClanCache] Attempt {_initAttempts}/{MAX_INIT_ATTEMPTS}: Clan.All henüz yüklenmemiş - retry scheduled",
                            TaleWorlds.Library.Colors.Yellow));
                }

                // Auto-retry mekanizması
                if (_initAttempts < MAX_INIT_ATTEMPTS)
                {
                    ScheduleRetry();
                }
                return;
            }

            try
            {
                _lootersClan = Clan.All.FirstOrDefault(c => c.StringId == "looters");
                _fallbackBanditClan = Clan.All.FirstOrDefault(c => c.IsBanditFaction);

                if (_lootersClan == null && _fallbackBanditClan == null)
                {
                    if (Settings.Instance?.TestingMode == true)
                    {
                        TaleWorlds.Library.InformationManager.DisplayMessage(
                            new TaleWorlds.Library.InformationMessage(
                                "[ClanCache] UYARI: Hiç klan bulunamadı! Tekrar denenecek.",
                                TaleWorlds.Library.Colors.Red));
                    }

                    if (_initAttempts < MAX_INIT_ATTEMPTS)
                    {
                        ScheduleRetry();
                    }
                    return;
                }

                _initialized = true;

                if (Settings.Instance?.TestingMode == true)
                {
                    TaleWorlds.Library.InformationManager.DisplayMessage(
                        new TaleWorlds.Library.InformationMessage(
                            $"[ClanCache] Initialized: Looters={_lootersClan != null}, Fallback={_fallbackBanditClan != null}",
                            TaleWorlds.Library.Colors.Green));
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Error("ClanCache", $"Initialize failed: {ex.Message}");
                if (_initAttempts < MAX_INIT_ATTEMPTS)
                {
                    ScheduleRetry();
                }
            }
        }

        private static void ScheduleRetry()
        {
            if (_retryScheduled) return;
            _retryScheduled = true;

            try
            {
                CampaignEvents.DailyTickEvent.AddNonSerializedListener(_retryOwner, RetryInit);
            }
            catch
            {
                _retryScheduled = false;
            }
        }

        private static void RetryInit()
        {
            try
            {
                CampaignEvents.DailyTickEvent.RemoveNonSerializedListener(_retryOwner, RetryInit);
            }
            catch
            {
            }

            _retryScheduled = false;
            Initialize();
        }

        public static Clan? GetLootersClan()
        {
            EnsureInitialized();
            return _lootersClan;
        }

        public static Clan? GetFallbackBanditClan()
        {
            EnsureInitialized();
            return _fallbackBanditClan ?? _lootersClan;
        }

        private static void EnsureInitialized()
        {
            if (!_initialized)
            {
                Initialize();
            }
        }

        public static void Reset()
        {
            // FIX #6: Reset sırasında bekleyen DailyTick retry listener'ı da temizle.
            // Aksi hâlde _retryScheduled=true kalıp yeni oyunda ScheduleRetry() çağrılmaz
            // ve ClanCache init başarısız olunca retry hiç yapılmaz → spawn tamamen durur.
            if (_retryScheduled)
            {
                try
                {
                    CampaignEvents.DailyTickEvent.RemoveNonSerializedListener(_retryOwner, RetryInit);
                }
                catch { /* listener zaten kaldırılmışsa sorun değil */ }
                _retryScheduled = false;
            }

            _lootersClan = null;
            _fallbackBanditClan = null;
            _initialized = false;
            _initAttempts = 0;

            if (Settings.Instance?.TestingMode == true)
            {
                TaleWorlds.Library.InformationManager.DisplayMessage(
                    new TaleWorlds.Library.InformationMessage(
                        "[ClanCache] Reset complete",
                        TaleWorlds.Library.Colors.Yellow));
            }
        }
    }
}