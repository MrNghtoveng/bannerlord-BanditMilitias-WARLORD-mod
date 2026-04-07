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

        private const float TICK_INTERVAL = 0.5f; // Optimizasyon: AI'yi saniyede sadece 2 kere dÃ¼ÅŸÃ¼ndÃ¼r.
        private float _timeSinceLastTick = 0f;

        private bool _isInitialized = false;
        private bool _tacticsComplete = false;

        private readonly Dictionary<Formation, HTNPlanner> _planners = new();
        private readonly WorldState _worldState = new();
        
        // Cache warlords in current mission
        private MobileParty? _warlordParty;

        public override void OnMissionTick(float dt)
        {
            if (_tacticsComplete) return; // Plan bittiyse veya devre dÄ±ÅŸÄ± bÄ±rakÄ±ldÄ±ysa artÄ±k CPU hÄ±rcama.

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
                _tacticsComplete = true; // Sadece Warlord varsa Ã§alÄ±ÅŸÄ±r.
                return;
            }

            UpdateWorldState();

            // EÄŸer Ã§arpÄ±ÅŸma fiilen baÅŸladÄ±ysa, HTN iÅŸini bitirip Bannerlord Vanilla AI'ye devretsin
            if (_worldState.GetFloat("ClosestEnemyDistance") < 20f || _worldState.GetBool("IsEngagedInMelee"))
            {
                HandoverToVanillaAI();
                _tacticsComplete = true;
                return;
            }

            float dtTally = TICK_INTERVAL; // HTN planner receives time since it last ticked

            bool anyExecuting = false;
            foreach (var kvp in _planners)
            {
                Formation formation = kvp.Key;
                HTNPlanner planner = kvp.Value;

                if (formation == null || formation.CountOfUnits == 0) continue;

                // PlancÄ±yÄ± gÃ¼ncelle
                planner.Tick(formation, _worldState, dtTally);
                anyExecuting = true;
            }

            // GÃ¶revler tÃ¼kendiyse devret.
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
                // Mevcut savaÅŸtaki ana gruplarÄ± Ã§ek
                if (Mission.Current == null || Mission.Current.Teams == null) return;

                // EÄŸer pusu kurdularsa genelde savunan taraftÄ±rlar veya yakalayan saldÄ±ran taraf olabilir. 
                // GerÃ§ek bir sistemde Warlord kim olduÄŸunu net anlamaliyiz. 
                // Åžimdilik tÃ¼m AI takÄ±mlarÄ±ndaki Militia partilerini tarÄ±yoruz.

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

                // Taktik doktrinini Katman A'dan (Stratejik Katman) çekiyoruz.
                AdaptiveDoctrineProfile profile = AdaptiveAIDoctrineSystem.Instance.GetProfileForWarlord(_warlordParty);
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

                // PlancÄ±larÄ± formasyonlarla eÅŸleÅŸtir (Sadece Warlord'un takÄ±mÄ±)
                Team? warlordTeam = Mission.Current.PlayerEnemyTeam ?? Mission.Current.Teams.FirstOrDefault();
                    
                if (warlordTeam == null) return;

                _worldState.SetBool("IsMeleeHeavy", IsMeleeHeavyTeam(warlordTeam));

                foreach (Formation formation in warlordTeam.FormationsIncludingEmpty)
                {
                    if (formation.CountOfUnits > 0 &&
                        (formation.PhysicalClass.IsMeleeInfantry() || formation.PhysicalClass.IsRanged()))
                    {
                        var planner = new HTNPlanner();
                        
                        // Dinamik Doktrin Seçimi
                        CompoundTask tacticalPlan = doctrine switch
                        {
                            CounterDoctrine.Turan => new ExecuteTuranDoctrineTask(),
                            CounterDoctrine.Killbox => new ExecuteKillboxTask(),
                            CounterDoctrine.DoubleSquare => new ExecuteDoubleSquareTask(),
                            CounterDoctrine.RefusedFlank => new ExecuteRefusedFlankTask(),
                            CounterDoctrine.SpearWall => new ExecuteDoubleSquareTask(), // Use double square for spear wall defensive
                            CounterDoctrine.HarassScreen => new ExecuteAmbushDoctrineTask(),
                            CounterDoctrine.ShockRaid => new ExecuteKillboxTask(), // Aggressive killbox
                            _ => new ExecuteAmbushDoctrineTask()
                        };
                        
                        planner.Plan(tacticalPlan, _worldState);
                        
                        // PlanlamayÄ± baÅŸarÄ±yla aldÄ±ysa sÃ¶zlÃ¼ÄŸe ekle, Formasyonu AI kontrolÃ¼nden HTN kontrolÃ¼ne geÃ§ir
                        formation.SetControlledByAI(false); 
                        _planners.Add(formation, planner);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.DebugLogger.Log($"HTN InitializeAI failyed: {ex}");
            }
        }

        private void UpdateWorldState()
        {
            if (Mission.Current == null || Mission.Current.PlayerTeam == null) return;

            // Ã‡ok hÄ±zlÄ± mesafe Ã¶lÃ§Ã¼mÃ¼ - sadece Formasyon ortalamalarÄ± Ã¼zerinden
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
                        if (dist < 25f) // 5x5 metre iÃ§erisinde ise melee say
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
                        formation.SetMovementOrder(MovementOrder.MovementOrderCharge); // Kan kokusu! YÄ±kÄ±m emri.
                        formation.SetControlledByAI(true); // Orijinal Bannerlord yapay zekasÄ±na geÃ§.
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
