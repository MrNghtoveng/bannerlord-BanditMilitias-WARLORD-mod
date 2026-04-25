using System.Collections.Generic;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BanditMilitias.Intelligence.Tactical
{
    // Compound Tasks for Advanced Advanced AI Tactics

    public class ExecuteTuranDoctrineTask : CompoundTask
    {
        public ExecuteTuranDoctrineTask() : base("ExecuteTuran") { }

        public override bool CheckPreconditions(WorldState state)
            => state.GetBool("IsTuranDoctrine") && state.GetFloat("ClosestEnemyDistance") > 40f;

        public override Queue<PrimitiveTask> Decompose(WorldState state)
        {
            var plan = new Queue<PrimitiveTask>();
            // 1. Formation Split (Logical call for coordinator)
            plan.Enqueue(new FormationSplitTask());
            // 2. Center group retreats to lure
            plan.Enqueue(new MockRetreatTask());
            // 3. Wings close the trap while center keeps baiting
            plan.Enqueue(new TuranManeuverTask());
            // 4. Final trigger window before vanilla melee takeover
            plan.Enqueue(new WaitUntilEnemyCloseTask(12f));
            return plan;
        }
    }

    public class ExecuteDoubleSquareTask : CompoundTask
    {
        public ExecuteDoubleSquareTask() : base("ExecuteDoubleSquare") { }

        public override bool CheckPreconditions(WorldState state) => true;

        public override Queue<PrimitiveTask> Decompose(WorldState state)
        {
            var plan = new Queue<PrimitiveTask>();
            plan.Enqueue(new SetArrangementTask(ArrangementOrder.ArrangementOrderSquare));
            plan.Enqueue(new WaitUntilEnemyCloseTask(90f));
            plan.Enqueue(new DeepShieldWallTask());
            plan.Enqueue(new WaitUntilEnemyCloseTask(10f));
            return plan;
        }
    }

    public class ExecuteKillboxTask : CompoundTask
    {
        public ExecuteKillboxTask() : base("ExecuteKillbox") { }

        public override bool CheckPreconditions(WorldState state) => true;

        public override Queue<PrimitiveTask> Decompose(WorldState state)
        {
            var plan = new Queue<PrimitiveTask>();
            // Set V-shape (simulated by Shield Wall and directional hold)
            plan.Enqueue(new SetArrangementTask(ArrangementOrder.ArrangementOrderShieldWall));
            plan.Enqueue(new MoveToTacticalPositionTask());
            plan.Enqueue(new WaitUntilEnemyCloseTask(90f));
            plan.Enqueue(new DeepShieldWallTask());
            plan.Enqueue(new WaitUntilEnemyCloseTask(25f));
            return plan;
        }
    }

    public class ExecuteRefusedFlankTask : CompoundTask
    {
        public ExecuteRefusedFlankTask() : base("ExecuteRefusedFlank") { }

        public override bool CheckPreconditions(WorldState state) => true; // Always available as a compound strategy

        public override Queue<PrimitiveTask> Decompose(WorldState state)
        {
            var plan = new Queue<PrimitiveTask>();
            plan.Enqueue(new SetArrangementTask(ArrangementOrder.ArrangementOrderShieldWall));
            // Complex logic for Refused Flank would go here
            plan.Enqueue(new WaitUntilEnemyCloseTask(20f));
            return plan;
        }
    }
}
