using System;
using TaleWorlds.CampaignSystem;

namespace BanditMilitias.Core.Components
{
    // ── Inline: AutoRegisterAttribute ────────────────────────────
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class AutoRegisterAttribute : Attribute { }

    // ── Inline: IMilitiaModule ────────────────────────────────────
    public interface IMilitiaModule
    {
        string ModuleName { get; }
        bool IsEnabled { get; }
        bool IsCritical { get; }
        int Priority { get; }
        void Initialize();
        void Cleanup();
        void OnDailyTick();
        void OnHourlyTick();
        void OnTick(float dt);
        void SyncData(IDataStore dataStore);
        void OnSessionStart();
        void RegisterCampaignEvents();
        string GetDiagnostics();
    }

    // ── Inline: ICleanupSystem ────────────────────────────────────
    public interface ICleanupSystem
    {
        void Cleanup();
        bool IsEnabled { get; }
    }

    // ── MilitiaModuleBase ─────────────────────────────────────────
    /// <summary>Tüm modüller için temel sınıf.</summary>
    public abstract class MilitiaModuleBase : IMilitiaModule, ICleanupSystem
    {
        public abstract string ModuleName { get; }
        public virtual bool IsEnabled => true;
        public virtual bool IsCritical => false;
        public virtual int Priority => 50;

        public virtual void Initialize() { }
        public virtual void Cleanup() { }
        public virtual void OnDailyTick() { }
        public virtual void OnHourlyTick() { }
        public virtual void OnTick(float dt) { }
        public virtual void SyncData(IDataStore ds) { }
        public virtual void OnSessionStart() { }
        public virtual void RegisterCampaignEvents() { }
        public virtual string GetDiagnostics() => ModuleName;
        public virtual bool HasFailedModules => false;
        public virtual string GetFailedModuleSummary() => string.Empty;
    }
}