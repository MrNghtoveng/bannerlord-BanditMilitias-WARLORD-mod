using System;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BanditMilitias.Intelligence.Tactical
{


    public class SetArrangementTask : PrimitiveTask
    {
        private ArrangementOrder _order;

        public SetArrangementTask(ArrangementOrder order) : base("SetArrangement")
        {
            _order = order;
        }

        public override bool CheckPreconditions(WorldState state)
        {
            return true;
        }

        public override void Start(Formation targetFormation)
        {
            if (targetFormation != null && targetFormation.CountOfUnits > 0)
            {
                targetFormation.SetArrangementOrder(_order);
            }
        }

        public override HTNStatus DefaultTick(Formation targetFormation, WorldState state, float dt)
        {
            return HTNStatus.Success;
        }
    }

    public class FormationSplitTask : PrimitiveTask
    {
        public FormationSplitTask() : base("FormationSplit") { }

        public override bool CheckPreconditions(WorldState state)
        {
            return !state.GetBool("IsFormationsSplit");
        }

        public override void Start(Formation targetFormation)
        {
            if (targetFormation == null || targetFormation.CountOfUnits < 20) return;


        }

        public override HTNStatus DefaultTick(Formation targetFormation, WorldState state, float dt)
        {
            state.SetBool("IsFormationsSplit", true);
            return HTNStatus.Success;
        }
    }

    public class DeepShieldWallTask : PrimitiveTask
    {
        public DeepShieldWallTask() : base("DeepShieldWall") { }

        public override bool CheckPreconditions(WorldState state)
        {
            return state.GetFloat("ClosestEnemyDistance") < 100f;
        }

        public override void Start(Formation targetFormation)
        {
            if (targetFormation == null) return;
            targetFormation.SetArrangementOrder(ArrangementOrder.ArrangementOrderShieldWall);


        }

        public override HTNStatus DefaultTick(Formation targetFormation, WorldState state, float dt)
        {
            return HTNStatus.Success;
        }
    }

    public class TuranManeuverTask : PrimitiveTask
    {
        public TuranManeuverTask() : base("TuranManeuver") { }

        public override bool CheckPreconditions(WorldState state)
        {
            return state.GetFloat("ClosestEnemyDistance") > 30f;
        }

        public override void Start(Formation targetFormation)
        {


            targetFormation.SetMovementOrder(MovementOrder.MovementOrderStop);
        }

        public override HTNStatus DefaultTick(Formation targetFormation, WorldState state, float dt)
        {
            if (state.GetFloat("ClosestEnemyDistance") < 15f)
            {


                return HTNStatus.Success;
            }
            return HTNStatus.Executing;
        }
    }

    public class MockRetreatTask : PrimitiveTask
    {
        private float _timer = 0;
        public MockRetreatTask() : base("MockRetreat") { }

        public override bool CheckPreconditions(WorldState state)
        {


            float dist = state.GetFloat("ClosestEnemyDistance");
            return dist < 60f && dist > 15f;
        }

        public override void Start(Formation targetFormation)
        {
            _timer = 0;
            if (targetFormation == null) return;


            Vec2 back = targetFormation.Direction * -20f;
            WorldPosition pos = new WorldPosition(Mission.Current.Scene, UIntPtr.Zero, new Vec3(targetFormation.CurrentPosition + back, 10f, -1f), false);
            targetFormation.SetMovementOrder(MovementOrder.MovementOrderMove(pos));
        }

        public override HTNStatus DefaultTick(Formation targetFormation, WorldState state, float dt)
        {
            _timer += dt;
            if (_timer > 10f || state.GetFloat("ClosestEnemyDistance") < 10f)
                return HTNStatus.Success;

            return HTNStatus.Executing;
        }
    }

    public class MoveToTacticalPositionTask : PrimitiveTask
    {
        private float _timeout = 0f;
        private WorldPosition _targetPos;
        private bool _posCalculated = false;

        public MoveToTacticalPositionTask() : base("MoveToTacticalPosition") { }

        public override bool CheckPreconditions(WorldState state)
        {


            return state.GetFloat("ClosestEnemyDistance") > 50f;
        }

        public override void Start(Formation targetFormation)
        {
            _timeout = 0f;
            _posCalculated = false;

            if (targetFormation == null || Mission.Current == null) return;


            Vec2 currentPos = targetFormation.CurrentPosition;
            Vec2 bestPos = currentPos + new Vec2(0, -30f);


            _targetPos = new WorldPosition(Mission.Current.Scene, UIntPtr.Zero, new Vec3(bestPos, 10f, -1f), false);
            _posCalculated = true;

            targetFormation.SetMovementOrder(MovementOrder.MovementOrderMove(_targetPos));
        }

        public override HTNStatus DefaultTick(Formation targetFormation, WorldState state, float dt)
        {
            if (!_posCalculated || targetFormation == null) return HTNStatus.Failure;

            _timeout += dt;


            if (state.GetFloat("ClosestEnemyDistance") < 40f)
            {
                return HTNStatus.Failure;

            }

            float distToTarget = targetFormation.CurrentPosition.Distance(_targetPos.AsVec2);


            if (distToTarget < 5f || _timeout > 30f)
            {


                Vec2 enemyDir = state.GetFloat("ClosestEnemyDistance") < 9999f ?
                     new Vec2(1, 0) :

                     new Vec2(1, 0);


                targetFormation.SetMovementOrder(MovementOrder.MovementOrderStop);


                return HTNStatus.Success;
            }

            return HTNStatus.Executing;
        }
    }

    public class WaitUntilEnemyCloseTask : PrimitiveTask
    {
        private float _triggerDistance;

        public WaitUntilEnemyCloseTask(float triggerDistance) : base("WaitUntilEnemyClose")
        {
            _triggerDistance = triggerDistance;
        }

        public override bool CheckPreconditions(WorldState state)
        {
            return true;
        }

        public override void Start(Formation targetFormation)
        {
            if (targetFormation != null)
            {
                targetFormation.SetMovementOrder(MovementOrder.MovementOrderStop);
            }
        }

        public override HTNStatus DefaultTick(Formation targetFormation, WorldState state, float dt)
        {


            float currentDist = state.GetFloat("ClosestEnemyDistance");

            if (currentDist <= _triggerDistance)
            {


                return HTNStatus.Success;
            }

            return HTNStatus.Executing;
        }
    }
}
