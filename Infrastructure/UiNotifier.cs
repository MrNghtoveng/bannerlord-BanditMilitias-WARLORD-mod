using System;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using TaleWorlds.Library;

namespace BanditMilitias.Infrastructure
{
    public static class UiNotifier
    {
        public static bool TryShow(string message, Color color, string category = "UiNotifier")
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            try
            {
                if (!EventBus.Instance.IsOnMainThread)
                {
                    FileLogger.LogWarning($"[{category}] UI message skipped off main thread: {message}");
                    return false;
                }

                InformationManager.DisplayMessage(new InformationMessage(message, color));
                return true;
            }
            catch (Exception ex)
            {
                DebugLogger.Warning(category, $"DisplayMessage failed: {ex.Message}");
                return false;
            }
        }
    }
}
