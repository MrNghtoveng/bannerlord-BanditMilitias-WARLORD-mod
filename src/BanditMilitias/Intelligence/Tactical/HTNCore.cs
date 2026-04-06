using System;
using System.Collections.Generic;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace BanditMilitias.Intelligence.Tactical
{
    /// <summary>
    /// Represents the current World State for the HTN Planner in the 3D battlefield context.
    /// Values are typically extracted from TaleWorlds.MountAndBlade.Mission.
    /// </summary>
    public class WorldState
    {
        private readonly Dictionary<string, object> _stateData = new();

        public void SetBool(string key, bool value) => _stateData[key] = value;
        public bool GetBool(string key, bool defaultValue = false) => 
            _stateData.TryGetValue(key, out var val) && val is bool b ? b : defaultValue;

        public void SetFloat(string key, float value) => _stateData[key] = value;
        public float GetFloat(string key, float defaultValue = 0f) => 
            _stateData.TryGetValue(key, out var val) && val is float f ? f : defaultValue;

        public void SetInt(string key, int value) => _stateData[key] = value;
        public int GetInt(string key, int defaultValue = 0) => 
            _stateData.TryGetValue(key, out var val) && val is int i ? i : defaultValue;

        public void Clear() => _stateData.Clear();
    }

    public enum HTNStatus
    {
        Failure,
        Success,
        Executing
    }

    /// <summary>
    /// Base interface for any HTN Task.
    /// </summary>
    public interface ITask
    {
        string Name { get; }
        bool CheckPreconditions(WorldState state);
    }

    /// <summary>
    /// A Primitive Task is an actual action that modifies the world or performs an action in the game engine.
    /// </summary>
    public abstract class PrimitiveTask : ITask
    {
        public string Name { get; }
        protected PrimitiveTask(string name) { Name = name; }
        
        public abstract bool CheckPreconditions(WorldState state);
        
        /// <summary>
        /// Called when the task begins execution.
        /// </summary>
        public abstract void Start(TaleWorlds.MountAndBlade.Formation targetFormation);

        /// <summary>
        /// Applies the action effects during every tick.
        /// </summary>
        public abstract HTNStatus DefaultTick(TaleWorlds.MountAndBlade.Formation targetFormation, WorldState state, float dt);
        
        public virtual void Stop(TaleWorlds.MountAndBlade.Formation targetFormation) { }
    }

    /// <summary>
    /// A Compound Task defines rules to decompose itself into subtasks or primitive actions.
    /// Usually contains multiple methods of achieving the underlying goal.
    /// </summary>
    public abstract class CompoundTask : ITask
    {
        public string Name { get; }
        protected CompoundTask(string name) { Name = name; }
        
        public abstract bool CheckPreconditions(WorldState state);
        
        /// <summary>
        /// Decomposes this compound task into a queue of primitive tasks.
        /// </summary>
        public abstract Queue<PrimitiveTask> Decompose(WorldState state);
    }

    /// <summary>
    /// Minimalist HTN Planner built for high-performance tick-based execution inside TaleWorlds Missions.
    /// </summary>
    public class HTNPlanner
    {
        private Queue<PrimitiveTask> _currentPlan;
        private PrimitiveTask? _executingTask;
        
        public HTNPlanner()
        {
            _currentPlan = new Queue<PrimitiveTask>();
        }

        /// <summary>
        /// Replan: given a root compound task, decomposes it down to primitive actions based on the current world state.
        /// </summary>
        public bool Plan(CompoundTask rootTask, WorldState state)
        {
            if (rootTask == null || !rootTask.CheckPreconditions(state))
                return false;

            var newPlan = rootTask.Decompose(state);
            if (newPlan != null && newPlan.Count > 0)
            {
                if (_executingTask != null)
                {
                    _executingTask = null; // stop any ongoing poorly-performing task gracefully if needed.
                }
                _currentPlan = newPlan;
                return true;
            }
            return false;
        }

        public void Tick(TaleWorlds.MountAndBlade.Formation formation, WorldState state, float dt)
        {
            if (formation == null) return;

            // If we don't have an executing task but have a plan, dequeue the next.
            if (_executingTask == null && _currentPlan.Count > 0)
            {
                _executingTask = _currentPlan.Dequeue();
                _executingTask.Start(formation);
            }

            // Execute the current task.
            if (_executingTask != null)
            {
                HTNStatus status = _executingTask.DefaultTick(formation, state, dt);
                
                if (status == HTNStatus.Success)
                {
                    _executingTask.Stop(formation);
                    _executingTask = null; // Next tick will grab the next task
                }
                else if (status == HTNStatus.Failure)
                {
                    // Plan failed during execution, clear everything so the higher level can replan.
                    _executingTask.Stop(formation);
                    _executingTask = null;
                    _currentPlan.Clear();
                }
                // Executing state means we wait and continue holding this task.
            }
        }
        
        public void Abort()
        {
            _executingTask = null;
            _currentPlan.Clear();
        }
    }
}
