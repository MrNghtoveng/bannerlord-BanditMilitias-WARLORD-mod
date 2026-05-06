using System;
using System.Collections.Generic;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace BanditMilitias.Intelligence.Tactical
{


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


    public interface ITask
    {
        string Name { get; }
        bool CheckPreconditions(WorldState state);
    }


    public abstract class PrimitiveTask : ITask
    {
        public string Name { get; }
        protected PrimitiveTask(string name) { Name = name; }

        public abstract bool CheckPreconditions(WorldState state);


        public abstract void Start(TaleWorlds.MountAndBlade.Formation targetFormation);


        public abstract HTNStatus DefaultTick(TaleWorlds.MountAndBlade.Formation targetFormation, WorldState state, float dt);

        public virtual void Stop(TaleWorlds.MountAndBlade.Formation targetFormation) { }
    }


    public abstract class CompoundTask : ITask
    {
        public string Name { get; }
        protected CompoundTask(string name) { Name = name; }

        public abstract bool CheckPreconditions(WorldState state);


        public abstract Queue<PrimitiveTask> Decompose(WorldState state);
    }


    public class HTNPlanner
    {
        private Queue<PrimitiveTask> _currentPlan;
        private PrimitiveTask? _executingTask;

        public HTNPlanner()
        {
            _currentPlan = new Queue<PrimitiveTask>();
        }


        public bool Plan(CompoundTask rootTask, WorldState state)
        {
            if (rootTask == null || !rootTask.CheckPreconditions(state))
                return false;

            var newPlan = rootTask.Decompose(state);
            if (newPlan != null && newPlan.Count > 0)
            {
                if (_executingTask != null)
                {
                    _executingTask = null;

                }
                _currentPlan = newPlan;
                return true;
            }
            return false;
        }

        public void Tick(TaleWorlds.MountAndBlade.Formation formation, WorldState state, float dt)
        {
            if (formation == null) return;


            while (_executingTask == null && _currentPlan.Count > 0)
            {
                PrimitiveTask candidate = _currentPlan.Dequeue();
                if (!candidate.CheckPreconditions(state))
                {
                    continue;
                }

                _executingTask = candidate;
                _executingTask.Start(formation);
            }


            if (_executingTask != null)
            {
                HTNStatus status = _executingTask.DefaultTick(formation, state, dt);

                if (status == HTNStatus.Success)
                {
                    _executingTask.Stop(formation);
                    _executingTask = null;

                }
                else if (status == HTNStatus.Failure)
                {


                    _executingTask.Stop(formation);
                    _executingTask = null;
                    _currentPlan.Clear();
                }


            }
        }

        public void Abort()
        {
            _executingTask = null;
            _currentPlan.Clear();
        }
    }
}
