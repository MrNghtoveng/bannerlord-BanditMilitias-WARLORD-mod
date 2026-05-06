using BanditMilitias.Behaviors;
using BanditMilitias.Components;
using BanditMilitias.Core.Components;
using BanditMilitias.Core.Config; // DynamicDifficulty için
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.ObjectSystem;
using TaleWorlds.Localization;

using BanditMilitias.Systems.Economy;
using BanditMilitias.Systems.Progression;
using BanditMilitias.Intelligence.Strategic;

namespace BanditMilitias.Systems.Spawning
{
    public class MilitiaSpawningSystem : MilitiaModuleBase, ISpawningSystem
    {
        public const float BaseDailySpawnChanceMin = 0.55f;
        public const float BaseDailySpawnChanceMax = 0.75f;

        private static readonly Lazy<bool> _hasNavalDLC = new Lazy<bool>(() =>
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            bool warsails = assemblies.Any(a => a.GetName().Name?.StartsWith("WarSails") == true);
            bool naval = assemblies.Any(a => a.GetName().Name?.Contains("TaleWorlds.CampaignSystem.Naval") == true);
            return warsails || naval;
        });

        public override string ModuleName => "SpawningSystem";
        public override bool IsEnabled => Settings.Instance?.MilitiaSpawn ?? true;
        public override bool IsCritical => true;
        public override int Priority => 82;

        public static MilitiaSpawningSystem? Instance { get; private set; }
        private int _cachedTotalParties;
        private int _tickCount;

