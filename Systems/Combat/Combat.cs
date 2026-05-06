using BanditMilitias.Components;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Systems.Enhancement;
using BanditMilitias.Systems.Progression;
using BanditMilitias.Systems.WarlordLegitimacy;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BanditMilitias.Systems.Combat
{


    public class WarlordCombatSystem : MissionBehavior
    {
        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        public override void OnAgentHit(Agent affectedAgent, Agent affectorAgent,
            in MissionWeapon affectorWeapon, in Blow blow, in AttackCollisionData collisionData)
        {
            if (affectedAgent == null || !affectedAgent.IsHuman || affectedAgent.Character == null || affectedAgent.Character.IsHero) return;
            if (!affectedAgent.IsActive()) return;

            MobileParty? party = TryResolveMobileParty(affectedAgent);
            if (party?.PartyComponent is not MilitiaPartyComponent) return;


            int nearbyAllies = 0;
            var agents = Mission.Current.Agents;
            foreach (var other in agents)
            {
                if (other != affectedAgent && other.IsActive() && other.Team == affectorAgent.Team)
                {


                    if (other.Position.DistanceSquared(affectedAgent.Position) < 9f)
                        nearbyAllies++;
                }
            }

            if (nearbyAllies >= 2)
            {


                float swarmBonus = Math.Min(0.50f, nearbyAllies * 0.10f);


                float ambushBonus = 0f;
                if (party?.PartyComponent is MilitiaPartyComponent comp &&
                    comp.CurrentOrder?.Type == Intelligence.Strategic.CommandType.Ambush)
                {
                    ambushBonus = 0.25f;
                }

                float finalMultiplier = 1.0f + swarmBonus + ambushBonus;
                affectedAgent.Health -= (blow.InflictedDamage * (finalMultiplier - 1.0f));
            }
        }

        public override void OnAgentBuild(Agent agent, Banner banner)
        {
            if (agent == null || !agent.IsHuman || agent.Origin == null) return;

            MobileParty? party = TryResolveMobileParty(agent);
            if (party?.PartyComponent is not MilitiaPartyComponent) return;


            var adaptiveAI = Systems.AI.AdaptiveAIDoctrineSystem.Instance;
            if (adaptiveAI != null)
            {
                var profile = adaptiveAI.GetProfileForWarlord(party);
                if (profile != null)
                {
                    if (profile.ActiveCounterDoctrine == Systems.AI.CounterDoctrine.FastFlank ||
                        profile.ActiveCounterDoctrine == Systems.AI.CounterDoctrine.ShockRaid)
                    {
                        CompatibilityLayer.SetAgentBaseSpeedMultiplier(agent, 1.15f);
                    }

                    if (profile.ActiveCounterDoctrine == Systems.AI.CounterDoctrine.DefensiveDepth)
                    {
                        agent.HealthLimit += 15f;
                        agent.Health = agent.HealthLimit;
                    }
                }
            }
        }

        private static MobileParty? TryResolveMobileParty(Agent agent)
        {
            try
            {
                var combatant = agent.Origin?.BattleCombatant;
                var originProp = combatant?.GetType().GetProperty("Origin");
                var originVal = originProp?.GetValue(combatant);
                var partyProp = originVal?.GetType().GetProperty("Party");
                var partyBase = partyProp?.GetValue(originVal) as PartyBase;
                return partyBase?.MobileParty;
            }
            catch
            {
                return null;
            }
        }
    }


    public class WarlordRegenerationSystem : MissionBehavior
    {
        private const float TICK_INTERVAL = 1.0f;
        private float _timeSinceLastTick;

        private class RegenAgent
        {
            public Agent Agent = null!;
            public float HealAmountPerTick;
            public LegitimacyLevel Tier;
        }

        private readonly List<RegenAgent> _activeWarlords = new();

        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        public override void OnAgentBuild(Agent agent, Banner banner)
        {
            if (agent == null || !agent.IsHuman || agent.Origin == null) return;
            if (Settings.Instance?.EnableWarlordRegeneration != true) return;

            PartyBase? party = null;
            try
            {
                var combatant = agent.Origin.BattleCombatant;
                var originProp = combatant?.GetType().GetProperty("Origin");
                var originVal = originProp?.GetValue(combatant);
                var partyProp = originVal?.GetType().GetProperty("Party");
                party = partyProp?.GetValue(originVal) as PartyBase;
            }
            catch (System.Exception ex)
            {
                DebugLogger.Log($"[Regeneration] Party extraction failed: {ex.Message}");
            }
            if (party?.MobileParty == null || !party.MobileParty.IsBandit) return;

            var warlord = Intelligence.Strategic.WarlordSystem.Instance.GetWarlordForParty(party.MobileParty);
            if (warlord == null) return;

            bool isLeader = agent.Character == party.MobileParty.LeaderHero?.CharacterObject ||
                           (agent.IsHero && agent.Name?.ToString() == party.Name?.ToString());

            bool isElite = agent.Character is CharacterObject charObj && charObj.Tier >= 5;

            if (isLeader || isElite)
            {
                float baseRegen = isLeader ? 2.0f : 0.5f;

                var legLevel = WarlordLegitimacySystem.Instance.GetLevel(warlord.StringId);
                float tierMult = legLevel switch
                {
                    LegitimacyLevel.FamousBandit => 1.0f,
                    LegitimacyLevel.Warlord => 1.5f,
                    LegitimacyLevel.Recognized => 2.5f,
                    _ => 0f
                };

                if (tierMult > 0)
                {
                    float finalRate = baseRegen * tierMult;
                    float healPerTick = (agent.HealthLimit * finalRate) / 100f;

                    _activeWarlords.Add(new RegenAgent
                    {
                        Agent = agent,
                        HealAmountPerTick = healPerTick,
                        Tier = legLevel
                    });
                }
            }
        }

        public override void OnMissionTick(float dt)
        {
            _timeSinceLastTick += dt;
            if (_timeSinceLastTick < TICK_INTERVAL) return;

            _timeSinceLastTick = 0;

            for (int i = _activeWarlords.Count - 1; i >= 0; i--)
            {
                var data = _activeWarlords[i];
                if (!data.Agent.IsActive() || data.Agent.State != AgentState.Active)
                {
                    _activeWarlords.RemoveAt(i);
                    continue;
                }


                if (data.Agent.Health < 20f && data.Agent.Health < data.Agent.HealthLimit)
                {


                    data.Agent.Health += 0.1f;
                }
            }
        }
    }


    public static class MilitiaVictorySystem
    {

        private static readonly HashSet<string> _ascendedCaptains = new();

        public static void Reset() => _ascendedCaptains.Clear();

        private static bool IsLootableBattleGear(ItemObject item)
        {
            if (item.IsFood) return false;
            return item.Value >= 40;
        }

        public static void ProcessVictory(MobileParty winner, MapEvent mapEvent)
        {
            if (winner == null || !winner.IsActive || mapEvent == null) return;

            try
            {


                var territory = BanditMilitias.Systems.Territory.TerritorySystem.Instance;
                var winnerPosition = CompatibilityLayer.GetPartyPosition(winner);
                if (territory?.IsEnabled == true && winnerPosition.IsValid)
                {
                    int casualties = 0;
                    if (mapEvent.StrengthOfSide != null && mapEvent.StrengthOfSide.Length >= 2)
                    {
                        casualties = (int)(mapEvent.StrengthOfSide[0] + mapEvent.StrengthOfSide[1]);
                    }

                }


                if (winner.PartyComponent is MilitiaPartyComponent winnerComp)
                {
                    winnerComp.BattlesWon++;


                    var warlord = Intelligence.Strategic.WarlordSystem.Instance.GetWarlordForParty(winner);


                    try
                    {
                        var battleEvt = BanditMilitias.Core.Events.EventBus.Instance.Get<Core.Events.MilitiaBattleResultEvent>();
                        battleEvt.WinnerParty = winner;


                        var defSide = mapEvent.DefeatedSide;
                        if (defSide != BattleSideEnum.None)
                        {
                            var defParties = mapEvent.GetMapEventSide(defSide)?.Parties;
                            if (defParties != null)
                            {
                                foreach (var dp in defParties)
                                {
                                    if (dp?.Party?.MobileParty == null) continue;
                                    var dmp = dp.Party.MobileParty;
                                    if (dmp.PartyComponent is MilitiaPartyComponent)
                                        battleEvt.LoserHadWarlordParty = true;
                                    if (dmp.LeaderHero?.IsLord == true)
                                        battleEvt.LoserHadLordParty = true;
                                    if (battleEvt.LoserParty == null)
                                        battleEvt.LoserParty = dmp;
                                }
                            }
                        }


                        var winSide = defSide == BattleSideEnum.Attacker ? BattleSideEnum.Defender : BattleSideEnum.Attacker;
                        var winParties = mapEvent.GetMapEventSide(winSide)?.Parties;
                        if (winParties != null)
                        {
                            foreach (var wp in winParties)
                            {
                                if (wp?.Party?.MobileParty == null) continue;
                                var wmp = wp.Party.MobileParty;
                                if (wmp.LeaderHero?.IsLord == true)
                                    battleEvt.WinnerHadLordParty = true;
                                if (wmp.PartyComponent is MilitiaPartyComponent && wmp != winner)
                                    battleEvt.WinnerHadWarlordParty = true;
                            }
                        }


                        if (mapEvent.StrengthOfSide != null && mapEvent.StrengthOfSide.Length >= 2)
                        {
                            var winnerSide = mapEvent.WinningSide;
                            var loserSide = winnerSide == BattleSideEnum.Attacker ? BattleSideEnum.Defender : BattleSideEnum.Attacker;
                            
                            float _winStr = mapEvent.StrengthOfSide[(int)winnerSide];
                            float _loseStr = mapEvent.StrengthOfSide[(int)loserSide];
                            
                            battleEvt.EnemyStrengthRatio = _winStr > 0f ? _loseStr / _winStr : 1f;
                            MilitiaProgressionSystem.Instance.OnBattleVictory(winner, _loseStr, _winStr);
                        }

                        Core.Neural.NeuralEventRouter.Instance.Publish(battleEvt);
                        BanditMilitias.Core.Events.EventBus.Instance.Return(battleEvt);
                    }
                    catch {  }
                }

                if (winner.PrisonRoster != null && winner.PrisonRoster.TotalManCount > 0)
                {


                    int limitSafe = Infrastructure.CompatibilityLayer.GetPartyMemberSizeLimit(winner.Party);
                    int current = winner.MemberRoster?.TotalManCount ?? 0;
                    int space = limitSafe - current;

                    if (space > 0 && winner.MemberRoster != null)
                    {

                        var prisoners = winner.PrisonRoster.GetTroopRoster().ToList();
                        foreach (var p in prisoners)
                        {
                            if (space <= 0) break;
                            if (p.Character == null) continue;

                            if (p.Character.Occupation == Occupation.Bandit || p.Character.Tier <= 3)
                            {
                                int count = Math.Min(space, p.Number);
                                _ = winner.MemberRoster.AddToCounts(p.Character, count);
                                _ = winner.PrisonRoster.AddToCounts(p.Character, -count);
                                space -= count;

                                TelemetryBridge.LogEvent("PrisonerRecruitment", new
                                {
                                    party = winner.Name?.ToString(),
                                    character = p.Character.Name?.ToString(),
                                    count
                                });
                            }
                        }
                    }
                }

                var defeatedSide = mapEvent.DefeatedSide;
                if (defeatedSide == BattleSideEnum.None) return;

                var defeatedParties = mapEvent.GetMapEventSide(defeatedSide)?.Parties;
                if (defeatedParties == null) return;

                List<MobileParty> mergedMilitias = defeatedParties
                    .Where(p => p?.Party?.MobileParty?.PartyComponent is MilitiaPartyComponent)
                    .Select(p => p!.Party.MobileParty)
                    .Where(p => p != null && p != winner)
                    .Distinct()
                    .ToList();

                if (mergedMilitias.Count > 0)
                {
                    var mergeEvt = BanditMilitias.Core.Events.EventBus.Instance.Get<Core.Events.MilitiaMergeEvent>();
                    mergeEvt.ResultingParty = winner;
                    mergeEvt.MergedParties = mergedMilitias;
                    Core.Neural.NeuralEventRouter.Instance.Publish(mergeEvt);
                    BanditMilitias.Core.Events.EventBus.Instance.Return(mergeEvt);
                }

                foreach (var p in defeatedParties)
                {
                    if (p?.Party == null || !p.Party.IsMobile || p.Party.MobileParty == null) continue;

                    MobileParty mobParty = p.Party.MobileParty;


                    int lootedGold = 0;
                    if (mobParty.PartyTradeGold > 0)
                    {
                        lootedGold += mobParty.PartyTradeGold;
                        winner.PartyTradeGold += mobParty.PartyTradeGold;
                        mobParty.PartyTradeGold = 0;
                    }


                    if (mobParty.LeaderHero != null)
                    {
                        int heroGold = mobParty.LeaderHero.Gold;
                        int lootAmount = (int)(heroGold * 0.5f);

                        lootedGold += lootAmount;
                        winner.PartyTradeGold += lootAmount;
                        mobParty.LeaderHero.Gold -= lootAmount;
                    }

                    if (lootedGold > 0 && Settings.Instance?.TestingMode == true)
                    {
                        DebugLogger.TestLog($"[LOOT] {winner.Name} looted {lootedGold} gold from {mobParty.Name} party.", Colors.Yellow);
                    }

                    if (mobParty.ItemRoster == null || winner.ItemRoster == null) continue;


                    var foodItems = mobParty.ItemRoster.Where(item => item.EquipmentElement.Item?.IsFood == true).ToList();
                    foreach (var item in foodItems)
                    {
                        if (item.EquipmentElement.Item == null) continue;
                        int amount = Math.Min(item.Amount, 50);

                        _ = winner.ItemRoster.AddToCounts(item.EquipmentElement, amount);
                    }


                    int gearStacksLooted = 0;
                    int maxStacks = mobParty.IsCaravan ? 20 : 10;


                    for (int i = 0; i < mobParty.ItemRoster.Count && gearStacksLooted < maxStacks; i++)
                    {
                        var stack = mobParty.ItemRoster.GetElementCopyAtIndex(i);
                        ItemObject? lootItem = stack.EquipmentElement.Item;
                        if (lootItem == null || !IsLootableBattleGear(lootItem)) continue;

                        int takeAmount = Math.Max(1, stack.Amount / 2);
                        _ = winner.ItemRoster.AddToCounts(stack.EquipmentElement, takeAmount);
                        gearStacksLooted++;
                    }
                }


                winner.RecentEventsMorale += 10f;
                BanditEnhancementSystem.Instance.EnhanceParty(winner);

                float xpMultiplier = 1.0f;
                if (mapEvent.StrengthOfSide == null || mapEvent.StrengthOfSide.Length < 2)
                {
                    xpMultiplier = 1.0f;
                }
                else
                {
                    float winnerStrength = mapEvent.StrengthOfSide[0];
                    float loserStrength = mapEvent.StrengthOfSide[1];

                    if (winnerStrength < loserStrength * 0.75f)
                    {
                        xpMultiplier = 2.0f;
                    }


                    if (winner.PartyComponent is MilitiaPartyComponent ambushWinnerComp &&
                        ambushWinnerComp.CurrentOrder?.Type == Intelligence.Strategic.CommandType.Ambush)
                    {
                        xpMultiplier *= 1.5f;
                    }
                }

                if (winner.LeaderHero != null && winner.LeaderHero.IsAlive)
                {
                    Hero captain = winner.LeaderHero;

                    int baseXP = 100;
                    if (mapEvent.StrengthOfSide != null && mapEvent.StrengthOfSide.Length >= 2)
                    {
                        float enemyStrength = mapEvent.StrengthOfSide[1];
                        baseXP = (int)(enemyStrength * 20);
                    }

                    int finalXP = (int)(baseXP * xpMultiplier);

                    captain.AddSkillXp(DefaultSkills.Leadership, finalXP);
                    captain.AddSkillXp(DefaultSkills.Tactics, finalXP / 2);

                    if (captain.Level >= 20 && Settings.Instance?.EnableWarlords == true)
                    {

                        bool alreadyAscended = _ascendedCaptains.Contains(captain.StringId);

                        if (!alreadyAscended)
                        {

                            TriggerCaptainAscension(captain, winner);
                        }
                    }

                }

            }
            catch (Exception ex)
            {

                DebugLogger.Log($"[ProcessMilitiaVictory] Error: {ex.Message}");
            }
        }

        private static void TriggerCaptainAscension(Hero captain, MobileParty party)
        {
            try
            {
                if (captain == null) return;

                _ = _ascendedCaptains.Add(captain.StringId);

                var warlord = Intelligence.Strategic.WarlordSystem.Instance.CreateWarlordFromHero(captain);

                if (warlord != null)
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        $"[ASCENSION] {warlord.FullName} claimed rights over {warlord.OwnedSettlement?.Name?.ToString() ?? "new properties"}!",
                        Colors.Magenta));

                    TelemetryBridge.LogEvent("CaptainAscension", new
                    {
                        captain = captain.Name?.ToString() ?? "None",
                        id = captain.StringId,
                        property = warlord.OwnedSettlement?.Name?.ToString() ?? "None"
                    });
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Error("CaptainAscension", $"Failed to promote {captain?.Name}: {ex.Message}");
            }
        }
    }

}
