using BanditMilitias.Components;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Logging;
using BanditMilitias.Intelligence.ML;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Intelligence.Swarm;
using BanditMilitias.Systems.AI;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using TaleWorlds.Core;

namespace BanditMilitias.Intelligence.AI.Components
{
    public class MilitiaDecider
    {
        public struct DecisionResult
        {
            public AIDecisionType Decision;
            public Settlement? TargetSettlement;
            public MobileParty? TargetParty;
            public Vec2? MovePoint;

            // Geriye uyumluluk — kod tabanındaki Type referansları çalışmaya devam eder
            public AIDecisionType Type
            {
                get => Decision;
                set => Decision = value;
            }
        }

        private const float ENGAGE_SCORE_MIN = 60f;
        private const float RAID_SCORE_MIN = 70f;
        private const float GUARDIAN_LEASH_SQ = 225f;
        private const float CMD_EXPIRE_HOURS = CustomMilitiaAI.STRATEGIC_ORDER_DURATION;

        // ML sistemi ne kadar eğitilmişse o kadar güvenilir.
        // Bu eşiğin üzerinde ML gerçek bir karar önerisi yapabilir.
        private const float ML_CONFIDENCE_GATE = 0.15f;   // ~75-80 savaş
        private const float ML_BONUS_WEIGHT = 20f;    // bonus puan (düşük güvende)

        // ── Ana karar fonksiyonu ──────────────────────────────────