        public override string GetDiagnostics()
        {
            try
            {
                return $"Spawns Active: {ModuleManager.Instance.GetMilitiaCount()} | DailyChanceBand: {BaseDailySpawnChanceMin:P0}-{BaseDailySpawnChanceMax:P0}";
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        public override void Initialize()
        {
            Instance = this;
        }

        public override void Cleanup()
        {
            Instance = null;
        }

        public override void OnHourlyTick()
        {
            if (!IsEnabled) return;

            if (CompatibilityLayer.IsGameplayActivationDelayed())
                return;

            if (ModuleManager.Instance.HideoutCache.Count == 0)
            {
                ModuleManager.Instance.RebuildCaches();
            }

            _cachedTotalParties = Campaign.Current?.MobileParties?.Count ?? 0;
            _tickCount++;

            var sw = System.Diagnostics.Stopwatch.StartNew();

            var scheduler = Systems.Scheduling.AISchedulerSystem.Instance;
            if (scheduler == null)
            {
                foreach (var h in ModuleManager.Instance.HideoutCache)
                    ProcessSpawnEvaluation(h);

                sw.Stop();
                BanditMilitias.Systems.Dev.DevDataCollector.Instance.RecordModuleTiming("SpawningSystem_Direct", sw.ElapsedMilliseconds);
                return;
            }

            foreach (var hideout in ModuleManager.Instance.HideoutCache)
            {
                scheduler.EnqueueSpawnEvaluation(hideout);
            }

            sw.Stop();
            BanditMilitias.Systems.Dev.DevDataCollector.Instance.RecordModuleTiming("SpawningSystem_Enqueue", sw.ElapsedMilliseconds);
        }

        /// <summary>
        /// Tek bir sığınak için spawn şansını ve şartlarını değerlendirir.
        /// AISchedulerSystem tarafından parça parça çağrılır.
        /// </summary>
        public void ProcessSpawnEvaluation(Settlement hideout)
        {
            if (hideout == null || !hideout.IsHideout) return;

            // KRİTİK: Settings kontrolü
            if (Settings.Instance == null)
            {
                StructuredLogger.Error("SpawningSystem", "Settings.Instance null! MCM yüklü mü?");
                return;
            }

            // Watchdog heartbeat
            BanditMilitias.Systems.Diagnostics.SystemWatchdog.Instance.ReportHeartbeat("SpawningSystem");

            if (!IsEnabled) return;

            // Mevcut durum kontrolü
            int currentCount = ModuleManager.Instance.GetMilitiaCount();
            int maxParties = Settings.Instance.MaxTotalMilitias;

            // Dinamik zorluk: Optimal sayı kontrolü
            int optimalCount = Core.Config.DynamicDifficulty.CalculateOptimalMilitiaCount();
            if (optimalCount <= 0) optimalCount = maxParties;
            int dynamicCap = CalculateDynamicMilitiaCap(currentCount, optimalCount, maxParties);

            if (currentCount >= dynamicCap)
            {
                if (Settings.Instance?.TestingMode == true && _tickCount % 24 == 0)
                    DebugLogger.Warning("SpawningSystem", $"Militia cap reached ({currentCount}/{dynamicCap}). Spawning suspended.");
                return; // Yeterli militia var
            }
            /*
            // YENİ: Performans Koruması (Global Party Count Guard) - REPLACED BY ORGANIC CONSOLIDATION
            if (totalMobileParties > 2000 && MBRandom.RandomFloat > 0.5f)
            {
                if (Settings.Instance?.TestingMode == true)
                    DebugLogger.Info("SpawningSystem", $"High party count reached ({totalMobileParties}). Spawning throttled (50% chance).");
                return;
            }
            */

            // YENİ: Önce oyunun tamamen initialize olup olmadığını kontrol et
            if (!CompatibilityLayer.IsGameFullyInitialized())
            {
                StructuredLogger.Warning("SpawningSystem",
                    "Game not fully initialized yet. Skipping spawn cycle.");
                return;
            }

            // YENİ: Globals.BasicInfantry boşsa retry mekanizması
            if (Globals.BasicInfantry.Count == 0)
            {
                StructuredLogger.Warning("SpawningSystem",
                    "Globals.BasicInfantry empty. Attempting re-initialization...");

                Globals.Initialize(force: true);

                if (Globals.BasicInfantry.Count == 0)
                {
                    StructuredLogger.Error("SpawningSystem",
                        "Globals initialization failed after retry! Cannot spawn militias.");

                    if (Settings.Instance?.TestingMode == true)
                    {
                        InformationManager.DisplayMessage(new InformationMessage(
                            "[BanditMilitias] KRİTİK: Globals initialize edilemedi!",
                            Colors.Red));
                    }
                    return;
                }
                else
                {
                    StructuredLogger.Info("SpawningSystem",
                        $"Globals re-initialization successful! {Globals.BasicInfantry.Count} troops available.");
                }
            }

            // Günlük spawn şansı kontrolü (Hourly tick'te çağrıldığı için %24 bölüyoruz)
            float dailyChance = ResolveBaseDailySpawnChance();
            dailyChance = CalculatePopulationAdjustedSpawnChance(dailyChance, currentCount, optimalCount, dynamicCap);
            float hourlyChance = dailyChance / 24f;

            if (MBRandom.RandomFloat > hourlyChance) return;

            var behavior = Campaign.Current?.GetCampaignBehavior<MilitiaBehavior>();
            if (behavior != null && behavior.IsHideoutOnCooldown(hideout)) return;

            float finalSpawnChance = 1.0f; // Base chance already handled by hourlyChance check

            int existingMilitiasForHideout = Systems.AI.MilitiaMemorySystem.Instance
                .GetActiveMilitiaCount(hideout);

            if (existingMilitiasForHideout >= 1)
            {
                finalSpawnChance *= 0.75f;
            }

            if (Settings.Instance?.WarSpawnMultiplier > 0f)
            {
                float deserterModifier = BanditMilitias.Systems.Tracking.WarActivityTracker.Instance
                    .GetDeserterSpawnModifier(CompatibilityLayer.GetSettlementPosition(hideout));

                if (deserterModifier > 1.0f)
                {
                    float extra = deserterModifier - 1.0f;
                    finalSpawnChance *= 1.0f + (extra * Settings.Instance.WarSpawnMultiplier);
                }
            }

            if (Settings.Instance?.TradeSpawnMultiplier > 0f)
            {
                float tradeIntensity = BanditMilitias.Systems.Tracking.CaravanActivityTracker.Instance
                    .GetTradeIntensity(CompatibilityLayer.GetSettlementPosition(hideout));

                if (tradeIntensity > 0.1f)
                {
                    float extra = tradeIntensity / 5.0f;
                    finalSpawnChance *= 1.0f + (extra * Settings.Instance.TradeSpawnMultiplier);
                }
            }

            finalSpawnChance = MathF.Min(finalSpawnChance, 0.92f);

            // Tek kontrol: finalSpawnChance 1.0'dan küçükse random check yap, değilse direkt spawn
            if (finalSpawnChance < 1.0f && MBRandom.RandomFloat >= finalSpawnChance)
                return;

            var spawnedParty = SpawnMilitia(hideout);
            if (spawnedParty != null)
            {
                behavior?.SetHideoutCooldown(hideout);
                StructuredLogger.Info("SpawningSystem",
                    $"Militia spawned at {hideout.Name}. Size: {spawnedParty.MemberRoster.TotalManCount}. Current total: {ModuleManager.Instance.GetMilitiaCount()}");
            }
        }

        /// <summary>
        /// HİBRİT AI: Dünya durumu ve oyuncu kapasitesini birleştirerek spawn boyutunu hesaplar.
        /// </summary>
        public int CalculateDynamicSpawnSize(Settlement hideout)
        {
            if (hideout == null) return 12;

            // 1. DÜNYA DURUMU (ANA MOTOR)
            float baseSize = 8f;
            
            // Refah (Prosperity) Çarpanı
            var nearestTown = Settlement.All.Where(s => s.IsTown && s.GatePosition.DistanceSquared(hideout.GatePosition) < 1600f).FirstOrDefault();
            if (nearestTown != null)
            {
                float prosperity = nearestTown.Town?.Prosperity ?? 4000f;
                baseSize += (prosperity / 1000f) * 2f; // Her 1000 refah için +2 asker
            }

            // Küresel Savaş (Chaos) Çarpanı
            int activeWars = 0;
            var kingdoms = Kingdom.All.Where(k => !k.IsEliminated).ToList();
            for (int i = 0; i < kingdoms.Count; i++)
            {
                for (int j = i + 1; j < kingdoms.Count; j++)
                {
                    if (kingdoms[i].IsAtWarWith(kingdoms[j]))
                        activeWars++;
                }
            }
            baseSize += activeWars * 1.5f; // Her aktif savaş için +1.5 asker

            // Warlord Etkisi
            var warlord = WarlordSystem.Instance.GetWarlordForHideout(hideout);
            if (warlord != null)
            {
                var tier = WarlordCareerSystem.Instance.GetTier(warlord.StringId);
                baseSize += (int)tier * 10f; // Her kariyer basamağı için +10 asker
            }

            // Sığınak Yoğunluğu
            int nearbyHideouts = ModuleManager.Instance.HideoutCache.Count(h => h != hideout && h.GatePosition.DistanceSquared(hideout.GatePosition) < 2500f);
            baseSize += nearbyHideouts * 4f;

            // 2. OYUNCU KAPASİTESİ (ADAPTİF FİLTRE)
            float playerStrength = MobileParty.MainParty != null 
                ? Infrastructure.CompatibilityLayer.GetTotalStrength(MobileParty.MainParty) 
                : 50f;
            int playerRenown = (int)(Hero.MainHero?.Clan?.Renown ?? 0f);

            // Filtreleme: Eğer dünya çok kaotik ama oyuncu güçsüzse, devasa sayıları biraz baskıla
            float maxAllowedForPlayer = 20f + (playerStrength / 5f) + (playerRenown / 50f);
            
            float finalSize = Math.Min(baseSize, maxAllowedForPlayer);

            // Rastgelelik ekle (+-%20)
            finalSize *= MBRandom.RandomFloatRanged(0.8f, 1.2f);

            return (int)MathF.Clamp(finalSize, Constants.SPAWN_TROOP_HARD_MIN, Constants.SPAWN_TROOP_MAX);
        }


        public override void SyncData(IDataStore dataStore)
        {
        }

        public MobileParty? SpawnMilitia(Settlement hideout)
            => SpawnMilitia(hideout, false);

        public MobileParty? SpawnMilitia(Settlement hideout, bool force = false)
        {
            // KRİTİK FIX: Comprehensive pre-flight checks
            if (hideout == null)
            {
                DebugLogger.Warning("SpawningSystem", "SpawnMilitia called with null hideout");
                return null;
            }

            if (!IsEnabled && !force)
            {
                return null;
            }

            // FIX: Defensive Globals initialization with retry logic
            if (Globals.BasicInfantry.Count == 0)
            {
                DebugLogger.Info("SpawningSystem", "Globals not initialized, attempting initialization...");
                Globals.Initialize();

                if (Globals.BasicInfantry.Count == 0)
                {
                    Globals.Initialize(force: true);

                    if (Globals.BasicInfantry.Count == 0)
                    {
                        DebugLogger.Error("SpawningSystem",
                            $"CRITICAL: Globals initialization failed after retry! " +
                            $"Attempts: {Globals.InitAttempts}. " +
                            $"CharacterObject.All: {CharacterObject.All?.Count() ?? 0} items. " +
                            "Spawn aborted.");

                        if (Settings.Instance?.TestingMode == true)
                        {
                            InformationManager.DisplayMessage(new InformationMessage(
                                "[BanditMilitias] KRİTİK: Globals initialize edilemedi! Mod başlamamış olabilir.",
                                Colors.Red));
                        }
                        return null;
                    }
                }
            }

            int currentCount = ModuleManager.Instance.GetMilitiaCount();
            if (!force && Settings.Instance != null && currentCount >= Settings.Instance.MaxTotalMilitias)
                return null;

            // FIX: Defensive Clan resolution with explicit null checks
            Clan? banditClan = ResolveBanditClan(hideout);

            if (banditClan == null)
            {
                DebugLogger.Error("SpawningSystem",
                    $"No valid bandit clan found for hideout: {hideout.Name}. " +
                    $"ClanCache initialized: {ClanCache.IsInitialized}, " +
                    $"Clan.All count: {Clan.All?.Count() ?? 0}");
                return null;
            }

            if (!force && Settings.Instance?.EnableSeaRaiders == false
                && banditClan.StringId.Contains("sea_raiders"))
                return null;

            if (!hideout.IsHideout)
            {
                DebugLogger.TestLog($"[Spawn] {hideout.Name} bir hideout değil! Spawn iptal.", Colors.Yellow);
                return null;
            }

            var gatePos = hideout.GatePosition;

            if (float.IsNaN(gatePos.X) || float.IsNaN(gatePos.Y))
            {
                DebugLogger.Log($"[Spawn] {hideout.Name} GatePosition NaN - NavMesh kurtarması denenecek.");
                var settPos = CompatibilityLayer.GetSettlementPosition(hideout);
                if (settPos.IsValid && !float.IsNaN(settPos.X) && !float.IsNaN(settPos.Y))
                    gatePos = CompatibilityLayer.CreateCampaignVec2(settPos);
                else
                    gatePos = CompatibilityLayer.CreateCampaignVec2(new Vec2(hideout.GatePosition.X, hideout.GatePosition.Y));
            }

            if (float.IsInfinity(gatePos.X) || float.IsInfinity(gatePos.Y))
            {
                var settPos = CompatibilityLayer.GetSettlementPosition(hideout);
                if (settPos.IsValid && !float.IsInfinity(settPos.X) && !float.IsInfinity(settPos.Y))
                    gatePos = CompatibilityLayer.CreateCampaignVec2(settPos);
                else
                    gatePos = CompatibilityLayer.CreateCampaignVec2(new Vec2(hideout.GatePosition.X, hideout.GatePosition.Y));
            }

            if (gatePos.X == 0f && gatePos.Y == 0f)
            {
                DebugLogger.Log($"[Spawn] {hideout.Name} GatePosition (0,0) - skipping.");
                MarkSpawnFailure(hideout, "GatePosition (0,0)");
                return null;
            }

            const float MIN_MAP_COORD = -100f;
            const float MAX_MAP_COORD = 2000f;
            if (gatePos.X < MIN_MAP_COORD || gatePos.Y < MIN_MAP_COORD ||
                gatePos.X > MAX_MAP_COORD || gatePos.Y > MAX_MAP_COORD)
            {
                DebugLogger.Log($"[Spawn] {hideout.Name} GatePosition harita dışı: ({gatePos.X:F1}, {gatePos.Y:F1}) - atlanıyor.");
                return null;
            }

            if (Campaign.Current == null)
            {
                DebugLogger.TestLog("[Spawn] Campaign.Current null! Spawn iptal.", Colors.Red);
                return null;
            }

            var mapScene = Campaign.Current?.MapSceneWrapper;
            if (mapScene == null)
            {
                DebugLogger.TestLog("[Spawn] MapSceneWrapper null! Spawn iptal.", Colors.Red);
                MarkSpawnFailure(hideout, "MapSceneWrapper null", 12f);
                return null;
            }

            var liveMapScene = mapScene;

            if (mapScene != null)
            {
                var testPos = liveMapScene.GetAccessiblePointNearPosition(gatePos, 1f);
                if (float.IsNaN(testPos.X) || float.IsNaN(testPos.Y))
                {
                    testPos = liveMapScene.GetAccessiblePointNearPosition(gatePos, 15f);
                    if (float.IsNaN(testPos.X) || float.IsNaN(testPos.Y))
                    {
                        testPos = liveMapScene.GetAccessiblePointNearPosition(gatePos, 40f);
                        if (float.IsNaN(testPos.X) || float.IsNaN(testPos.Y))
                        {
                            string msg = $"[BanditMilitias] SPAWN İPTAL: '{hideout.Name}' sığınağının koordinatları " +
                                         "haritada geçersiz (NavMesh NaN). Sonraki tick'te yeniden denenecek.";
                            DebugLogger.Log(msg);
                            if (Settings.Instance?.TestingMode == true && MBRandom.RandomFloat < 0.1f)
                                InformationManager.DisplayMessage(new InformationMessage(msg, Colors.Yellow));
                            MarkSpawnFailure(hideout, "NavMesh NaN");
                            return null;
                        }
                    }

                    var vec2Pos = CompatibilityLayer.ToVec2(testPos);
                    if (vec2Pos.IsValid && !float.IsNaN(vec2Pos.X))
                        gatePos = CompatibilityLayer.CreateCampaignVec2(vec2Pos);
                }
            }

            if (!hideout.IsActive)
            {
                var isActiveProp = typeof(Settlement).GetProperty("IsActive");
                if (isActiveProp != null && isActiveProp.CanWrite)
                {
                    isActiveProp.SetValue(hideout, true);
                }
                else
                {
                    var isActiveField = typeof(Settlement).GetField("_isVisible",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (isActiveField != null)
                        isActiveField.SetValue(hideout, true);
                }
                hideout.IsVisible = true;
                if (hideout.Hideout != null) hideout.Hideout.IsSpotted = true;
                if (hideout.Party != null)
                    hideout.Party.SetVisualAsDirty();

                if (Settings.Instance?.TestingMode == true)
                    DebugLogger.TestLog($"[Spawn] {hideout.Name} yeniden aktive edildi.", Colors.Green);
            }

            Vec2 finalSpawnPos = Vec2.Invalid;
            bool validPosFound = false;
            bool spawnIsOnLand = true;
            int maxAttempts = 15;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                float searchRadius = 5f + (attempt * 3f);
                var potentialPos = liveMapScene.GetAccessiblePointNearPosition(gatePos, searchRadius);
                if (potentialPos == null) continue;
                var vec2Pos = CompatibilityLayer.ToVec2(potentialPos);

                if (float.IsNaN(vec2Pos.X) || float.IsNaN(vec2Pos.Y)) continue;
                if (vec2Pos.X < 0 || vec2Pos.Y < 0 || vec2Pos.X > 2000 || vec2Pos.Y > 2000) continue;

                var faceIndex = liveMapScene.GetFaceIndex(potentialPos);
                if (!faceIndex.IsValid()) continue;

                var terrainType = liveMapScene.GetFaceTerrainType(faceIndex);
                bool isSeaRaider = banditClan.StringId.Contains("sea_raiders");

                if (terrainType == TerrainType.Water || terrainType == TerrainType.River)
                {
                    if (!isSeaRaider || !_hasNavalDLC.Value) continue;
                    spawnIsOnLand = false;
                }
                else
                {
                    spawnIsOnLand = true;
                }

                if (terrainType == TerrainType.Canyon) continue;

                finalSpawnPos = vec2Pos;
                validPosFound = true;
                break;
            }

            if (!validPosFound)
            {
                string msg = $"[BanditMilitias] SPAWN İPTAL: '{hideout.Name}' çevresinde geçerli bir " +
                             $"spawn alanı bulunamadı ({maxAttempts} deneme). Sonraki tick'te yeniden denenecek.";
                if (Settings.Instance?.TestingMode == true)
                {
                    DebugLogger.Log(msg);
                    InformationManager.DisplayMessage(new InformationMessage(msg, Colors.Yellow));
                }
                MarkSpawnFailure(hideout, "No valid spawn position");
                return null;
            }


            // OPTIMIZASYON: ObjectPool kullan
            var roster = CreateTroopRosterFromPool();
            var prisonerRoster = CreateTroopRosterFromPool();
            MobileParty? party = null;
            try
            {
                // HİBRİT AI: Dinamik troop sayısını hesapla
                int troopCount = CalculateDynamicSpawnSize(hideout);

                // ── TİER SCALING: Kademeli Asker Kalitesi Eğrisi (Dünya Gelişimine Duyarlı) ─────────────────
                float elapsedDays = BanditMilitias.Infrastructure.CompatibilityLayer.GetActivationDelayElapsedDays();
                if (elapsedDays < 0f) elapsedDays = 0f;
                // Day 0-10  : Sadece Tier 1-2
                // Day 10-100: Tier 3 giriyor (%40)
                // Day 100+  : Tier 5-6 her 50 günde +%3 büyür (Day 100=%3, Day 300=%15)
                // Day 300+  : Late Game — Tier 5-6 max %15, Tier 4 %38  (~3.5 Bannerlord yılı)
                var allTroops = Globals.BasicInfantry;
                var tier1_2 = allTroops.Where(c => c.Tier <= 2).ToList();
                var tier3   = allTroops.Where(c => c.Tier == 3).ToList();
                var tier4   = allTroops.Where(c => c.Tier == 4).ToList();
                var tier5_6 = allTroops.Where(c => c.Tier >= 5).ToList();

                if (tier1_2.Count == 0) tier1_2 = allTroops.ToList();

                // Tier 5-6: Day 100'de %3, her 50 günde +%3, Day 300'de %15 sabit
                float elite56Chance = 0f;
                if (elapsedDays >= 100f)
                {
                    float steps = Math.Min((elapsedDays - 100f) / 50f, 4f);
                    elite56Chance = 0.03f + steps * 0.03f;
                }

                // Tier 4: Day 100'de %25, Day 300'de %38
                float elite4Chance = 0f;
                if (elapsedDays >= 100f)
                {
                    float t4p = Math.Min((elapsedDays - 100f) / 200f, 1f);
                    elite4Chance = 0.25f + t4p * 0.13f;
                }

                // Tier 3: Day 10'da %40, Day 300'de %32'ye geriler
                float tier3Chance = 0f;
                if (elapsedDays >= 10f)
                {
                    float t3d = Math.Min((elapsedDays - 10f) / 290f, 1f);
                    tier3Chance = 0.40f - t3d * 0.08f;
                }

                int remainingToSpawn = troopCount;
                while (remainingToSpawn > 0)
                {
                    CharacterObject? selectedTroop = null;
                    float roll = MBRandom.RandomFloat;

                    if (tier5_6.Count > 0 && roll < elite56Chance)
                        selectedTroop = tier5_6.GetRandomElement();
                    else if (tier4.Count > 0 && roll < elite56Chance + elite4Chance)
                        selectedTroop = tier4.GetRandomElement();
                    else if (tier3.Count > 0 && roll < elite56Chance + elite4Chance + tier3Chance)
                        selectedTroop = tier3.GetRandomElement();
                    else
                        selectedTroop = tier1_2.GetRandomElement();

                    if (selectedTroop == null)
                    {
                        selectedTroop = allTroops.GetRandomElement();
                        if (selectedTroop == null) break;
                    }

                    int batchSize = Math.Min(MBRandom.RandomInt(2, 6), remainingToSpawn);
                    _ = roster.AddToCounts(selectedTroop, batchSize);
                    remainingToSpawn -= batchSize;
                }
                // ── TİER SCALING SONU ─────────────────────────────────────────────

                if (roster.TotalManCount <= 0)
                {
                    ReturnTroopRosterToPool(roster);
                    ReturnTroopRosterToPool(prisonerRoster);
                    return null;
                }

                var component = new MilitiaPartyComponent(hideout);
                string partyId = "Bandit_Militia_" + Guid.NewGuid().ToString("N").Substring(0, 8);

                party = BanditMilitias.Infrastructure.CompatibilityLayer
                    .CreatePartySafe(partyId, component, banditClan, true);

                if (party == null)
                {
                    ReturnTroopRosterToPool(roster);
                    ReturnTroopRosterToPool(prisonerRoster);
                    return null;
                }

                var campaignVec2Pos = BanditMilitias.Infrastructure.CompatibilityLayer
                    .CreateCampaignVec2(finalSpawnPos, spawnIsOnLand);

                party.InitializeMobilePartyAtPosition(
                    roster,
                    prisonerRoster,
                    campaignVec2Pos);

                // FIX: Robust validation (Expected by Stage5_SpawnMilitia_ValidatesPartyAfterCreation)
                if (party.ActualClan == null || party.MapFaction == null)
                {
                    BanditMilitias.Infrastructure.CompatibilityLayer.DestroyParty(party);
                    return null;
                }

                // KRİTİK: AI'yı serbest bırak (InitializeMobilePartyAtPosition genelde kilitler)
                if (party.Ai != null)
                {
                    party.Ai.SetDoNotMakeNewDecisions(false);
                }


                PartyBase? partyBase = party.Party;
                if (partyBase == null)
                {
                    BanditMilitias.Infrastructure.CompatibilityLayer.DestroyParty(party);
                    ReturnTroopRosterToPool(roster);
                    ReturnTroopRosterToPool(prisonerRoster);
                    return null;
                }

                // BAŞARILI: Roster'ları havuza geri verme - artık parti kullanıyor
                // Not: Roster'lar parti tarafından kullanıldığı için havuza geri vermiyoruz
                // Parti yok edildiğinde GC tarafından temizlenecekler

                Vec2 actualPos = BanditMilitias.Infrastructure.CompatibilityLayer.GetPartyPosition(party);

                if (party.MapEvent != null)
                {
                    BanditMilitias.Infrastructure.CompatibilityLayer.DestroyParty(party);
                    return null;
                }

                if (actualPos.IsValid)
                {
                    const float MAX_ALLOWED_DISTANCE_SQ = 160000f;
                    if (actualPos.DistanceSquared(finalSpawnPos) > MAX_ALLOWED_DISTANCE_SQ)
                    {
                        DebugLogger.Log($"[Spawn] {hideout.Name} parti çok uzağa yerleşti - iptal.");
                        BanditMilitias.Infrastructure.CompatibilityLayer.DestroyParty(party);
                        return null;
                    }
                }

                if (!party.IsActive)
                {
                    party.IsActive = true;
                    party.IsVisible = true;
                }

                // NEW: WarlordEconomySystem Integration (Walkthrough Rule: Captain 1000, Group 300)
                var warlordForHideout = WarlordSystem.Instance.GetWarlordForHideout(hideout);
                var currentTier = warlordForHideout != null 
                    ? WarlordCareerSystem.Instance.GetTier(warlordForHideout.StringId) 
                    : CareerTier.Eskiya;

                bool hideoutHasActiveMilitia = Systems.AI.MilitiaMemorySystem.Instance
                    .HasActiveMilitia(hideout);

                if (!hideoutHasActiveMilitia)
                {
                    component.Role = MilitiaPartyComponent.MilitiaRole.Captain;
                }

                party.PartyTradeGold = (int)WarlordEconomySystem.Instance.GetStartingGold(currentTier, component.Role == MilitiaPartyComponent.MilitiaRole.Captain);

                ModuleManager.Instance.RegisterMilitia(party);

                if (Settings.Instance?.EnableCustomBanditNames == true)
                {
                    TextObject customNameObj = MilitiaNameGenerator.GenerateName(hideout, banditClan);
                    if (customNameObj != null)
                        partyBase.SetCustomName(customNameObj);
                }

                int totalManCount = party.MemberRoster.TotalManCount;
                
                // LOJİSTİK PAKET: Büyük partilere ekstra ikmal
                int totalGrain = Math.Max(8, totalManCount / 3);  // İkmal artırıldı
                int totalMeat = Math.Max(2, totalManCount / 8);
                int totalButter = totalManCount > 40 ? (totalManCount / 12) : 0; // Çok büyük partilere moral desteği

                _ = party.ItemRoster.AddToCounts(DefaultItems.Grain, totalGrain);
                _ = party.ItemRoster.AddToCounts(DefaultItems.Meat, totalMeat);
                if (totalButter > 0)
                {
                    var butterObj = MBObjectManager.Instance.GetObject<ItemObject>("butter");
                    if (butterObj != null) _ = party.ItemRoster.AddToCounts(butterObj, totalButter);
                }
                
                AddRandomLoot(party);

                // NEW: Captain Starting Equipment (Walkthrough Rule: Armor, Horse, and Weapon)
                if (component.Role == MilitiaPartyComponent.MilitiaRole.Captain)
                {
                    AddCaptainEquipment(party);
                }

                partyBase.SetVisualAsDirty();

                if (Game.Current != null)
                {
                    ItemObject? horse = Game.Current.ObjectManager.GetObject<ItemObject>("midlands_palfrey")
                                     ?? Game.Current.ObjectManager.GetObject<ItemObject>("sumpter_horse");
                    if (horse != null)
                    {
                        _ = party.ItemRoster.AddToCounts(horse, Math.Max(1, totalManCount / 3));
                    }

                    var spawnEvt = EventBus.Instance.Get<BanditMilitias.Core.Events.MilitiaSpawnedEvent>();
                    if (spawnEvt != null)
                    {
                        spawnEvt.Party = party;
                        spawnEvt.HomeHideout = hideout;
                        EventBus.Instance.Publish(spawnEvt);
                        EventBus.Instance.Return(spawnEvt);
                    }
                }

                if (Settings.Instance?.TestingMode == true)
                {
                    int totalTroopsAfterSpawn = party.MemberRoster?.TotalManCount ?? 0;
                    DebugLogger.TestLog(
                        $"{party.Name} › {hideout.Name} | Asker: {totalTroopsAfterSpawn} | Rol: {component.Role}",
                        Colors.Green);
                }

                return party;
            }
            catch (Exception ex)
            {
                DebugLogger.Error("SpawningSystem",
                    $"SpawnMilitia exception: {hideout?.Name} | {ex.Message}");

                if (party != null)
                {
                    try { BanditMilitias.Infrastructure.CompatibilityLayer.DestroyParty(party); }
                    catch { }
                }

                ReturnTroopRosterToPool(roster);
                ReturnTroopRosterToPool(prisonerRoster);
                return null;
            }

        }

        public bool CanSpawn(Settlement hideout)
        {
            if (!IsEnabled) return false;
            if (Globals.BasicInfantry.Count == 0) return false;
            if (!hideout.IsHideout) return false;
            return true;
        }

        private static Clan? ResolveBanditClan(Settlement hideout)
        {
            // Önce hideout'un kendi klanını dene
            Clan? banditClan = hideout.OwnerClan;
            if (banditClan != null && banditClan.IsBanditFaction)
            {
                return banditClan;
            }

            // ClanCache'den al
            if (!ClanCache.IsInitialized)
            {
                ClanCache.Initialize();
            }

            banditClan = ClanCache.GetLootersClan() ?? ClanCache.GetFallbackBanditClan();
            if (banditClan != null)
            {
                return banditClan;
            }

            // Son çare: Doğrudan Clan.All'dan ara
            if (Clan.All != null)
            {
                Clan? fallbackBanditClan = null;
                foreach (var clan in Clan.All)
                {
                    if (clan == null || !clan.IsBanditFaction)
                        continue;

                    if (clan.StringId.IndexOf("looter", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return clan;
                    }

                    fallbackBanditClan ??= clan;
                }

                if (fallbackBanditClan != null)
                {
                    return fallbackBanditClan;
                }
            }

            return null;
        }

        private static float ResolveBaseDailySpawnChance()
        {
            return BaseDailySpawnChanceMin
                   + (MBRandom.RandomFloat * (BaseDailySpawnChanceMax - BaseDailySpawnChanceMin));
        }

        private static int CalculateDynamicMilitiaCap(int currentCount, int optimalCount, int hardCap)
        {
            int activeHideouts = Settlement.All?.Count(s => s.IsHideout && s.IsActive) ?? 0;
            float elapsedDays = 0f;

            if (Campaign.Current != null)
            {
                CampaignTime startTime = CompatibilityLayer.GetCampaignStartTime();
                if (startTime.ToHours > 0.0)
                    elapsedDays = (float)(CampaignTime.Now - startTime).ToDays;
                if (elapsedDays < 0f)
                    elapsedDays = 0f;
            }

            int hideoutBuffer = System.Math.Max(2, activeHideouts / 8);
            int timeBuffer;
            if (elapsedDays < 20f) timeBuffer = 2;
            else if (elapsedDays < 60f) timeBuffer = 4;
            else if (elapsedDays < 120f) timeBuffer = 6;
            else if (elapsedDays < 240f) timeBuffer = 8;
            else timeBuffer = 10;

            int dynamicCap = optimalCount + hideoutBuffer + timeBuffer;
            if (currentCount <= optimalCount / 2)
                dynamicCap += 2;

            if (dynamicCap < 15)
                dynamicCap = 15;
            if (dynamicCap > hardCap)
                dynamicCap = hardCap;

            return dynamicCap;
        }

        private static float CalculatePopulationAdjustedSpawnChance(float baseChance, int currentCount, int optimalCount, int dynamicCap)
        {
            if (currentCount >= dynamicCap)
                return baseChance * 0.05f; // Çok fazla varsa iyice kıs

            // KRİTİK EKSİKLİK (0-5 Arası): Devasa boost (10x)
            if (currentCount < 5)
                return baseChance * 10.0f;

            // ÇOK DÜŞÜK (%25 altı): Büyük boost (4x)
            if (currentCount < optimalCount * 0.25f)
                return baseChance * 4.0f;

            // DÜŞÜK (%80 altı): Standart boost (1.5x)
            if (currentCount < optimalCount * 0.8f)
                return baseChance * 1.5f;

            if (currentCount > optimalCount * 1.2f)
                return baseChance * 0.45f;

            return baseChance;
        }

        private void MarkSpawnFailure(Settlement hideout, string reason, float cooldownHours = 6f)
        {
            if (hideout == null) return;
            try
            {
                var behavior = Campaign.Current?.GetCampaignBehavior<MilitiaBehavior>();
                behavior?.SetHideoutFailureCooldown(hideout, cooldownHours);
            }
            catch
            {
            }

            if (Settings.Instance?.TestingMode == true)
            {
                DebugLogger.Warning("SpawningSystem",
                    $"Spawn failure cooldown set: {hideout.Name} ({cooldownHours:F1}h) - {reason}");
            }
        }

        private void AddRandomLoot(MobileParty party)
        {
            if (party?.ItemRoster == null) return;

            if (DefaultItems.HardWood != null)
                _ = party.ItemRoster.AddToCounts(DefaultItems.HardWood, MBRandom.RandomInt(2, 10));

            if (DefaultItems.Tools != null)
                _ = party.ItemRoster.AddToCounts(DefaultItems.Tools, MBRandom.RandomInt(1, 4));
        }

        private void AddCaptainEquipment(MobileParty party)
        {
            if (party?.ItemRoster == null) return;

            // Armor (Gambeson/Leather)
            var armor = Game.Current.ObjectManager.GetObject<ItemObject>("leather_armor") 
                     ?? Game.Current.ObjectManager.GetObject<ItemObject>("gambeson");
            if (armor != null) _ = party.ItemRoster.AddToCounts(armor, 1);

            // Weapon (Sword/Axe)
            var weapon = Game.Current.ObjectManager.GetObject<ItemObject>("iron_spatha") 
                      ?? Game.Current.ObjectManager.GetObject<ItemObject>("hand_axe");
            if (weapon != null) _ = party.ItemRoster.AddToCounts(weapon, 1);
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("svrm.42", "sys.bandit.sys")]
        public static string SpawnSwarm(System.Collections.Generic.List<string> args)
        {
            if (MobileParty.MainParty == null) return "Main Party bulunamadý.";

            int count = 5;
            if (args.Count > 0) _ = int.TryParse(args[0], out count);

            var system = ModuleManager.Instance.GetModule<MilitiaSpawningSystem>();
            if (system == null) return "Hata: SpawningSystem aktif deðil.";

            var hideout = ModuleManager.Instance.HideoutCache.FirstOrDefault(s => s.IsActive);
            if (hideout == null) return "Aktif hideout bulunamadý.";

            int spawned = 0;
            for (int i = 0; i < count; i++)
            {
                var p = system.SpawnMilitia(hideout, true);
                if (p != null) spawned++;
            }

            return $"{spawned} parti spawn edildi.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("spawn_swarm", "bandit_militias")]
        public static string SpawnSwarmAlias(System.Collections.Generic.List<string> args)
            => SpawnSwarm(args);

        [CommandLineFunctionality.CommandLineArgumentFunction("spawn_all", "bandit_militias")]
        public static string SpawnAll(List<string> args)
        {
            var instance = ModuleManager.Instance.GetModule<MilitiaSpawningSystem>();
            if (instance == null) return "SpawningSystem modülü bulunamadý!";

            int spawned = 0;
            var hideouts = ModuleManager.Instance.HideoutCache;

            foreach (var hideout in hideouts)
            {
                var party = instance.SpawnMilitia(hideout, force: true);
                if (party != null) spawned++;
            }

            return $"Tamamlandý! {hideouts.Count} hideout'tan {spawned} parti spawn edildi.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("debug_hideout", "bandit_militias")]
        public static string DebugHideout(List<string> args)
        {
            if (args.Count == 0)
                return "Kullaným: bandit_militias.debug_hideout <hideout_adý>";

            string hideoutName = string.Join(" ", args);
            var hideout = Settlement.All.FirstOrDefault(s =>
                s.IsHideout &&
                s.Name.ToString().IndexOf(hideoutName, StringComparison.OrdinalIgnoreCase) >= 0);

            if (hideout == null)
                return $"'{hideoutName}' bulunamadý!";

            var sb = new System.Text.StringBuilder();
            _ = sb.AppendLine("============================================");
            _ = sb.AppendLine($"HIDEOUT DEBUG: {hideout.Name}");
            _ = sb.AppendLine("============================================");
            _ = sb.AppendLine($"String ID : {hideout.StringId}");
            _ = sb.AppendLine($"IsActive  : {hideout.IsActive}");
            _ = sb.AppendLine($"IsHideout : {hideout.IsHideout}");
            _ = sb.AppendLine($"OwnerClan : {hideout.OwnerClan?.Name?.ToString() ?? "NULL"}");
            _ = sb.AppendLine($"Culture   : {hideout.Culture?.Name?.ToString() ?? "NULL"}");
            _ = sb.AppendLine("¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦");

            var gatePos = hideout.GatePosition;
            _ = sb.AppendLine($"GatePosition: ({gatePos.X:F4}, {gatePos.Y:F4})");
            _ = sb.AppendLine($"  IsNaN      : X={float.IsNaN(gatePos.X)}, Y={float.IsNaN(gatePos.Y)}");
            _ = sb.AppendLine($"  IsInfinity : X={float.IsInfinity(gatePos.X)}, Y={float.IsInfinity(gatePos.Y)}");
            _ = sb.AppendLine($"  Is (0,0)   : {gatePos.X == 0f && gatePos.Y == 0f}");
            _ = sb.AppendLine("¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦");
            _ = sb.AppendLine("Blacklist: DEVRE DISI");
            _ = sb.AppendLine("¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦¦");

            try
            {
                var mapScene = Campaign.Current.MapSceneWrapper;
                _ = sb.AppendLine("NavMesh Testleri:");

                for (int radius = 1; radius <= 10; radius += 3)
                {
                    var testPos = mapScene.GetAccessiblePointNearPosition(gatePos, radius);
                    bool isNaN = float.IsNaN(testPos.X) || float.IsNaN(testPos.Y);
                    _ = sb.AppendLine($"  Radius {radius}f: ({testPos.X:F2}, {testPos.Y:F2})  {(isNaN ? "NaN!" : "OK")}");

                    if (!isNaN)
                    {
                        var faceIndex = mapScene.GetFaceIndex(testPos);
                        _ = sb.AppendLine(faceIndex.IsValid()
                            ? $"    Terrain: {mapScene.GetFaceTerrainType(faceIndex)}"
                            : "    Terrain: Geçersiz FaceIndex");
                    }
                }
            }
            catch (Exception ex)
            {
                _ = sb.AppendLine($"NavMesh HATA: {ex.Message}");
            }

            _ = sb.AppendLine("============================================");

            string result = sb.ToString();
            try { BanditMilitias.Infrastructure.FileLogger.Log($"DEBUG HIDEOUT: {hideout.Name}\n{result}"); }
            catch (Exception ex) { DebugLogger.Log($"[Spawn] Log yazýlamadý: {ex.Message}"); }

            return result;
        }

        #region Helper Methods

        /// <summary>
        /// Activation Delay kontrolü - oyun başladıktan X gün sonra aktif ol
        #endregion



        /// <summary>
        /// ObjectPool kullanarak TroopRoster oluştur
        /// </summary>
        private TroopRoster CreateTroopRosterFromPool()
        {
            return TroopRosterPool.Rent();
        }

        /// <summary>
        /// TroopRoster'ı havuza geri ver
        /// </summary>
        private void ReturnTroopRosterToPool(TroopRoster roster)
        {
            if (roster != null)
            {
                TroopRosterPool.Return(roster);
            }
        }

    }

    // ── Inline: SpawnDecisionRules ────────────────────────────────
    public static class SpawnDecisionRules
    {
        public static bool ShouldResetDailySpawnCounter(float elapsedDaysSinceLastSpawn)
            => elapsedDaysSinceLastSpawn >= 1.0f;
    }
}
