using System;
using System.Reflection;
using System.Runtime.Serialization;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace BanditMilitias.Tests
{


    public static class MockingHub
    {


        public static T CreateUninitializedObject<T>() where T : class
        {
            return (T)FormatterServices.GetUninitializedObject(typeof(T));
        }


        public static void SetPrivateField(object obj, string fieldName, object value)
        {
            var field = obj.GetType().GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            if (field == null)
            {


                field = obj.GetType().BaseType?.GetField(fieldName,
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            }

            field?.SetValue(obj, value);
        }


        public static Settlement CreateFakeSettlement(string stringId, string name, Vec2 position)
        {
            var settlement = CreateUninitializedObject<Settlement>();
            SetPrivateField(settlement, "_stringId", stringId);


            var textObj = new TextObject(name);
            SetPrivateField(settlement, "_name", textObj);


            SetPrivateField(settlement, "_position2D", position);

            return settlement;
        }


        public static MobileParty CreateFakeMobileParty(string stringId, string name)
        {
            var party = CreateUninitializedObject<MobileParty>();
            SetPrivateField(party, "_stringId", stringId);

            var textObj = new TextObject(name);
            SetPrivateField(party, "_name", textObj);


            var partyBase = CreateUninitializedObject<PartyBase>();
            SetPrivateField(party, "_party", partyBase);
            SetPrivateField(partyBase, "_mobileParty", party);

            return party;
        }
    }
}
