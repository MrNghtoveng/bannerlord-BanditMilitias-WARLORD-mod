using System;
using System.Reflection;
using System.Runtime.Serialization;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace BanditMilitias.Tests
{
    /// <summary>
    /// Bannerlord nesnelerini (Settlement, MobileParty vb.) oyun motoru çalışmadan 
    /// taklit etmek için kullanılan yardımcı sınıf.
    /// </summary>
    public static class MockingHub
    {
        /// <summary>
        /// Belirtilen tipten, constructor çalıştırılmadan bir nesne oluşturur.
        /// (Bannerlord'un internal/private constructor kısıtlamalarını aşmak için).
        /// </summary>
        public static T CreateUninitializedObject<T>() where T : class
        {
            return (T)FormatterServices.GetUninitializedObject(typeof(T));
        }

        /// <summary>
        /// Bir nesnenin private/internal alanını (field) zorla ayarlar.
        /// </summary>
        public static void SetPrivateField(object obj, string fieldName, object value)
        {
            var field = obj.GetType().GetField(fieldName, 
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            
            if (field == null)
            {
                // Base class kontrolü
                field = obj.GetType().BaseType?.GetField(fieldName, 
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            }

            field?.SetValue(obj, value);
        }

        /// <summary>
        /// Sahte bir Settlement oluşturur.
        /// </summary>
        public static Settlement CreateFakeSettlement(string stringId, string name, Vec2 position)
        {
            var settlement = CreateUninitializedObject<Settlement>();
            SetPrivateField(settlement, "_stringId", stringId);
            
            // Name için TextObject gerekebilir
            var textObj = new TextObject(name);
            SetPrivateField(settlement, "_name", textObj);
            
            // Position2D (veya GatePosition)
            SetPrivateField(settlement, "_position2D", position);
            
            return settlement;
        }

        /// <summary>
        /// Sahte bir MobileParty oluşturur.
        /// </summary>
        public static MobileParty CreateFakeMobileParty(string stringId, string name)
        {
            var party = CreateUninitializedObject<MobileParty>();
            SetPrivateField(party, "_stringId", stringId);
            
            var textObj = new TextObject(name);
            SetPrivateField(party, "_name", textObj);
            
            // AI ve PartyBase nesnelerini de boş (null olmayan) olarak set edelim
            var partyBase = CreateUninitializedObject<PartyBase>();
            SetPrivateField(party, "_party", partyBase);
            SetPrivateField(partyBase, "_mobileParty", party);

            return party;
        }
    }
}
