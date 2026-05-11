using BanditMilitias.Infrastructure;
using BanditMilitias.Systems.AI;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BanditMilitias.Intelligence.Tactical
{
    public class WarlordTacticalMissionBehavior : MissionBehavior
    {
        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        private const float TICK_INTERVAL = 0.5f;

        private float _timeSinceLastTick = 0f;

        private bool _isInitialized = false;
        private bool _tacticsComplete = false;

        private readonly Dictionary<Formation, HTNPlanner> _planners = new();
        private readonly WorldState _worldState = new();


        private MobileParty? _warlordParty;

        public override void OnMissionTick(float dt)
        {
            if (_tacticsComplete) return;


            _timeSinceLastTick += dt;
            if (_timeSinceLastTick < TICK_INTERVAL) return;

            _timeSinceLastTick = 0f;

            if (!_isInitialized)
            {
                InitializeAI();
                _isInitialized = true;
            }

            if (_warlordParty == null || _planners.Count == 0)
            {
                _tacticsComplete = true;

                return;
            }

            UpdateWorldState();


            if (_worldState.GetFloat("ClosestEnemyDistance") < 20f || _worldState.GetBool("IsEngagedInMelee"))
            {
                HandoverToVanillaAI();
                _tacticsComplete = true;
                return;
            }

            float dtTally = TICK_INTERVAL;


            bool anyExecuting = false;
            foreach (var kvp in _planners)
            {
                Formation formation = kvp.Key;
                HTNPlanner planner = kvp.Value;

                if (formation == null || formation.CountOfUnits == 0) continue;


                planner.Tick(formation, _worldState, dtTally);
                anyExecuting = true;
            }


            if (!anyExecuting)
            {
                HandoverToVanillaAI();
                _tacticsComplete = true;
            }
        }

        private void InitializeAI()
        {
            try
            {


                if (Mission.Current == null || Mission.Current.Teams == null) return;


                foreach (var agent in Mission.Current.Agents)
                {
                    if (agent.IsHuman && agent.Origin?.BattleCombatant != null)
                    {
                        var party = ExtractMobileParty(agent.Origin.BattleCombatant);
                        if (party != null && party.IsBandit)
                        {
                            var warlord = Strategic.WarlordSystem.Instance.GetWarlordForParty(party);
                            if (warlord != null)
                            {
                                _warlordParty = party;
                                break;
                            }
                        }
                    }
                }

                if (_warlordParty == null) return;


                var instance = AdaptiveAIDoctrineSystem.Instance;
                if (instance == null) return;
                AdaptiveDoctrineProfile? profile = instance.GetProfileForWarlord(_warlordParty);
                if (profile == null) return;
                CounterDoctrine doctrine = profile.ActiveCounterDoctrine;

                if (Settings.Instance?.TestingMode == true)
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        $"[Bandit Militias - TACTICAL] Strategy: {doctrine}",
                        Colors.Cyan));
                }

                _worldState.SetFloat("ClosestEnemyDistance", 9999f);
                _worldState.SetBool("IsEngagedInMelee", false);
                _worldState.SetBool("IsAmbushDoctrine", doctrine == CounterDoctrine.HarassScreen);
                _worldState.SetBool("IsTuranDoctrine", doctrine == CounterDoctrine.Turan);


                Team? warlordTeam = Mission.Current.PlayerEnemyTeam ?? Mission.Current.Teams.FirstOrDefault();

                if (warlordTeam == null) return;

                _worldState.SetBool("IsMeleeHeavy", IsMeleeHeavyTeam(warlordTeam));

                foreach (Formation formation in warlordTeam.FormationsIncludingEmpty)
                {
                    if (formation.CountOfUnits > 0 &&
                        (formation.PhysicalClass.IsMeleeInfantry() || formation.PhysicalClass.IsRanged()))
                    {
                        var planner = new HTNPlanner();


                        CompoundTask tacticalPlan = doctrine switch
                        {
                            CounterDoctrine.Turan => new ExecuteTuranDoctrineTask(),
                            CounterDoctrine.Killbox => new ExecuteKillboxTask(),
                            CounterDoctrine.DoubleSquare => new ExecuteDoubleSquareTask(),
                            CounterDoctrine.RefusedFlank => new ExecuteRefusedFlankTask(),
                            CounterDoctrine.SpearWall => new ExecuteDoubleSquareTask(),

                            CounterDoctrine.HarassScreen => new ExecuteAmbushDoctrineTask(),
                            CounterDoctrine.ShockRaid => new ExecuteKillboxTask(),

                            _ => new ExecuteAmbushDoctrineTask()
                        };

                        planner.Plan(tacticalPlan, _worldState);


                        formation.SetControlledByAI(false);
                        _planners.Add(formation, planner);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.DebugLogger.Log($"HTN InitializeAI failed: {ex}");
            }
        }

        private void UpdateWorldState()
        {
            if (Mission.Current == null || Mission.Current.PlayerTeam == null) return;


            float minDistance = float.MaxValue;
            bool isInMelee = false;
            int meleeUnits = _planners.Keys
                .Where(f => f != null && f.CountOfUnits > 0 && !f.PhysicalClass.IsRanged())
                .Sum(f => f.CountOfUnits);
            int rangedUnits = _planners.Keys
                .Where(f => f != null && f.CountOfUnits > 0 && f.PhysicalClass.IsRanged())
                .Sum(f => f.CountOfUnits);

            foreach (var enemyAgent in Mission.Current.PlayerTeam.ActiveAgents)
            {
                if (!enemyAgent.IsHuman) continue;

                foreach (var friendlyAgent in _planners.Keys.SelectMany(f => f.GetUnitsWithoutDetachedOnes()))
                {
                    if (friendlyAgent is Agent fa && fa.IsActive())
                    {
                        float dist = fa.Position.DistanceSquared(enemyAgent.Position);
                        if (dist < minDistance) minDistance = dist;
                        if (dist < 25f)

                        {
                            isInMelee = true;
                            break;
                        }
                    }
                }
                if (isInMelee) break;
            }

            _worldState.SetFloat("ClosestEnemyDistance", minDistance != float.MaxValue ? (float)Math.Sqrt(minDistance) : 9999f);
            _worldState.SetBool("IsEngagedInMelee", isInMelee);
            _worldState.SetBool("IsMeleeHeavy", meleeUnits >= rangedUnits);
        }

        private static bool IsMeleeHeavyTeam(Team team)
        {
            int meleeUnits = 0;
            int rangedUnits = 0;

            foreach (Formation formation in team.FormationsIncludingEmpty)
            {
                if (formation == null || formation.CountOfUnits <= 0) continue;

                if (formation.PhysicalClass.IsRanged())
                    rangedUnits += formation.CountOfUnits;
                else
                    meleeUnits += formation.CountOfUnits;
            }

            return meleeUnits >= rangedUnits;
        }

        private void HandoverToVanillaAI()
        {
            try
            {
                foreach (var kvp in _planners)
                {
                    var formation = kvp.Key;
                    var planner = kvp.Value;

                    planner.Abort();

                    if (formation != null && formation.CountOfUnits > 0)
                    {
                        formation.SetMovementOrder(MovementOrder.MovementOrderCharge);

                        formation.SetControlledByAI(true);

                    }
                }
                _planners.Clear();

                if (Settings.Instance?.TestingMode == true)
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        "[Bandit Militias - TACTICAL] Ambush triggered! Handing control to Vanilla Battle AI.",
                        Colors.Red));
                }
            }
            catch (Exception ex)
            {
                Debug.DebugLogger.Log($"Handover to AI error: {ex}");
            }
        }

        private MobileParty? ExtractMobileParty(IBattleCombatant combatant)
        {
            try
            {
                var originProp = combatant?.GetType().GetProperty("Origin");
                var originVal = originProp?.GetValue(combatant);
                var partyProp = originVal?.GetType().GetProperty("Party");
                var party = partyProp?.GetValue(originVal) as PartyBase;
                return party?.MobileParty;
            }
            catch
            {
                return null;
            }
        }
    }
}