        public DecisionResult GetBestDecision(
            MobileParty party,
            MilitiaPartyComponent component,
            MilitiaAISensors sensors)
        {
            var doctrineSystem = ModuleManager.Instance.GetModule<AdaptiveAIDoctrineSystem>();
            var home = component.GetHomeSettlement();

            // ── ML hazırlık (zengin state ile) ───────────────────
            AILearningSystem? mlSystem = null;
            AIAction mlAction = AIAction.Patrol;
            float mlConfidence = 0f;
            float mlQValue = 0f;
            AIState mlState = AIState.Weak_Poor;

            try
            {
                mlSystem = ModuleManager.Instance.GetModule<AILearningSystem>();
                if (mlSystem?.IsEnabled == true)
                {
                    mlState = mlSystem.DetermineState(party);      // zengin state
                    mlAction = mlSystem.GetBestAction(mlState);
                    mlQValue = mlSystem.GetBestQValue(mlState);
                    mlConfidence = TaleWorlds.Library.MathF.Min(1f, mlSystem.TrainingSampleCount / 500f);
                }
            }
            catch { /* ML devre dışıysa sessizce atla */ }
            // ─────────────────────────────────────────────────────

            var result = new DecisionResult { Decision = AIDecisionType.Patrol };
            string source = "DefaultPatrol";
            string? targetId = null;
            float? score = null;

            // ══ Katman 1: SwarmCoordinator (her şeyi override eder) ══════
            if (SwarmCoordinator.Instance.TryGetOrder(party, out var swarmOrder))
            {
                if (ShouldApplySwarmOverride(swarmOrder, party, component, sensors))
                {
                    result.Decision = MapSwarmToDecision(swarmOrder.Tactic);
                    result.TargetParty = swarmOrder.TargetPartyId != null
                        ? Campaign.Current?.MobileParties.FirstOrDefault(p => p.StringId == swarmOrder.TargetPartyId)
                        : null;
                    result.MovePoint = swarmOrder.TargetPosition;
                    source = "SwarmOverride";
                    targetId = swarmOrder.TargetPartyId;
                    LogAndReturn();
                    return result;
                }

                source = "SwarmBypass";
            }

            // ══ Katman 2: Warlord komutu ═════════════════════════════════
            if (component.CurrentOrder != null)
            {
                double age = (CampaignTime.Now - component.OrderTimestamp).ToHours;
                if (age > CMD_EXPIRE_HOURS)
                {
                    component.CurrentOrder = null;
                }
                else
                {
                    var dec = TranslateCommand(component.CurrentOrder, party, sensors);
                    if (dec.HasValue)
                    {
                        result = dec.Value;
                        source = $"WarlordCommand:{component.CurrentOrder.Type}";
                        targetId = component.CurrentOrder.TargetParty?.StringId;
                        LogAndReturn();
                        return result;
                    }
                }
            }

            // ══ Katman 3: Guardian tasması ═══════════════════════════════
            if (component.Role == MilitiaPartyComponent.MilitiaRole.Guardian)
            {
                // home yukarıda tanımlandı
                if (home != null)
                {
                    float d2 = CompatibilityLayer.GetPartyPosition(party)
                                .DistanceSquared(CompatibilityLayer.GetSettlementPosition(home));
                    if (d2 > GUARDIAN_LEASH_SQ)
                    {
                        result.Decision = AIDecisionType.Patrol;
                        result.MovePoint = CompatibilityLayer.GetSettlementPosition(home);
                        source = "GuardianLeash";
                        targetId = home.StringId;
                        LogAndReturn();
                        return result;
                    }

                    float defScore = ScoringFunctions.CalculateDefenseScore(party, home);
                    if (defScore > 50f)
                    {
                        result.Decision = AIDecisionType.Defend;
                        result.MovePoint = sensors.Position;
                        source = "GuardianDefend";
                        targetId = home.StringId;
                        score = defScore;
                        LogAndReturn();
                        return result;
                    }
                }
            }

            if (party.MemberRoster != null && party.MemberRoster.TotalManCount < 15 && party.PartyTradeGold >= 1500)
            {
                // home yukarıda tanımlandı
                if (home != null)
                {
                    result.Decision = AIDecisionType.Defend;
                    result.MovePoint = CompatibilityLayer.GetSettlementPosition(home);
                    source = "ForcedRecruit";
                    targetId = home.StringId;
                    LogAndReturn();
                    return result;
                }
            }

            // ══ Katman 3.5: İç Tehdit (Cannibalization) - YENİ ═══════════
            int totalMobileParties = Campaign.Current.MobileParties.Count;
            if (totalMobileParties > 1200 && component.Role >= MilitiaPartyComponent.MilitiaRole.Captain)
            {
                var nearbyMilitias = sensors.GetNearbyMilitias();
                if (nearbyMilitias.Count > 0)
                {
                    float myStr = CompatibilityLayer.GetTotalStrength(party);
                    var internalTarget = nearbyMilitias
                        .Where(m => m != party && CompatibilityLayer.GetTotalStrength(m) < myStr * 0.4f)
                        .OrderBy(m => CompatibilityLayer.GetPartyPosition(party).DistanceSquared(CompatibilityLayer.GetPartyPosition(m)))
                        .FirstOrDefault();

                    if (internalTarget != null)
                    {
                        result.Decision = AIDecisionType.Engage;
                        result.TargetParty = internalTarget;
                        source = "InternalThreat_Cannibalization";
                        targetId = internalTarget.StringId;
                        LogAndReturn();
                        return result;
                    }
                }
            }

            // ══ Katman 4: Düşman tespiti (puan + ML bonus) ═══════════════
            var enemies = sensors.GetNearbyEnemies();
            if (enemies.Count > 0)
            {
                float doctrineEngageMod = doctrineSystem?.IsEnabled == true
                    ? doctrineSystem.GetDecisionModifier(party, AIDecisionType.Engage) : 0f;
                float engageThreshold = TaleWorlds.Library.MathF.Clamp(
                    ENGAGE_SCORE_MIN - doctrineEngageMod * 0.25f, 45f, 80f);

                float mlEngageBonus = (mlAction == AIAction.Engage && mlConfidence > 0f)
                    ? mlConfidence * ML_BONUS_WEIGHT : 0f;

                MobileParty? best = null;
                float bestScore = 0f;

                foreach (var enemy in enemies)
                {
                    float s = ScoringFunctions.CalculateAttackScore(party, enemy)
                              + doctrineEngageMod + mlEngageBonus;
                    if (s > bestScore) { bestScore = s; best = enemy; }
                }

                if (best != null && bestScore >= engageThreshold)
                {
                    result.Decision = AIDecisionType.Engage;
                    result.TargetParty = best;
                    source = doctrineSystem?.IsEnabled == true
                        ? $"EnemyDetection[{doctrineSystem.DescribeDoctrine(party)}]"
                        : "EnemyDetection";
                    targetId = best.StringId;
                    score = bestScore;
                    LogAndReturn();
                    return result;
                }
            }

            // ══ Katman 5: Raider baskını (puan + ML bonus) ═══════════════
            if (component.Role == MilitiaPartyComponent.MilitiaRole.Raider)
            {
                var villages = sensors.GetNearbyVillages();
                float doctrineRaidMod = doctrineSystem?.IsEnabled == true
                    ? doctrineSystem.GetDecisionModifier(party, AIDecisionType.Raid) : 0f;
                float raidThreshold = TaleWorlds.Library.MathF.Clamp(
                    RAID_SCORE_MIN - doctrineRaidMod * 0.25f, 52f, 85f);

                float mlRaidBonus = (mlAction == AIAction.Raiding && mlConfidence > 0f)
                    ? mlConfidence * ML_BONUS_WEIGHT : 0f;

                foreach (var v in villages)
                {
                    float s = ScoringFunctions.CalculateAttackScore(party, v)
                              + doctrineRaidMod + mlRaidBonus;
                    if (s >= raidThreshold)
                    {
                        result.Decision = AIDecisionType.Raid;
                        result.TargetSettlement = v;
                        source = doctrineSystem?.IsEnabled == true
                            ? $"RaiderAttack[{doctrineSystem.DescribeDoctrine(party)}]"
                            : "RaiderAttack";
                        targetId = v.StringId;
                        score = s;
                        LogAndReturn();
                        return result;
                    }
                }
            }

            // ══ Katman 6: ML önerisi (yüksek güven + pozitif Q değeri) ══
            if (mlSystem != null && mlConfidence >= ML_CONFIDENCE_GATE
                                 && mlQValue > 0f
                                 && mlAction != AIAction.Patrol)
            {
                var mlDecision = MapMLActionToDecision(mlAction, party, sensors);
                if (mlDecision.HasValue)
                {
                    result = mlDecision.Value;
                    source = $"ML[{mlState}→{mlAction} Q={mlQValue:F1} conf={mlConfidence:F2}]";
                    LogAndReturn();
                    return result;
                }
            }

            // ══ Katman 7: State-Aware Fallback Matrix ════════════════════
            // ══ Katman 7: Hibrit AI - Durumsal Farkındalık Matrisi ════════════
            float strength = CompatibilityLayer.GetTotalStrength(party);
            bool isWeak = strength < 40f || party.MemberRoster?.TotalManCount < 18;
            bool isOverdue = component.NextThinkTime < CampaignTime.Now;
            bool hasThreat = enemies.Count > 0;
            // home yukarıda tanımlandı

            if (isWeak)
            {
                // Çaresizlik Doktrini: Incubation (Sığınak Kuluçkası)
                if (hasThreat && home != null)
                {
                    float distToHome = sensors.Position.DistanceSquared(CompatibilityLayer.GetSettlementPosition(home));
                    if (distToHome < 1600f) // Sığınağa yakınsa kuluçkaya yat
                    {
                        result.Decision = AIDecisionType.Defend;
                        result.MovePoint = CompatibilityLayer.GetSettlementPosition(home);
                        result.TargetSettlement = home;
                        source = "IncubationMode:RetreatToSafety";
                        
                        // Sığınağa vardığında uyku moduna geçmesi için işaretle (MilitiaBehavior bunu işler)
                        component.SleepFor(MBRandom.RandomFloatRanged(18f, 24f)); 
                        
                        LogAndReturn();
                        return result;
                    }
                }

                // Kütleçekimsel Birleşme: Merge
                var weakMerge = TryGetMergeDecisionForWeakParty(party, component, sensors);
                if (weakMerge.HasValue)
                {
                    result = weakMerge.Value;
                    source = hasThreat ? "SwarmCoalescence:PanicMerge" : "SwarmCoalescence:GrowthMerge";
                    targetId = result.TargetParty?.StringId;
                    LogAndReturn();
                    return result;
                }

                if (hasThreat)
                {
                    bool canRecruitAtHome = home != null && party.PartyTradeGold >= 900;
                    if (canRecruitAtHome && home != null)
                    {
                        result.Decision = AIDecisionType.Defend;
                        result.MovePoint = CompatibilityLayer.GetSettlementPosition(home);
                        result.TargetSettlement = home;
                        source = "EmergencyRecruit";
                        targetId = home.StringId;
                    }
                    else
                    {
                        result.Decision = AIDecisionType.Flee;
                        source = "Survival:Evade";
                    }
                }
                else 
                {
                    // ── HAFIZA ENTEGRASYONU: Görünmez ama yakın zamandaki tehditler ───
                    var rememberedThreats = sensors.GetThreatsFromMemory(150f);
                    if (rememberedThreats.Count > 0)
                    {
                        var dangerousThreat = rememberedThreats.OrderByDescending(t => t.ReportedStrength).First();
                        if (dangerousThreat.ReportedStrength > strength * 1.5f)
                        {
                            result.Decision = AIDecisionType.Flee;
                            source = "Survival:MemoryEvade"; // Hafıza kaynaklı kaçış
                            LogAndReturn();
                            return result;
                        }
                    }

                    if (isOverdue)
                    {
                        result.Decision = AIDecisionType.Patrol;
                        source = "SafetyPatrol";
                    }
                }
            }
            else
            {
                result.Decision = AIDecisionType.Patrol;
                source = "StandardPatrol";
            }

            LogAndReturn();
            return result;

            void LogAndReturn()
            {
                MilitiaSmartCache.Instance.CacheDecision(
                    party,
                    result.Decision,
                    CampaignTime.Now,
                    result.TargetSettlement,
                    result.TargetParty);
                AIDecisionLogger.LogTacticalDecision(
                    party.StringId, result.Decision.ToString(), source, targetId, score);
                if (Settings.Instance?.DevMode == true)
                {
                    var dev = BanditMilitias.Systems.Dev.DevDataCollector.Instance;
                    if (dev?.IsEnabled == true)
                    {
                        var comp2 = party.PartyComponent as MilitiaPartyComponent;
                        dev.RecordAIDecision(
                            party,
                            result.Decision.ToString(),
                            source ?? "Unknown",
                            score ?? 0f,
                            comp2?.GetSleepRemainingHours() ?? 0f,
                            targetId,
                            mlConfidence,
                            mlState.ToString());
                    }
                }
            }
        }

