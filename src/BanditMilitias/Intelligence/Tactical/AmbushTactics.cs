using System.Collections.Generic;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BanditMilitias.Intelligence.Tactical
{
    // Compound Tasks for HTN Planner

    public class ExecuteAmbushDoctrineTask : CompoundTask
    {
        public ExecuteAmbushDoctrineTask() : base("ExecuteAmbushDoctrine") { }

        public override bool CheckPreconditions(WorldState state)
        {
            // Only ambush if we have the Ambush doctrine set from Strat layer, 
            // and the enemy hasn't already broken the ambush range.
            return state.GetBool("IsAmbushDoctrine") && state.GetFloat("ClosestEnemyDistance") > 30f;
        }

        public override Queue<PrimitiveTask> Decompose(WorldState state)
        {
            var plan = new Queue<PrimitiveTask>();

            // 1. Arrange safely into Loose or ShieldWall depending on composition
            // For bandits, we often use Loose for ambush to avoid ranged fire until closing.
            bool isMeleeHeavy = state.GetBool("IsMeleeHeavy", false);
            if (isMeleeHeavy)
                plan.Enqueue(new SetArrangementTask(ArrangementOrder.ArrangementOrderShieldWall));
            else
                plan.Enqueue(new SetArrangementTask(ArrangementOrder.ArrangementOrderLoose));

            // 2. Move to optimal tactical position on the 3D map (Hill/Forest)
            plan.Enqueue(new MoveToTacticalPositionTask());

            // 3. Wait silently until the enemy closes distance (Trap springs at 30 meters)
            plan.Enqueue(new WaitUntilEnemyCloseTask(30f));

            // When the plan completes, WarlordTacticalMissionBehavior will notice Plan == null/Success
            // and hand control over to native TaleWorlds AI for a brutal charge.
            return plan;
        }
    }
}
