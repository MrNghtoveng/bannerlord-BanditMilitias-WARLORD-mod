using System.Collections.Generic;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BanditMilitias.Intelligence.Tactical
{


    public class ExecuteAmbushDoctrineTask : CompoundTask
    {
        public ExecuteAmbushDoctrineTask() : base("ExecuteAmbushDoctrine") { }

        public override bool CheckPreconditions(WorldState state)
        {


            return state.GetBool("IsAmbushDoctrine") && state.GetFloat("ClosestEnemyDistance") > 30f;
        }

        public override Queue<PrimitiveTask> Decompose(WorldState state)
        {
            var plan = new Queue<PrimitiveTask>();


            bool isMeleeHeavy = state.GetBool("IsMeleeHeavy", false);
            if (isMeleeHeavy)
                plan.Enqueue(new SetArrangementTask(ArrangementOrder.ArrangementOrderShieldWall));
            else
                plan.Enqueue(new SetArrangementTask(ArrangementOrder.ArrangementOrderLoose));


            plan.Enqueue(new MoveToTacticalPositionTask());


            plan.Enqueue(new WaitUntilEnemyCloseTask(30f));


            return plan;
        }
    }
}