        // ── ML aksiyonunu AIDecision tipine çevir ─────────────────
        // NOT: Artık Recruit, Upgrade, Merge ve daha fazlası destekleniyor.
        // Bu sayede ML sistemi'nin önerdiği tüm aksiyonlar motor seviyesinde karşılık buluyor.

        private static DecisionResult? MapMLActionToDecision(
            AIAction action, MobileParty party, MilitiaAISensors sensors)
        {
            var comp = party.PartyComponent as MilitiaPartyComponent;

            switch (action)
            {
                // ── Savaş aksiyonları ────────────────────────────────────────
                case AIAction.Engage:
                    {
                        var enemies = sensors.GetNearbyEnemies();
                        if (enemies.Count == 0) return null;
                        float myStr = CompatibilityLayer.GetTotalStrength(party);
                        var target = enemies
                            .Where(e => CompatibilityLayer.GetTotalStrength(e) <= myStr * 1.5f)
                            .OrderByDescending(e => ScoringFunctions.CalculateAttackScore(party, e))
                            .FirstOrDefault();
                        if (target == null) return null;
                        return new DecisionResult { Decision = AIDecisionType.Engage, TargetParty = target };
                    }

                case AIAction.Hunt:
                    {
                        var enemies = sensors.GetNearbyEnemies();
                        if (enemies.Count == 0) return null;
                        float myStr = CompatibilityLayer.GetTotalStrength(party);
                        // Hunt: Sadece kendinden en fazla %25 güçlü düşmanları hedefle
                        var target = enemies
                            .Where(e => CompatibilityLayer.GetTotalStrength(e) <= myStr * 1.25f)
                            .OrderBy(e => CompatibilityLayer.GetPartyPosition(party).DistanceSquared(CompatibilityLayer.GetPartyPosition(e)))
                            .FirstOrDefault();
                        if (target == null) return null;
                        return new DecisionResult { Decision = AIDecisionType.Engage, TargetParty = target };
                    }

                case AIAction.Ambush:
                    return new DecisionResult { Decision = AIDecisionType.Ambush };

                case AIAction.Defend:
                    return new DecisionResult { Decision = AIDecisionType.Defend, MovePoint = sensors.Position };

                case AIAction.LayLow:
                    // Gizlen: En yakın yerleşkeye kaçmaya çalış
                    {
                        var home = comp?.GetHomeSettlement();
                        if (home != null)
                            return new DecisionResult { Decision = AIDecisionType.Flee, MovePoint = CompatibilityLayer.GetSettlementPosition(home) };
                        return new DecisionResult { Decision = AIDecisionType.Flee };
                    }

                case AIAction.Raiding:
                    {
                        var villages = sensors.GetNearbyVillages();
                        if (villages.Count == 0) return null;
                        var target = villages
                            .OrderByDescending(v => ScoringFunctions.CalculateAttackScore(party, v))
                            .FirstOrDefault();
                        if (target == null) return null;
                        return new DecisionResult { Decision = AIDecisionType.Raid, TargetSettlement = target };
                    }

                case AIAction.Extort:
                    // Haraç: Güçlü değilsek yağmaya benzer davranış göster
                    {
                        var villages = sensors.GetNearbyVillages();
                        if (villages.Count == 0) return null;
                        float myStr = CompatibilityLayer.GetTotalStrength(party);
                        // Güçlüyken muhtara git, zayıfken köyleri seç
                        var target = villages
                            .OrderBy(v => CompatibilityLayer.GetSettlementPosition(v).DistanceSquared(CompatibilityLayer.GetPartyPosition(party)))
                            .FirstOrDefault();
                        if (target == null) return null;
                        return new DecisionResult { Decision = AIDecisionType.Raid, TargetSettlement = target };
                    }

                // ── Büyüme aksiyonları (ML öğreniyor) ─────────────────────────
                // Bu aksiyonlar artık askere alma ve birleşme gibi somut oyun eylemlerine dönüşüyor.

                case AIAction.Recruit:
                    // Askere al: Yeterli altın varsa ve birlik sayısı azsa ana üsse dön
                    {
                        var home = comp?.GetHomeSettlement();
                        if (home == null) return null;
                        // Eve dön, orada askere alım gerçekleşecek (SpawnSystem tetikler)
                        return new DecisionResult
                        {
                            Decision = AIDecisionType.Defend,
                            MovePoint = CompatibilityLayer.GetSettlementPosition(home),
                            TargetSettlement = home
                        };
                    }

                case AIAction.Upgrade:
                    // Yükselt: Recruit ile aynı mantık — eve dön, bekle
                    {
                        var home = comp?.GetHomeSettlement();
                        if (home == null) return null;
                        return new DecisionResult
                        {
                            Decision = AIDecisionType.Defend,
                            MovePoint = CompatibilityLayer.GetSettlementPosition(home),
                            TargetSettlement = home
                        };
                    }

                case AIAction.Merge:
                    // Birleş: En yakın dost milis Captain/VeteranCaptain'ını bul ve Engage ile yaklaş
                    {
                        if (ModuleManager.Instance == null) return null;
                        var myPos = CompatibilityLayer.GetPartyPosition(party);
                        float myStr = CompatibilityLayer.GetTotalStrength(party);
                        int myTroops = party.MemberRoster?.TotalManCount ?? 0;

                        MobileParty? mergeTarget = null;
                        float minDist = float.MaxValue;

                        foreach (var ally in ModuleManager.Instance.ActiveMilitias)
                        {
                            if (ally == null || ally == party || !ally.IsActive) continue;
                            if (ally.MapEvent != null || ally.SiegeEvent != null) continue;
                            if (ally.PartyComponent is not MilitiaPartyComponent allyComp) continue;

                            // Sadece Captain ve üzeri birliklere birleş
                            if (allyComp.Role < MilitiaPartyComponent.MilitiaRole.Captain) continue;

                            // Kendimizden daha güçlü birliklere birleş
                            if (CompatibilityLayer.GetTotalStrength(ally) <= myStr) continue;

                            // Kapasite kontrolü: hedef birlik doluysa atla (max 150 birlik)
                            int allyTroops = ally.MemberRoster?.TotalManCount ?? 0;
                            if (allyTroops + myTroops > 150) continue;

                            // Aynı Warlord ağına ait olanları tercih et
                            if (allyComp.WarlordId != comp?.WarlordId && comp?.WarlordId != null) continue;

                            float dist = myPos.DistanceSquared(CompatibilityLayer.GetPartyPosition(ally));
                            if (dist < minDist)
                            {
                                minDist = dist;
                                mergeTarget = ally;
                            }
                        }

                        if (mergeTarget == null) return null;
                        return new DecisionResult
                        {
                            Decision = AIDecisionType.Engage, // Engage → hedefe aktif takip ve birleşme
                            MovePoint = CompatibilityLayer.GetPartyPosition(mergeTarget),
                            TargetParty = mergeTarget
                        };
                    }

                case AIAction.BuildRepute:
                    // İtibar kazan: Devriye at ama devriye davranışı artık bilinçli seçilmiş
                    {
                        var home = comp?.GetHomeSettlement();
                        if (home != null)
                            return new DecisionResult
                            {
                                Decision = AIDecisionType.Patrol,
                                MovePoint = CompatibilityLayer.GetSettlementPosition(home)
                            };
                        return null;
                    }

                default:
                    return null;
            }
        }

