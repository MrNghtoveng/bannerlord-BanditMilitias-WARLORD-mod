using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BanditMilitias.Infrastructure
{

    public static class CompatibilityLayer
    {


        public static void ForceInitializeAll()
        {
            var fields = typeof(CompatibilityLayer).GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            foreach (var field in fields)
            {
                if (field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(Lazy<>))
                {
                    var value = field.GetValue(null);
                    if (value != null)
                    {
                        var prop = value.GetType().GetProperty("Value");
                        _ = prop?.GetValue(value);
                    }
                }
            }
        }

        private static readonly Lazy<MethodInfo?> _createPartyMethod = new Lazy<MethodInfo?>(() =>
        {
            try
            {

                var method = typeof(MobileParty).GetMethod("CreateParty",
                    new[] { typeof(string), typeof(PartyComponent), typeof(System.Action<MobileParty>) });

                return method ?? typeof(MobileParty).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "CreateParty" && m.GetParameters().Length == 2);
            }
            catch
            {
                return null;
            }
        }, true);

        public static MobileParty? CreateParty(string partyId, PartyComponent component, Action<MobileParty>? initAction = null)
        {

            var method = _createPartyMethod.Value;

            if (initAction == null)
            {
                return MobileParty.CreateParty(partyId, component);
            }

            if (method == null) return null;

            var parameters = method.GetParameters();
            object? result = null;

            if (parameters.Length == 3)
            {

                result = method.Invoke(null, [partyId, component, initAction]);
            }
            else if (parameters.Length == 2)
            {

                result = method.Invoke(null, [partyId, component]);
                if (result is MobileParty legacyParty && initAction != null)
                {
                    initAction(legacyParty);
                }
            }

            return result as MobileParty;
        }

        public static void DestroyParty(MobileParty party)
        {

            if (party == null || party.Party == null) return;

            if (party.ActualClan != null && party.MapFaction != null)
            {
                DestroyPartyAction.Apply(null, party);
            }
            else
            {

                party.IsActive = false;
                party.IsVisible = false;
            }
        }

        public static MobileParty? CreatePartySafe(string partyId, PartyComponent component, Clan? faction, bool forceLootersIfNull = true)
        {
            if (TaleWorlds.CampaignSystem.Campaign.Current == null) return null;

            if (faction == null && forceLootersIfNull)
            {

                faction = BanditMilitias.Infrastructure.ClanCache.GetLootersClan()!;
            }

            if (faction == null)
            {
                LogCompatibilityWarning($"Faction is null for partyId {partyId}. Spawn cancelled.", null);
                return null;
            }

            void Init(MobileParty p)
            {
                p.ActualClan = faction;
                p.Ai?.SetDoNotMakeNewDecisions(true);
            }

            MobileParty? party = CreateParty(partyId, component, Init);

            if (party == null)
            {
                LogCompatibilityWarning($"CreateParty returned null for {partyId}. This might be due to API mismatch.", null);
                return null;
            }

            if (party.ActualClan == null)
            {
                LogCompatibilityWarning($"Party {partyId} was created but ActualClan is null. Destroying it.", null);
                DestroyParty(party);
                return null;
            }

            return party;
        }

        private static readonly Lazy<PropertyInfo?> _partySizeLimitProp = new Lazy<PropertyInfo?>(() =>
        {
            var type = typeof(PartyBase);


            return type.GetProperty("LimitedPartySize", BindingFlags.Public | BindingFlags.Instance) ??
                   type.GetProperty("PartySizeLimit", BindingFlags.Public | BindingFlags.Instance);
        }, true);


        public static int GetPartyMemberSizeLimit(PartyBase party)
        {
            if (party == null) return 0;

            try
            {
                var prop = _partySizeLimitProp.Value;
                if (prop != null)
                {
                    object? val = prop.GetValue(party);
                    if (val is int limit) return limit;
                }
            }
            catch (Exception ex)
            {
                LogCompatibilityWarning("GetPartyMemberSizeLimit", ex);
            }


            return 50;
        }

        private static readonly Lazy<Func<MobileParty, Vec2>?> _getPartyPositionDelegate = new Lazy<Func<MobileParty, Vec2>?>(() =>
        {
            try
            {
                var type = typeof(MobileParty);


                PropertyInfo? prop = type.GetProperty("GetPosition2D") ??
                                     type.GetProperty("VisualPosition2DWithoutError") ??
                                     type.GetProperty("Position2D");

                if (prop != null && prop.PropertyType == typeof(Vec2))
                {
                    var param = System.Linq.Expressions.Expression.Parameter(type, "p");
                    var access = System.Linq.Expressions.Expression.Property(param, prop);
                    var lambda = System.Linq.Expressions.Expression.Lambda<Func<MobileParty, Vec2>>(access, param);
                    return lambda.Compile();
                }


                var method = type.GetMethod("GetPosition2D", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (method != null && method.ReturnType == typeof(Vec2))
                {
                    var param = System.Linq.Expressions.Expression.Parameter(type, "p");
                    var call = System.Linq.Expressions.Expression.Call(param, method);
                    var lambda = System.Linq.Expressions.Expression.Lambda<Func<MobileParty, Vec2>>(call, param);
                    return lambda.Compile();
                }

                return null;
            }
            catch (Exception ex)
            {
                LogCompatibilityWarning("GetPartyPosition.DelegateInit", ex);
                return null;
            }
        }, true);
        private static bool _partyPositionFastPathDisabled = false;

        public static Vec2 GetPartyPosition(MobileParty party)
        {
            if (party == null) return Vec2.Invalid;

            Vec2 result = Vec2.Invalid;
            var fastPathDelegate = _partyPositionFastPathDisabled ? null : _getPartyPositionDelegate.Value;

            if (fastPathDelegate != null)
            {
                try
                {
                    result = fastPathDelegate(party);

                    if (!float.IsNaN(result.X) && !float.IsNaN(result.Y))
                    {
                        return result;
                    }
                }
                catch (Exception ex)
                {

                    _partyPositionFastPathDisabled = true;
                    ExceptionMonitor.Capture(
                        "CompatibilityLayer.GetPartyPosition.FastPath",
                        ex,
                        userVisible: true,
                        notifyCooldownMinutes: 120);
                }
            }

            result = GetPartyPositionSlow(party);

            if (float.IsNaN(result.X) || float.IsNaN(result.Y))
            {

                try
                {
                    if (party.Party != null)
                    {

                        var visualsProp = typeof(PartyBase).GetProperty("Visuals");
                        if (visualsProp != null)
                        {
                            var visuals = visualsProp.GetValue(party.Party);
                            if (visuals != null)
                            {

                                var getPosMethod = visuals.GetType().GetMethod("GetPosition");
                                if (getPosMethod != null)
                                {

                                    var posObj = getPosMethod.Invoke(visuals, null);
                                    if (posObj is TaleWorlds.Library.Vec3 v3)
                                    {
                                        return new Vec2(v3.X, v3.Y);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogCompatibilityWarning("GetPartyPosition.VisualFallback", ex);
                }

                return Vec2.Invalid;
            }

            return result;
        }

        private static Vec2 GetPartyPositionSlow(MobileParty party)
        {
            try
            {


                var prop = typeof(MobileParty).GetProperty("GetPosition2D") ??
                           typeof(MobileParty).GetProperty("VisualPosition2DWithoutError") ??
                           typeof(MobileParty).GetProperty("Position2D");

                if (prop != null)
                {
                    object? val = prop.GetValue(party);
                    if (val is Vec2 v2) return v2;
                    if (val != null)
                    {
                        var converted = ToVec2(val);
                        if (converted.IsValid) return converted;
                    }
                }


                var method = typeof(MobileParty).GetMethod("GetPosition2D", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (method != null && method.ReturnType == typeof(Vec2))
                {
                    var result = method.Invoke(party, null);
                    if (result is Vec2 mv) return mv;
                }
            }
            catch (Exception ex)
            {
                LogCompatibilityWarning("GetPartyPositionSlow.Primary", ex);
            }

            try
            {
                var prop = typeof(MobileParty).GetProperty("Position");
                if (prop != null)
                {
                    var val = prop.GetValue(party);
                    if (val is Vec2 v2direct) return v2direct;
                    if (val is Vec3 v3) return new Vec2(v3.X, v3.Y);


                    if (val != null)
                    {
                        var converted = ToVec2(val);
                        if (converted.IsValid) return converted;
                    }
                }
            }
            catch (Exception ex)
            {
                LogCompatibilityWarning("GetPartyPositionSlow.Position", ex);
            }

            if (party.Party != null)
            {
                try
                {
                    var prop = typeof(PartyBase).GetProperty("GetPosition2D") ??
                               typeof(PartyBase).GetProperty("Position2D");
                    if (prop != null)
                    {
                        object? val = prop.GetValue(party.Party);
                        if (val is Vec2 v2) return v2;
                    }
                }
                catch (Exception ex)
                {
                    LogCompatibilityWarning("GetPartyPositionSlow.PartyBase", ex);
                }
            }

            return Vec2.Invalid;
        }

        private static readonly Lazy<Func<Settlement, Vec2>?> _getSettlementPositionDelegate = new Lazy<Func<Settlement, Vec2>?>(() =>
        {
            try
            {
                var gatePosProp = typeof(Settlement).GetProperty("GatePosition");
                if (gatePosProp == null) return null;

                var gateType = gatePosProp.PropertyType;
                var xProp = gateType.GetProperty("X");
                var yProp = gateType.GetProperty("Y");

                if (xProp == null || yProp == null) return null;

                var param = System.Linq.Expressions.Expression.Parameter(typeof(Settlement), "s");
                var gateAccess = System.Linq.Expressions.Expression.Property(param, gatePosProp);
                var xAccess = System.Linq.Expressions.Expression.Property(gateAccess, xProp);
                var yAccess = System.Linq.Expressions.Expression.Property(gateAccess, yProp);

                var xCast = xAccess.Type == typeof(float)
                    ? (System.Linq.Expressions.Expression)xAccess
                    : System.Linq.Expressions.Expression.Convert(xAccess, typeof(float));
                var yCast = yAccess.Type == typeof(float)
                    ? (System.Linq.Expressions.Expression)yAccess
                    : System.Linq.Expressions.Expression.Convert(yAccess, typeof(float));

                var vec2Ctor = typeof(Vec2).GetConstructor(new[] { typeof(float), typeof(float) });
                if (vec2Ctor == null) return null;

                var newVec2 = System.Linq.Expressions.Expression.New(vec2Ctor, xCast, yCast);
                var lambda = System.Linq.Expressions.Expression.Lambda<Func<Settlement, Vec2>>(newVec2, param);
                return lambda.Compile();
            }
            catch
            {
                return null;
            }
        }, true);

        public static Vec2 GetSettlementPosition(Settlement settlement)
        {
            if (settlement == null) return Vec2.Invalid;

            try
            {
                var del = _getSettlementPositionDelegate.Value;
                if (del != null) return del(settlement);
            }
            catch (Exception ex)
            {
                if (Settings.Instance?.TestingMode == true)
                {
                    TaleWorlds.Library.Debug.Print($"[CompatibilityLayer] GetSettlementPosition failed: {ex.Message}");
                }
            }

            try
            {
                var pos2d = typeof(Settlement).GetProperty("Position2D");
                if (pos2d != null)
                {
                    object? val = pos2d.GetValue(settlement);
                    if (val is Vec2 v2) return v2;
                }
            }
            catch (Exception ex)
            {
                LogCompatibilityWarning("GetSettlementPosition.Position2D", ex);
            }

            return Vec2.Invalid;
        }

        public static TerrainType GetTerrainType(MobileParty party)
        {
            if (party == null) return TerrainType.Plain;
            try
            {
                if (Campaign.Current?.MapSceneWrapper != null)
                {
                    return Campaign.Current.MapSceneWrapper.GetTerrainTypeAtPosition(CreateCampaignVec2(GetPartyPosition(party)));
                }
            }
            catch { }
            return TerrainType.Plain;
        }

        public static Vec3 GetHeroPosition(Hero hero)
        {
            if (hero == null) return Vec3.Invalid;
            if (hero.CurrentSettlement != null)
                return new Vec3(GetSettlementPosition(hero.CurrentSettlement).X, GetSettlementPosition(hero.CurrentSettlement).Y, 0f);
            if (hero.PartyBelongedTo != null)
                return new Vec3(GetPartyPosition(hero.PartyBelongedTo).X, GetPartyPosition(hero.PartyBelongedTo).Y, 0f);
            return Vec3.Invalid;
        }

        internal static Vec2 ToVec2(object campaignVec2)
        {
            if (campaignVec2 == null) return Vec2.Invalid;
            try
            {
                var type = campaignVec2.GetType();
                var xProp = type.GetProperty("X");
                var yProp = type.GetProperty("Y");
                if (xProp != null && yProp != null)
                {
                    float x = (float?)xProp.GetValue(campaignVec2) ?? 0f;
                    float y = (float?)yProp.GetValue(campaignVec2) ?? 0f;
                    return new Vec2(x, y);
                }
            }
            catch (Exception ex)
            {
                if (Settings.Instance?.TestingMode == true)
                {
                    TaleWorlds.Library.Debug.Print($"[CompatibilityLayer] ToVec2 conversion failed: {ex.Message}");
                }
            }
            return Vec2.Invalid;
        }

        public static bool IsTroopRosterEmpty(TroopRoster? roster)
        {
            return roster == null || roster.TotalManCount <= 0;
        }

        public static int GetTotalWoundedTroops(TroopRoster? roster)
        {
            if (roster == null) return 0;

            try
            {
                var prop = typeof(TroopRoster).GetProperty("TotalWoundedTroops");
                if (prop?.GetValue(roster) is int wounded)
                    return wounded;
            }
            catch (Exception ex)
            {
                LogCompatibilityWarning("GetTotalWoundedTroops", ex);
            }

            int total = 0;
            for (int i = 0; i < roster.Count; i++)
            {
                var element = roster.GetElementCopyAtIndex(i);
                total += Math.Max(0, element.WoundedNumber);
            }

            return total;
        }

        public static void HealWoundedTroops(TroopRoster? roster, int count)
        {
            if (roster == null || count <= 0) return;

            try
            {
                var method = typeof(TroopRoster).GetMethod("HealWoundedTroops", new[] { typeof(int) });
                if (method != null)
                {
                    _ = method.Invoke(roster, new object[] { count });
                    return;
                }
            }
            catch (Exception ex)
            {
                LogCompatibilityWarning("HealWoundedTroops", ex);
            }

            int remaining = count;
            for (int i = 0; i < roster.Count && remaining > 0; i++)
            {
                var element = roster.GetElementCopyAtIndex(i);
                if (element.Character == null || element.WoundedNumber <= 0) continue;

                int healAmount = Math.Min(element.WoundedNumber, remaining);
                if (healAmount <= 0) continue;

                roster.AddToCounts(element.Character, 0, insertAtFront: false, woundedCount: -healAmount, xpChange: 0, removeDepleted: false, index: i);
                remaining -= healAmount;
            }
        }

        public static int GetElementXpAtIndex(TroopRoster? roster, int index)
        {
            if (roster == null || index < 0 || index >= roster.Count) return 0;

            try
            {
                var method = typeof(TroopRoster).GetMethod("GetElementXpAtIndex", new[] { typeof(int) });
                if (method?.Invoke(roster, new object[] { index }) is int xp)
                    return xp;
            }
            catch (Exception ex)
            {
                LogCompatibilityWarning("GetElementXpAtIndex", ex);
            }

            return 0;
        }

        public static void SetAgentBaseSpeedMultiplier(Agent? agent, float multiplier)
        {
            if (agent == null) return;

            try
            {
                var method = typeof(Agent).GetMethod("SetBaseSpeedMultiplier", new[] { typeof(float) });
                if (method != null)
                {
                    _ = method.Invoke(agent, new object[] { multiplier });
                    return;
                }
            }
            catch (Exception ex)
            {
                LogCompatibilityWarning("SetAgentBaseSpeedMultiplier", ex);
            }

            try
            {
                var method = typeof(Agent).GetMethod("SetMaximumSpeedLimit", new[] { typeof(float), typeof(bool) });
                if (method != null)
                {
                    _ = method.Invoke(agent, new object[] { multiplier, false });
                }
            }
            catch (Exception ex)
            {
                LogCompatibilityWarning("SetMaximumSpeedLimitFallback", ex);
            }
        }

        private static readonly Lazy<Func<PartyBase, float>?> _totalStrengthDelegateLazy =
            new Lazy<Func<PartyBase, float>?>(() =>
            {
                try
                {
                    var prop = typeof(PartyBase).GetProperty("TotalStrength");
                    if (prop == null || prop.PropertyType != typeof(float)) return null;

                    var param = System.Linq.Expressions.Expression.Parameter(typeof(PartyBase), "p");
                    var propAccess = System.Linq.Expressions.Expression.Property(param, prop);
                    return System.Linq.Expressions.Expression.Lambda<Func<PartyBase, float>>(propAccess, param).Compile();
                }
                catch { return null; }
            }, true);

        public static float GetTotalStrength(MobileParty party)
        {
            try
            {
                if (party != null && party.Party != null)
                {
                    var del = _totalStrengthDelegateLazy.Value;
                    if (del != null)
                    {
                        return del(party.Party);
                    }
                }
            }
            catch (Exception ex)
            {
                if (Settings.Instance?.TestingMode == true)
                {
                    TaleWorlds.Library.Debug.Print($"[CompatibilityLayer] TotalStrength delegate failed, using fallback: {ex.Message}");
                }
            }

            if (party != null && party.MemberRoster != null)
            {
                return party.MemberRoster.TotalManCount;
            }
            return 0f;
        }

        public static float GetPartySpeed(MobileParty party)
        {
            if (party == null) return 1.5f;
            try { return System.Math.Max(0.1f, party.Speed); }
            catch (Exception ex)
            {
                if (Settings.Instance?.TestingMode == true)
                    TaleWorlds.Library.Debug.Print($"[CompatibilityLayer] GetPartySpeed failed for {party.StringId}: {ex.Message}");
                return 1.5f;
            }
        }

        private static readonly Lazy<MethodInfo?> _setMoveGoToPointLazy = new Lazy<MethodInfo?>(() =>
        {

            var method = typeof(MobileParty).GetMethod("SetMoveGoToPoint", [typeof(Vec2)]);

            if (method == null)
            {
                var methods = typeof(MobileParty).GetMethods().Where(m => m.Name == "SetMoveGoToPoint").ToList();
                foreach (var m in methods)
                {
                    var paramsInfo = m.GetParameters();
                    if (paramsInfo.Length >= 1 && paramsInfo[0].ParameterType.Name == "CampaignVec2")
                    {
                        return m;
                    }
                }
            }
            return method;
        });

        public static object? CreateCampaignVec2Compat(Type campaignVec2Type, Vec2 point)
        {

            var ctor2 = campaignVec2Type.GetConstructor([typeof(Vec2), typeof(bool)]);
            if (ctor2 != null)
            {
                return ctor2.Invoke([point, true]);
            }

            var ctor1 = campaignVec2Type.GetConstructor([typeof(Vec2)]);
            if (ctor1 != null)
            {
                return ctor1.Invoke([point]);
            }

            return null;
        }

        private static object? GetEnumDefault(Type enumType)
        {
            if (!enumType.IsEnum) return null;
            return Enum.GetValues(enumType).GetValue(0);
        }

        public static void SetMoveGoToPoint(MobileParty party, Vec2 point)
        {
            if (party == null || !point.IsValid) return;

            try
            {
                var method = _setMoveGoToPointLazy.Value;

                if (method != null)
                {
                    var parameters = method.GetParameters();
                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(Vec2))
                    {
                        _ = method.Invoke(party, [point]);
                    }
                    else if (parameters.Length >= 1 && parameters[0].ParameterType.Name == "CampaignVec2")
                    {
                        var campaignVec2Type = parameters[0].ParameterType;
                        object? campaignVec2 = CreateCampaignVec2Compat(campaignVec2Type, point);
                        if (campaignVec2 == null) return;

                        if (parameters.Length == 1)
                        {
                            _ = method.Invoke(party, [campaignVec2]);
                        }
                        else if (parameters.Length == 2)
                        {
                            object? nav = GetEnumDefault(parameters[1].ParameterType);
                            _ = method.Invoke(party, [campaignVec2, nav]);
                        }
                        else if (parameters.Length == 3)
                        {
                            object? nav = GetEnumDefault(parameters[1].ParameterType);
                            _ = method.Invoke(party, [campaignVec2, nav, false]);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (Settings.Instance?.TestingMode == true)
                    TaleWorlds.Library.Debug.Print($"[CompatibilityLayer] SetMoveGoToPoint failed: {ex.Message}");
            }
        }

        private static readonly Lazy<(MethodInfo? Method, bool UseAi)> _setMoveGoToSettlementLazy = new Lazy<(MethodInfo?, bool)>(() =>
        {

            var method = typeof(MobileParty).GetMethod("SetMoveGoToSettlement");
            if (method != null) return (method, false);

            var aiType = typeof(MobileParty).GetProperty("Ai")?.PropertyType;
            if (aiType != null)
            {
                method = aiType.GetMethod("SetMoveGoToSettlement");
                if (method != null) return (method, true);
            }
            return (null, false);
        });

        public static void SetMoveGoToSettlement(MobileParty party, Settlement settlement)
        {
            if (party == null || settlement == null) return;

            try
            {
                var (method, useAi) = _setMoveGoToSettlementLazy.Value;
                if (method == null) return;

                object targetObj = useAi ? party.Ai : party;
                if (targetObj == null) return;

                var parameters = method.GetParameters();

                if (parameters.Length == 1)
                {
                    _ = method.Invoke(targetObj, [settlement]);
                }
                else if (parameters.Length >= 2)
                {

                    var navType = parameters[1].ParameterType;
                    object? standardNav = Enum.GetValues(navType).GetValue(0);

                    if (parameters.Length == 2)
                    {
                        _ = method.Invoke(targetObj, [settlement, standardNav]);
                    }
                    else if (parameters.Length == 3)
                    {

                        _ = method.Invoke(targetObj, [settlement, standardNav, false]);
                    }
                }
            }
            catch (Exception ex)
            {
                if (BanditMilitias.Settings.Instance?.TestingMode == true)
                    TaleWorlds.Library.Debug.Print($"[CompatibilityLayer] SetMoveGoToSettlement failed: {ex.Message}");
            }
        }

        private static readonly Lazy<MethodInfo?> _setMoveEngagePartyLazy = new Lazy<MethodInfo?>(() =>
        {
            return typeof(MobileParty).GetMethod("SetMoveEngageParty", [typeof(MobileParty)]);
        });

        public static void SetMoveEngageParty(MobileParty party, MobileParty target)
        {
            if (party == null || target == null) return;

            try
            {
                var method = _setMoveEngagePartyLazy.Value;

                if (method != null)
                {
                    _ = method.Invoke(party, [target]);
                }
                else
                {

                    SetMoveGoToPoint(party, GetPartyPosition(target));
                }
            }
            catch (Exception ex)
            {

                if (Settings.Instance?.TestingMode == true)
                {
                    TaleWorlds.Library.Debug.Print($"[CompatibilityLayer] SetMoveEngageParty failed: {ex.Message}");
                }
            }
        }

        private static readonly Lazy<(MethodInfo? Method, bool UseAi)> _setMoveRaidLazy = new Lazy<(MethodInfo?, bool)>(() =>
        {
            var method = typeof(MobileParty).GetMethods().FirstOrDefault(m => m.Name == "SetMoveRaid");
            if (method != null) return (method, false);

            var aiType = typeof(MobileParty).GetProperty("Ai")?.PropertyType;
            if (aiType != null)
            {
                method = aiType.GetMethods().FirstOrDefault(m => m.Name == "SetMoveRaid");
                if (method != null) return (method, true);
            }
            return (null, false);
        }, true);

        public static void SetMoveRaidSettlement(MobileParty party, Settlement target)
        {
            if (party == null || target == null) return;
            try
            {
                var (method, useAi) = _setMoveRaidLazy.Value;
                if (method != null)
                {
                    object targetObj = useAi ? party.Ai : party;
                    if (targetObj != null)
                    {
                        _ = method.Invoke(targetObj, new object[] { target });
                        return;
                    }
                }
                else
                {
                    SetMoveGoToSettlement(party, target);
                }
            }
            catch (Exception ex)
            {
                LogCompatibilityWarning("SetMoveRaidSettlement", ex);
                SetMoveGoToSettlement(party, target);
            }
        }
        private static readonly Lazy<(MethodInfo? Method, bool UseAi)> _setMovePatrolSettlementLazy =
            new Lazy<(MethodInfo?, bool)>(() =>
        {
            var methods = typeof(MobileParty).GetMethods().Where(m => m.Name == "SetMovePatrolAroundSettlement").ToArray();
            if (methods.Length > 0) return (methods[0], false);

            var aiProp = typeof(MobileParty).GetProperty("Ai");
            var aiType = aiProp?.PropertyType;
            if (aiType != null)
            {
                var aiMethods = aiType.GetMethods().Where(m => m.Name == "SetMovePatrolAroundSettlement").ToArray();
                if (aiMethods.Length > 0) return (aiMethods[0], true);
            }
            return (null, false);
        }, true);

        public static void SetMovePatrolAroundSettlement(MobileParty party, Settlement target)
        {
            if (party == null || target == null) return;
            try
            {
                var (selectedMethod, useAi) = _setMovePatrolSettlementLazy.Value;
                object? targetInstance = useAi ? (object?)party.Ai : party;

                if (selectedMethod != null && targetInstance != null)
                {
                    var parameters = selectedMethod.GetParameters();
                    var args = new object[parameters.Length];
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        var pType = parameters[i].ParameterType;
                        if (pType == typeof(Settlement)) args[i] = target;
                        else if (pType.IsEnum) args[i] = GetEnumDefault(pType)!;
                        else if (pType == typeof(bool)) args[i] = false;
                        else if (pType == typeof(float)) args[i] = 0f;
                        else if (pType == typeof(int)) args[i] = 0;
                        else args[i] = pType.IsValueType ? Activator.CreateInstance(pType)! : null!;
                    }

                    _ = selectedMethod.Invoke(targetInstance, args);
                    return;
                }

                string warn = $"[CompatibilityLayer] SetMovePatrolAroundSettlement: metod bulunamadý, DefaultBehavior None kalabilir! Party: {party.StringId}";
                LogCompatibilityWarning("SetMovePatrolAroundSettlement_NotFound", null);
                try { FileLogger.LogError(warn); } catch { }

                SetMoveGoToSettlement(party, target);
            }
            catch (Exception ex)
            {
                string err = $"[CompatibilityLayer] SetMovePatrolAroundSettlement: {ex.GetType().Name}: {ex.Message}";
                LogCompatibilityWarning("SetMovePatrolAroundSettlement", ex);
                try { FileLogger.LogError(err); } catch { }
                SetMoveGoToSettlement(party, target);
            }
        }

        private static readonly Lazy<MethodInfo?> _setMoveDefendLazy = new Lazy<MethodInfo?>(() =>
        {
            return typeof(MobileParty).GetMethod("SetMoveDefendSettlement");
        }, true);

        public static void SetMoveDefendSettlement(MobileParty party, Settlement target)
        {
            if (party == null || target == null) return;
            try
            {
                var method = _setMoveDefendLazy.Value;
                if (method != null)
                {
                    _ = method.Invoke(party, new object[] { target });
                }
                else
                {
                    SetMoveGoToSettlement(party, target);
                }
            }
            catch (Exception ex)
            {
                LogCompatibilityWarning("SetMoveDefendSettlement", ex);
                SetMoveGoToSettlement(party, target);
            }
        }

        private static readonly Lazy<MethodInfo?> _setMovePatrolPointLazy = new Lazy<MethodInfo?>(() =>
        {
            var methods = typeof(MobileParty).GetMethods().Where(m => m.Name == "SetMovePatrolAroundPoint").ToList();
            if (!methods.Any()) return null;

            return methods.FirstOrDefault(x =>
            {
                var p = x.GetParameters();
                return p.Length == 1 && p[0].ParameterType.Name == "Vec2";
            }) ?? methods.FirstOrDefault(x =>
            {
                var p = x.GetParameters();
                return p.Length >= 1 && p[0].ParameterType.Name == "CampaignVec2";
            });
        }, true);

        public static void SetMovePatrolAroundPoint(MobileParty party, Vec2 target)
        {
            if (party == null || !target.IsValid) return;
            try
            {
                var m = _setMovePatrolPointLazy.Value;
                if (m != null)
                {
                    var parameters = m.GetParameters();
                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(Vec2))
                    {
                        _ = m.Invoke(party, new object[] { target });
                        return;
                    }
                    else if (parameters.Length >= 1 && parameters[0].ParameterType.Name == "CampaignVec2")
                    {
                        var campaignVec2Type = parameters[0].ParameterType;
                        object? campaignVec2 = CreateCampaignVec2Compat(campaignVec2Type, target);
                        if (campaignVec2 != null)
                        {
                            if (parameters.Length == 1)
                            {
                                _ = m.Invoke(party, new object[] { campaignVec2 });
                                return;
                            }
                            else if (parameters.Length == 2)
                            {
                                object? standardNav = GetEnumDefault(parameters[1].ParameterType);
                                _ = m.Invoke(party, new object[] { campaignVec2, standardNav! });
                                return;
                            }
                            else if (parameters.Length == 3)
                            {
                                object? standardNav = GetEnumDefault(parameters[1].ParameterType);
                                _ = m.Invoke(party, new object[] { campaignVec2, standardNav!, false });
                                return;
                            }
                        }
                    }
                }

                SetMoveGoToPoint(party, target);
            }
            catch (Exception ex)
            {
                LogCompatibilityWarning("SetMovePatrolAroundPoint", ex);
                SetMoveGoToPoint(party, target);
            }
        }

        private static readonly Lazy<MethodInfo?> _giveGoldMethod = new Lazy<MethodInfo?>(() =>
        {
            var type = Type.GetType("TaleWorlds.CampaignSystem.Actions.GiveGoldAction, TaleWorlds.CampaignSystem");
            return type?.GetMethod("ApplyBetweenCharacters", BindingFlags.Public | BindingFlags.Static);
        }, true);

        public static void GiveGoldSafe(Hero receiver, int amount)
        {
            if (receiver == null || amount <= 0) return;

            try
            {
                MethodInfo? giveGoldMethod = _giveGoldMethod.Value;
                if (giveGoldMethod != null)
                {
                    var paramCount = giveGoldMethod.GetParameters().Length;

                    object?[]? args = null;
                    if (paramCount == 4) args = [null, receiver, amount, false];
                    else if (paramCount == 3) args = [null, receiver, amount];

                    if (args != null)
                    {
                        _ = giveGoldMethod.Invoke(null, args);
                        return;
                    }
                }

                if (receiver.Gold >= 0)
                {

                    var goldProp = typeof(Hero).GetProperty("Gold");
                    if (goldProp != null && goldProp.CanWrite)
                    {
                        goldProp.SetValue(receiver, receiver.Gold + amount);
                    }
                }
            }
            catch (Exception ex)
            {

                TaleWorlds.Library.Debug.Print($"[BanditMilitias] GiveGoldSafe Failed: {ex.Message}");
            }
        }

        public static CampaignVec2 CreateCampaignVec2(Vec2 position, bool isOnLand = true)
        {
            return new CampaignVec2(position, isOnLand);
        }

        private static CampaignTime? _cachedStartTime = null;
        private static bool _campaignStartTimeNotSupported = false;
        private static System.Reflection.PropertyInfo? _campaignStartTimeProp = null;

        public static string GetGameVersion()
        {
            try
            {
                var version = TaleWorlds.Library.ApplicationVersion.FromParametersFile();
                return version.ToString();
            }
            catch
            {
                return "Unknown";
            }
        }

        public static void Reset()
        {
            _cachedStartTime = null;
            _campaignStartTimeNotSupported = false;
            _campaignStartTimeProp = null;
            _isACGDetected = null;
            ModActivationManager.Reset();

            // [v1.3.15] Oyun sürümünü her zaman logla — uyumluluk sorunlarını erken tespit için
            try
            {
                string gameVer = GetGameVersion();
                FileLogger.Log($"[CompatibilityLayer] Reset. Game Version: {gameVer} | Target: 1.3.15");
            }
            catch { /* Logger hazır olmayabilir */ }

            if (Settings.Instance?.TestingMode == true)
            {
                FileLogger.Log($"[CompatibilityLayer] Game Version detected: {GetGameVersion()}");
            }
        }



        public static void UpdateAllPartiesNextThinkTime(CampaignTime time)
        {
            try
            {
                int count = 0;
                var militias = ModuleManager.Instance.ActiveMilitias;
                if (militias == null) return;

                foreach (var party in militias)
                {
                    if (party != null && party.IsActive && party.PartyComponent is BanditMilitias.Components.MilitiaPartyComponent comp)
                    {


                        if (comp.NextThinkTime == CampaignTime.Zero || comp.NextThinkTime < time)
                        {
                            comp.NextThinkTime = time;
                            count++;
                        }
                    }
                }

                if (count > 0)
                {
                    FileLogger.Log($"Sync: Adjusted NextThinkTime for {count} militias to {time.ToHours:F1}h.");
                }
            }
            catch (Exception ex)
            {
                LogCompatibilityWarning("UpdateAllPartiesNextThinkTime", ex);
            }
        }


        public static CampaignTime GetCampaignStartTime()
        {
            if (_cachedStartTime.HasValue) return _cachedStartTime.Value;
            if (_campaignStartTimeNotSupported) return CampaignTime.Zero;

            CampaignTime result = CampaignTime.Zero;

            if (Campaign.Current != null)
            {
                try
                {
                    if (_campaignStartTimeProp == null)
                    {
                        _campaignStartTimeProp = typeof(Campaign).GetProperty("CampaignStartTime",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    }

                    if (_campaignStartTimeProp != null)
                    {
                        var val = _campaignStartTimeProp.GetValue(Campaign.Current);
                        if (val is CampaignTime t && t != CampaignTime.Zero)
                            result = t;
                    }
                    else
                    {
                        _campaignStartTimeNotSupported = true;
                    }
                }
                catch (Exception ex)
                {
                    _campaignStartTimeNotSupported = true;
                    LogCompatibilityWarning("GetCampaignStartTime reflection failed", ex);
                }
            }

            if (result != CampaignTime.Zero)
                _cachedStartTime = result;

            return result;
        }

        public static IEnumerable<MobileParty> GetSafeMobileParties()
        {
            try
            {
                return MobileParty.All ?? Enumerable.Empty<MobileParty>();
            }
            catch (Exception ex)
            {
                LogCompatibilityWarning("GetSafeMobileParties", ex);
                return Enumerable.Empty<MobileParty>();
            }
        }

        public static IEnumerable<Settlement> GetSafeSettlements()
        {
            try
            {
                return Settlement.All ?? Enumerable.Empty<Settlement>();
            }
            catch (Exception ex)
            {
                LogCompatibilityWarning("GetSafeSettlements", ex);
                return Enumerable.Empty<Settlement>();
            }
        }



        private static readonly Lazy<MethodInfo?> _agentCrashGuardWarningMethod = new(() =>
        {
            try
            {
                return Type.GetType("AgentCrashGuard.DiagnosticLogger, AgentCrashGuard")
                    ?.GetMethod("Warning", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(string) }, null);
            }
            catch
            {
                return null;
            }
        }, true);

        private static readonly Lazy<MethodInfo?> _agentCrashGuardLogEventMethod = new(() =>
        {
            try
            {
                return Type.GetType("AgentCrashGuard.Systems.AIDataFactory, AgentCrashGuard")
                    ?.GetMethod("LogEvent", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(string) }, null);
            }
            catch
            {
                return null;
            }
        }, true);

        public static void TryRelayExternalDiagnosticEvent(string eventName, string payload)
        {
            try
            {
                MethodInfo? method = _agentCrashGuardLogEventMethod.Value;
                if (method != null)
                {
                    _ = method.Invoke(null, new object[] { eventName, payload });
                }
            }
            catch
            {
            }
        }

        private static void LogCompatibilityWarning(string operation, Exception? ex)
        {
            string detail = ex?.Message ?? "n/a";
            if (Settings.Instance?.TestingMode == true)
            {
                TaleWorlds.Library.Debug.Print($"[CompatibilityLayer] {operation} failed: {detail}");
            }
            TryAgentCrashGuardLog($"WARNING [{operation}] {detail} (Type: {ex?.GetType().Name})");
        }

        private static void TryAgentCrashGuardLog(string msg)
        {
            try
            {
                MethodInfo? method = _agentCrashGuardWarningMethod.Value;
                if (method != null)
                {
                    _ = method.Invoke(null, new object[] { "BanditMilitias_Compat", msg });
                }
            }
            catch { }
        }

        public static bool IsModActive(string modId)
        {
            if (string.IsNullOrWhiteSpace(modId)) return false;
            try
            {
                var modules = TaleWorlds.ModuleManager.ModuleHelper.GetModules();
                if (modules == null) return false;
                foreach (var mod in modules)
                {
                    if (mod != null && (mod.Id == modId || mod.Name == modId))
                    {
                        return true;
                    }
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool? _isACGDetected = null;
        public static bool IsAgentCrashGuardLoaded()
        {
            if (_isACGDetected.HasValue) return _isACGDetected.Value;

            try
            {


                var type = Type.GetType("AgentCrashGuard.DiagnosticLogger, AgentCrashGuard");
                if (type != null)
                {
                    _isACGDetected = true;
                    return true;
                }


                var modules = TaleWorlds.ModuleManager.ModuleHelper.GetModules();
                if (modules != null)
                {
                    foreach (var mod in modules)
                    {
                        if (mod != null && (mod.Id == "AgentCrashGuard" || mod.Name == "AgentCrashGuard"))
                        {
                            _isACGDetected = true;
                            return true;
                        }
                    }
                }

                _isACGDetected = false;
                return false;
            }
            catch
            {
                _isACGDetected = false;
                return false;
            }
        }
    }
}
