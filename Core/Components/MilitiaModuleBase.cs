using System;
using System.Collections.Generic;
using BanditMilitias.Core.Events;
using TaleWorlds.CampaignSystem;

namespace BanditMilitias.Core.Components
{

    public enum ModuleCategory
    {
        Core = 0,
        Infrastructure = 1,
        System = 2,
        Intelligence = 3,
        Diagnostics = 4,
        Behavior = 5,
        UI = 6
    }

    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class AutoRegisterAttribute : Attribute 
    {
        public bool IsCritical { get; set; } = false;
        public int Priority { get; set; } = 50;
        public bool DevOnly { get; set; } = false;
        public string[] RequiredMods { get; set; } = Array.Empty<string>();
        public bool IsSingleton { get; set; } = true;
        public ModuleCategory Category { get; set; } = ModuleCategory.System;
    }

    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class ModuleDependencyAttribute : Attribute
    {
        public Type[] Dependencies { get; }

        public ModuleDependencyAttribute(params Type[] dependencies)
        {
            Dependencies = dependencies ?? Array.Empty<Type>();
        }
    }


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


    public interface ICleanupSystem
    {
        void Cleanup();
        bool IsEnabled { get; }
    }


    public abstract class MilitiaModuleBase : IMilitiaModule, ICleanupSystem
    {
        public abstract string ModuleName { get; }
        public virtual bool IsEnabled => true;
        public virtual bool IsCritical => false;
        public virtual int Priority => 50;

        // Tracks all EventBus subscriptions made via SubscribeSafe<T> so
        // they can be guaranteed-unsubscribed in Cleanup() even if a derived
        // class forgets to call Unsubscribe manually.
        private readonly List<Action> _eventBusUnsubscribeActions = new List<Action>();

        /// <summary>
        /// Subscribe to an EventBus event and register the unsubscribe action
        /// so it is automatically called during Cleanup().
        /// </summary>
        protected void SubscribeSafe<T>(Action<T> handler) where T : IGameEvent
        {
            EventBus.Instance.Subscribe(handler);
            _eventBusUnsubscribeActions.Add(() =>
            {
                try { EventBus.Instance.Unsubscribe(handler); }
                catch { /* Suppress — already unsubscribed or bus reset */ }
            });
        }

        /// <summary>
        /// Manually unsubscribe from an event. Also clears the stored unsubscribe
        /// action to prevent a double-unsubscribe during Cleanup().
        /// </summary>
        protected void UnsubscribeSafe<T>(Action<T> handler) where T : IGameEvent
        {
            try { EventBus.Instance.Unsubscribe(handler); }
            catch { }
        }

        public virtual void Initialize() { }

        /// <summary>
        /// Override and call base.Cleanup() to guarantee all SubscribeSafe
        /// subscriptions are removed even if the override throws.
        /// </summary>
        public virtual void Cleanup()
        {
            foreach (var unsub in _eventBusUnsubscribeActions)
                unsub();
            _eventBusUnsubscribeActions.Clear();
        }

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