        // ── Komut çevirisi ────────────────────────────────────────

        private static DecisionResult? TranslateCommand(
            StrategicCommand cmd,
            MobileParty party,
            MilitiaAISensors sensors)
        {
            switch (cmd.Type)
            {
                case CommandType.Ambush:
                    return new DecisionResult { Decision = AIDecisionType.Ambush };

                case CommandType.Hunt:
                case CommandType.Harass:
                case CommandType.Engage:
                    if (cmd.TargetParty?.IsActive == true)
                        return new DecisionResult { Decision = AIDecisionType.Engage, TargetParty = cmd.TargetParty };
                    if (cmd.TargetLocation.IsValid)
                        return new DecisionResult { Decision = AIDecisionType.Patrol, MovePoint = cmd.TargetLocation };
                    break;

                case CommandType.Patrol:
                case CommandType.CommandRaidVillage:
                case CommandType.CommandExtort:
                case CommandType.Scavenge:
                    if (cmd.TargetLocation.IsValid)
                        return new DecisionResult { Decision = AIDecisionType.Patrol, MovePoint = cmd.TargetLocation };
                    break;

                case CommandType.Defend:
                    return new DecisionResult { Decision = AIDecisionType.Defend, MovePoint = sensors.Position };

                case CommandType.Retreat:
                case CommandType.CommandLayLow:
                case CommandType.AvoidCrowd:
                    if (cmd.TargetLocation.IsValid)
                    {
                        Vec2 pos = CompatibilityLayer.GetPartyPosition(party);
                        Vec2 dir = (pos - cmd.TargetLocation);
                        if (dir.LengthSquared > 0.001f) dir = dir.Normalized();
                        return new DecisionResult { Decision = AIDecisionType.Flee, MovePoint = pos + dir * 50f };
                    }
                    return new DecisionResult { Decision = AIDecisionType.Flee };

                case CommandType.CommandBuildRepute:
                    return new DecisionResult { Decision = AIDecisionType.Patrol, MovePoint = sensors.Position };
            }
            return null;
        }

