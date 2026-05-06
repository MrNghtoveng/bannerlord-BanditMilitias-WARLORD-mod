using BanditMilitias.Components;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using System;
using System.Collections.Generic;

namespace BanditMilitias.Systems.AI
{
    public class MilitiaEquipmentManager
    {
        private static readonly Lazy<MilitiaEquipmentManager> _instance = new(() => new MilitiaEquipmentManager());
        public static MilitiaEquipmentManager Instance => _instance.Value;
        private readonly object _policyLock = new();
        private readonly Dictionary<string, CounterDoctrine> _missionDoctrineByPartyId = new();

        public void ApplyMissionEquipmentPolicy(MobileParty party, CounterDoctrine doctrine)
        {
            if (party == null || string.IsNullOrWhiteSpace(party.StringId)) return;

            lock (_policyLock)
            {
                _missionDoctrineByPartyId[party.StringId] = doctrine;
            }
        }

        public bool TryGetMissionEquipmentPolicy(MobileParty? party, out CounterDoctrine doctrine)
        {
            doctrine = CounterDoctrine.Balanced;
            if (party == null || string.IsNullOrWhiteSpace(party.StringId)) return false;

            lock (_policyLock)
            {
                return _missionDoctrineByPartyId.TryGetValue(party.StringId, out doctrine);
            }
        }

        public void ClearMissionEquipmentPolicy(MobileParty? party)
        {
            if (party == null || string.IsNullOrWhiteSpace(party.StringId)) return;

            lock (_policyLock)
            {
                _ = _missionDoctrineByPartyId.Remove(party.StringId);
            }
        }

        public void ResetMissionEquipmentPolicies()
        {
            lock (_policyLock)
            {
                _missionDoctrineByPartyId.Clear();
            }
        }


        public Equipment GetEquipmentForCategory(CharacterObject character, CounterDoctrine doctrine)
        {
            return character.Equipment;
        }
    }


    public class WarlordEquipmentMissionBehavior : MissionBehavior
    {
        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        public override void OnAgentCreated(Agent agent)
        {
            if (agent == null || !agent.IsHuman) return;
            MobileParty? party = TryResolveMobileParty(agent);
            if (party?.PartyComponent is not MilitiaPartyComponent) return;

            if (MilitiaEquipmentManager.Instance.TryGetMissionEquipmentPolicy(party, out CounterDoctrine doctrine))
            {
                ApplyDoctrineBuffs(agent, doctrine);
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

        private static void ApplyDoctrineBuffs(Agent agent, CounterDoctrine doctrine)
        {
        }
    }
}