        private static bool IsOffensiveSwarmTactic(SwarmTactic tactic)
            => tactic is SwarmTactic.Hunt or SwarmTactic.Pincer or SwarmTactic.Ambush or SwarmTactic.Envelopment;

        private static bool ShouldApplySwarmOverride(
            SwarmOrder order,
            MobileParty party,
            MilitiaPartyComponent component,
            MilitiaAISensors sensors)
        {
            int troopCount = party.MemberRoster?.TotalManCount ?? 0;
            float strength = CompatibilityLayer.GetTotalStrength(party);
            bool isWeak = strength < 40f || troopCount < 10;

            var enemies = sensors.GetNearbyEnemies();
            bool hasThreat = enemies.Count > 0;

            if (order.Tactic == SwarmTactic.Retreat)
            {
                if (!hasThreat) return false;
                if (isWeak) return true;

                float strongestThreat = enemies.Max(e => CompatibilityLayer.GetTotalStrength(e));
                return strength < strongestThreat * 1.15f;
            }

            if (IsOffensiveSwarmTactic(order.Tactic) && !hasThreat)
                return false;

            if (isWeak)
            {
                var home = component.GetHomeSettlement();
                bool canRecover = home != null && party.PartyTradeGold >= 900;
                if (canRecover) return false;
            }

            if (order.Priority <= 2 && !component.IsPriorityAIUpdate && !hasThreat)
                return false;

            return true;
        }

        private static DecisionResult? TryGetMergeDecisionForWeakParty(
            MobileParty party,
            MilitiaPartyComponent component,
            MilitiaAISensors sensors)
        {
            // Hibrit AI: Daha geniş bir tarama alanı kullan
            var allMilitias = ModuleManager.Instance.ActiveMilitias;
            if (allMilitias == null || allMilitias.Count == 0) return null;

            float myStrength = CompatibilityLayer.GetTotalStrength(party);
            int myTroops = party.MemberRoster?.TotalManCount ?? 0;
            string? myWarlordId = component.WarlordId;
            Vec2 myPos = sensors.Position;

            MobileParty? target = null;
            float minDistanceSq = 10000f; // 100 birim mesafe (Genişletilmiş Swarm Alanı)

            foreach (var ally in allMilitias)
            {
                if (ally == null || ally == party || !ally.IsActive) continue;
                if (ally.MapEvent != null || ally.SiegeEvent != null) continue;
                if (ally.PartyComponent is not MilitiaPartyComponent allyComp) continue;
                
                // Kütleçekimi: Sadece Kaptan veya daha üst rütbeli birliklere birleş
                if (allyComp.Role < MilitiaPartyComponent.MilitiaRole.Captain) continue;

                // Aynı ağdaki birliği tercih et
                if (!string.IsNullOrEmpty(myWarlordId) && allyComp.WarlordId != myWarlordId) continue;

                int allyTroops = ally.MemberRoster?.TotalManCount ?? 0;
                if (allyTroops + myTroops > 150) continue; // Max kapasite kontrolü

                float allyStrength = CompatibilityLayer.GetTotalStrength(ally);
                if (allyStrength <= myStrength * 1.1f) continue; // Biraz daha güçlü olması yeterli

                float distSq = myPos.DistanceSquared(CompatibilityLayer.GetPartyPosition(ally));
                if (distSq < minDistanceSq)
                {
                    minDistanceSq = distSq;
                    target = ally;
                }
            }

            if (target == null) return null;

            return new DecisionResult
            {
                Decision = AIDecisionType.Engage, // Engage hedefe aktif kilitlenmeyi sağlar
                TargetParty = target,
                MovePoint = CompatibilityLayer.GetPartyPosition(target)
            };
        }

        // ── Swarm çevirisi ────────────────────────────────────────

        private static AIDecisionType MapSwarmToDecision(SwarmTactic tactic) => tactic switch
        {
            SwarmTactic.Hunt => AIDecisionType.Engage,
            SwarmTactic.Retreat => AIDecisionType.Flee,
            SwarmTactic.Ambush => AIDecisionType.Ambush,
            _ => AIDecisionType.Patrol
        };
    }
}
