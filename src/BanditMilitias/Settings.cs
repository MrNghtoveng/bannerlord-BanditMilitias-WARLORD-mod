using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;
using MCM.Abstractions.Base.Global;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using TaleWorlds.Localization;
namespace BanditMilitias
{

    public class Settings : AttributeGlobalSettings<Settings>
    {
        public override string Id => "BanditMilitias_Warlord";
        public override string DisplayName => new TextObject("{=BMS_DisplayName}Bandit Militias: WARLORD").ToString();
        public override string FolderName => "BanditMilitias";
        public override string FormatType => "json";

        [SettingPropertyBool("{=BMS_MilitiaSpawn}Enable Militia Spawning", Order = 0, RequireRestart = false,
            HintText = "{=BMS_MilitiaSpawn_H}Master switch for militia spawning. Disabled = hideouts behave like vanilla.")]
        [SettingPropertyGroup("{=BMSG_Spawn}1. Spawning & Population")]
        public bool MilitiaSpawn { get; set; } = true;

        [SettingPropertyInteger("{=BMS_MaxTotalMilitias}Max Total Militias", 10, 200, "0 parties", Order = 1, RequireRestart = false,
            HintText = "{=BMS_MaxTotalMilitias_H}Hard cap on active militia parties across the entire map.")]
        [SettingPropertyGroup("{=BMSG_Spawn}1. Spawning & Population")]
        public int MaxTotalMilitias { get; set; } = 200;

        [SettingPropertyInteger("{=BMS_GlobalPartyLimit}Global Performance Party Limit", 1000, 5000, "0 parties", Order = 2, RequireRestart = false,
            HintText = "{=BMS_GlobalPartyLimit}Map-wide total party count (including lords) at which spawning is suspended to protect FPS.")]
        [SettingPropertyGroup("{=BMSG_Spawn}1. Spawning & Population")]
        public int GlobalPerformancePartyLimit { get; set; } = 3000;

        [SettingPropertyInteger("{=BMS_HideoutCooldown}Respawn Cooldown (Days)", 1, 30, "0 days", Order = 3, RequireRestart = false,
            HintText = "{=BMS_HideoutCooldown_H}Days to wait after a militia is destroyed before a new one spawns from the same hideout.")]
        [SettingPropertyGroup("{=BMSG_Spawn}1. Spawning & Population")]
        public int HideoutCooldownDays { get; set; } = 1;

        [SettingPropertyInteger("{=BMS_ActivationDelay}Initial Activation Delay (Days)", 2, 30, "0 days", Order = 3, RequireRestart = false,
            HintText = "{=BMS_ActivationDelay_H}Days after the world map opens before spawning begins. Applies to both Campaign and Sandbox.")]
        [SettingPropertyGroup("{=BMSG_Spawn}1. Spawning & Population")]
        public int ActivationDelay { get; set; } = 2;

        [SettingPropertyBool("{=BMS_SeaRaiders}Enable Sea Raiders", Order = 4, RequireRestart = false,
            HintText = "{=BMS_SeaRaiders_H}Allows Sea Raider hideouts to spawn militias.")]
        [SettingPropertyGroup("{=BMSG_Spawn}1. Spawning & Population")]
        public bool EnableSeaRaiders { get; set; } = true;

        [SettingPropertyBool("{=BMS_CustomNames}Custom Bandit Names", Order = 5, RequireRestart = false,
            HintText = "{=BMS_CustomNames_H}Militias receive unique, flavourful names instead of the generic 'Bandit Militia'.")]
        [SettingPropertyGroup("{=BMSG_Spawn}1. Spawning & Population")]
        public bool EnableCustomBanditNames { get; set; } = true;

        [SettingPropertyBool("{=BMS_DynHideout}Enable Dynamic Hideout Formation", Order = 0, RequireRestart = false,
            HintText = "{=BMS_DynHideout_H}Bandits can establish brand-new hideouts in lawless regions.")]
        [SettingPropertyGroup("{=BMSG_DynHideout}1. Spawning & Population/Dynamic Hideouts", GroupOrder = 1)]
        public bool EnableDynamicHideouts { get; set; } = true;

        [SettingPropertyBool("{=BMS_HardcoreHideout}Hardcore Dynamic Hideouts", Order = 1, RequireRestart = false,
            HintText = "{=BMS_HardcoreHideout_H}EXPERIMENTAL: Dynamically injects completely new hideout settlements into the map mid-game. Warning: Can cause save instability.")]
        [SettingPropertyGroup("{=BMSG_DynHideout}1. Spawning & Population/Dynamic Hideouts")]
        public bool EnableHardcoreDynamicHideouts { get; set; } = false;

        [SettingPropertyInteger("{=BMS_MinParties}Min Parties for Formation", 2, 20, "0 parties", Order = 1, RequireRestart = false,
            HintText = "{=BMS_MinParties_H}Number of nearby bandit parties required to trigger a new hideout.")]
        [SettingPropertyGroup("{=BMSG_DynHideout}1. Spawning & Population/Dynamic Hideouts")]
        public int MinPartiesForHideout { get; set; } = 4;

        [SettingPropertyInteger("{=BMS_FormCooldown}Formation Cooldown (Days)", 1, 90, "0 days", Order = 2, RequireRestart = false,
            HintText = "{=BMS_FormCooldown_H}Minimum days between hideout formation attempts.")]
        [SettingPropertyGroup("{=BMSG_DynHideout}1. Spawning & Population/Dynamic Hideouts")]
        public int HideoutFormationCooldown { get; set; } = 30;

        [SettingPropertyFloatingInteger("{=BMS_PatrolDensity}Max Patrol Density", 0f, 5f, "0.0", Order = 3, RequireRestart = false,
            HintText = "{=BMS_PatrolDensity_H}Formation is blocked when lord patrol density exceeds this value. Higher = easier formation.")]
        [SettingPropertyGroup("{=BMSG_DynHideout}1. Spawning & Population/Dynamic Hideouts")]
        public float MaxPatrolDensityForHideout { get; set; } = 1.0f;

        [SettingPropertyBool("{=BMS_EnableWarlords}Enable Warlords", Order = 0, RequireRestart = false,
            HintText = "{=BMS_EnableWarlords_H}Powerful 'Anti-Hero' warlords emerge to command bandit forces. Disabling this reduces the mod to a simple spawn enhancer.")]
        [SettingPropertyGroup("{=BMSG_AI}2. Warlords & AI")]
        public bool EnableWarlords { get; set; } = true;

        [SettingPropertyBool("{=BMS_CustomAI}Custom AI Behaviors", Order = 1, RequireRestart = false,
            HintText = "{=BMS_CustomAI_H}Advanced militia AI: territory control, swarm coordination, strategic retreats. Disabled = vanilla AI logic.")]
        [SettingPropertyGroup("{=BMSG_AI}2. Warlords & AI")]
        public bool EnableCustomAI { get; set; } = true;

        [SettingPropertyBool("{=BMS_WarlordRegen}Warlord Regeneration", Order = 2, RequireRestart = false,
            HintText = "{=BMS_WarlordRegen_H}Warlords and their bodyguards slowly regenerate HP during battle.")]
        [SettingPropertyGroup("{=BMSG_AI}2. Warlords & AI")]
        public bool EnableWarlordRegeneration { get; set; } = true;

        [SettingPropertyBool("{=BMS_WarlordTactics}Warlord Tactics", Order = 3, RequireRestart = false,
            HintText = "{=BMS_WarlordTactics_H}Warlords analyze the player's army composition and field counter-units (cavalry vs. infantry vs. ranged).")]
        [SettingPropertyGroup("{=BMSG_AI}2. Warlords & AI")]
        public bool EnableWarlordTactics { get; set; } = true;

        [SettingPropertyInteger("{=BMS_WarlordMinTroops}Min Troops for Warlord", 5, 200, "0 troops", Order = 0, RequireRestart = false,
            HintText = "{=BMS_WarlordMinTroops_H}Regional bandit strength required before a Warlord can emerge.")]
        [SettingPropertyGroup("{=BMSG_Promotion}2. Warlord & AI/Promotion", GroupOrder = 1)]
        public int WarlordMinTroops { get; set; } = 15;

        [SettingPropertyInteger("{=BMS_WarlordMinDays}Min Days Alive (Captain)", 7, 120, "0 days", Order = 1, RequireRestart = false,
            HintText = "{=BMS_WarlordMinDays_H}Minimum days a Captain must survive before being eligible for Warlord promotion.")]
        [SettingPropertyGroup("{=BMSG_Promotion}2. Warlords & AI/Promotion")]
        public int WarlordMinDaysAlive { get; set; } = 30;

        [SettingPropertyInteger("{=BMS_WarlordMinBattles}Min Battles Won (Captain)", 1, 20, "0 battles", Order = 2, RequireRestart = false,
            HintText = "{=BMS_WarlordMinBattles_H}Minimum battles a Captain must win before Warlord promotion.")]
        [SettingPropertyGroup("{=BMSG_Promotion}2. Warlords & AI/Promotion")]
        public int WarlordMinBattlesWon { get; set; } = 3;

        [SettingPropertyInteger("{=BMS_FamousBanditFallback}Famous Bandit Fallback Days", 30, 200, "0 days", Order = 3, RequireRestart = false,
            HintText = "{=BMS_FamousBanditFallback_H}If no Famous Bandit exists after this many days, the top 3 Captains are force-promoted.")]
        [SettingPropertyGroup("{=BMSG_Promotion}2. Warlords & AI/Promotion")]
        public int FamousBanditFallbackDays { get; set; } = 60;

        [SettingPropertyInteger("{=BMS_MaxWarlords}Max Warlords", 0, 50, "0 lords", Order = 4, RequireRestart = false,
            HintText = "{=BMS_MaxWarlords_H}Maximum concurrent Warlords on the map. (Recommended: 5 | Chaos: 20+)")]
        [SettingPropertyGroup("{=BMSG_Promotion}2. Warlord & AI/Promotion")]
        public int MaxWarlordCount { get; set; } = 50;

        [SettingPropertyBool("{=BMS_ChaosNerf}Reduce Chaos (Nerf Excess Lords)", Order = 5, RequireRestart = false,
            HintText = "{=BMS_ChaosNerf_H}If Warlords exceed the cap, excess ones are weakened to Famous Bandit level — making them easier to eliminate.")]
        [SettingPropertyGroup("{=BMSG_Promotion}2. Warlord & AI/Promotion")]
        public bool EnableChaosNerf { get; set; } = false;

        [SettingPropertyBool("{=BMS_AlwaysSpawnCaptain}Always Spawn Captain", Order = 6, RequireRestart = false,
            HintText = "{=BMS_AlwaysSpawnCaptain_H}If enabled, EVERY new militia party will spawn with a Captain Hero. If disabled, captains will spawn randomly.")]
        [SettingPropertyGroup("{=BMSG_Promotion}2. Warlords & AI/Promotion")]
        public bool AlwaysSpawnCaptain { get; set; } = true;

        [SettingPropertyBool("{=BMS_AdaptiveDoctrine}Enable Adaptive Doctrine", Order = 0, RequireRestart = false,
            HintText = "{=BMS_AdaptiveDoctrine_H}Militias study the player's combat style (cavalry / ranged / infantry / mixed) and adapt counter-strategies.")]
        [SettingPropertyGroup("{=BMSG_Doctrine}2. Warlords & AI/Adaptive Doctrine", GroupOrder = 2)]
        public bool EnableAdaptiveAIDoctrine { get; set; } = true;

        [SettingPropertyFloatingInteger("{=BMS_LearningRate}Learning Rate", 0.05f, 1.00f, "0.00", Order = 1, RequireRestart = false,
            HintText = "{=BMS_LearningRate_H}How quickly doctrine confidence shifts after battle outcomes. Higher = faster adaptation.")]
        [SettingPropertyGroup("{=BMSG_Doctrine}2. Warlords & AI/Adaptive Doctrine")]
        public float AdaptiveDoctrineLearningRate { get; set; } = 0.30f;

        [SettingPropertyInteger("{=BMS_SwitchCooldown}Switch Cooldown (Hours)", 6, 120, "0 h", Order = 2, RequireRestart = false,
            HintText = "{=BMS_SwitchCooldown_H}Minimum hours before a Warlord can switch to a different doctrine.")]
        [SettingPropertyGroup("{=BMSG_Doctrine}2. Warlords & AI/Adaptive Doctrine")]
        public int AdaptiveDoctrineSwitchCooldownHours { get; set; } = 36;

        [SettingPropertyFloatingInteger("{=BMS_AggressionBias}Aggression Bias", -0.50f, 0.50f, "0.00", Order = 3, RequireRestart = false,
            HintText = "{=BMS_AggressionBias_H}Fine-tune doctrine aggression. Negative = cautious, Positive = reckless.")]
        [SettingPropertyGroup("{=BMSG_Doctrine}2. Warlords & AI/Adaptive Doctrine")]
        public float AdaptiveDoctrineAggressionBias { get; set; } = 0.00f;

        [SettingPropertyBool("{=BMS_Politics}Bandit Politics", Order = 0, RequireRestart = false,
            HintText = "{=BMS_Politics_H}Enables alliances, rivalries, and betrayals between active warlords.")]
        [SettingPropertyGroup("{=BMSG_Diplo}3. Politics & Diplomacy")]
        public bool EnableBanditPolitics { get; set; } = true;

        [SettingPropertyBool("{=BMS_Propaganda}Propaganda", Order = 1, RequireRestart = false,
            HintText = "{=BMS_Propaganda_H}Warlords can run propaganda campaigns to erode town loyalty.")]
        [SettingPropertyGroup("{=BMSG_Diplo}3. Politics & Diplomacy")]
        public bool EnablePropaganda { get; set; } = true;

        [SettingPropertyBool("{=BMS_BlackMarket}Black Market", Order = 2, RequireRestart = false,
            HintText = "{=BMS_BlackMarket_H}Warlords bribe officials to establish underground networks, earning daily gold income.")]
        [SettingPropertyGroup("{=BMSG_Diplo}3. Politics & Diplomacy")]
        public bool EnableBlackMarket { get; set; } = true;

        [SettingPropertyInteger("{=BMS_BribeCostPerMan}Bribe Cost Per Man", 10, 500, "0 gold", Order = 3, RequireRestart = false,
            HintText = "{=BMS_BribeCostPerMan_H}Gold cost per militia member during diplomacy bribe events.")]
        [SettingPropertyGroup("{=BMSG_Diplo}3. Politics & Diplomacy")]
        public int BribeCostPerMan { get; set; } = 50;

        [SettingPropertyInteger("{=BMS_AllianceThreshold}Alliance Threshold", 0, 100, "0 score", Order = 0, RequireRestart = false,
            HintText = "{=BMS_AllianceThreshold_H}Minimum relation score required for two warlords to form an alliance.")]
        [SettingPropertyGroup("{=BMSG_RelThreshold}3. Politics & Diplomacy/Relation Thresholds", GroupOrder = 1)]
        public int PoliticsAllianceThreshold { get; set; } = 40;

        [SettingPropertyInteger("{=BMS_RivalryThreshold}Rivalry Threshold", -100, 0, "0 score", Order = 1, RequireRestart = false,
            HintText = "{=BMS_RivalryThreshold_H}Relations at or below this value trigger open rivalry.")]
        [SettingPropertyGroup("{=BMSG_RelThreshold}3. Politics & Diplomacy/Relation Thresholds")]
        public int PoliticsRivalryThreshold { get; set; } = -35;

        [SettingPropertyInteger("{=BMS_BetrayalThreshold}Betrayal Threshold", -100, 0, "0 score", Order = 2, RequireRestart = false,
            HintText = "{=BMS_BetrayalThreshold_H}An allied warlord may betray the other once relations fall below this value.")]
        [SettingPropertyGroup("{=BMSG_RelThreshold}3. Politics & Diplomacy/Relation Thresholds")]
        public int PoliticsBetrayalThreshold { get; set; } = -55;

        [SettingPropertyFloatingInteger("{=BMS_DailyDrift}Daily Relation Drift", 0.00f, 0.20f, "0.00", Order = 3, RequireRestart = false,
            HintText = "{=BMS_DailyDrift_H}How quickly political relations drift back toward neutrality each day.")]
        [SettingPropertyGroup("{=BMSG_RelThreshold}3. Politics & Diplomacy/Relation Thresholds")]
        public float PoliticsDailyDrift { get; set; } = 0.04f;

        [SettingPropertyBool("{=BMS_EnhancedBandits}Enhanced Bandits", Order = 0, RequireRestart = false,
            HintText = "{=BMS_EnhancedBandits_H}Upgrades bandits with better gear and combat stats. Highly recommended for mid-to-late game.")]
        [SettingPropertyGroup("{=BMSG_Combat}4. Combat & Loot")]
        public bool EnhancedBandits { get; set; } = true;

        [SettingPropertyInteger("{=BMS_SkillBoost}Bandit Skill Boost", 0, 100, "0 points", Order = 1, RequireRestart = false,
            HintText = "{=BMS_SkillBoost_H}Flat skill point bonus applied to enhanced bandits.")]
        [SettingPropertyGroup("{=BMSG_Combat}4. Combat & Loot")]
        public int BanditSkillBoost { get; set; } = 30;

        [SettingPropertyInteger("{=BMS_EqQuality}Equipment Quality", 0, 5, "Level 0", Order = 2, RequireRestart = false,
            HintText = "{=BMS_EqQuality_H}Overall gear quality for enhanced bandits. 0 = vanilla, 5 = elite.")]
        [SettingPropertyGroup("{=BMSG_Combat}4. Combat & Loot")]
        public int EquipmentQuality { get; set; } = 2;

        [SettingPropertyBool("{=BMS_HeroicFeats}Heroic Feats", Order = 3, RequireRestart = false,
            HintText = "{=BMS_HeroicFeats_H}Defeating stronger enemies can reward Focus points or rare high-tier items.")]
        [SettingPropertyGroup("{=BMSG_Combat}4. Combat & Loot")]
        public bool EnableHeroicFeats { get; set; } = true;

        [SettingPropertyFloatingInteger("{=BMS_GoldMult}Gold Multiplier", 0.5f, 5.0f, "0.0x", Order = 0, RequireRestart = false,
            HintText = "{=BMS_GoldMult_H}Multiplies gold dropped by enhanced bandits.")]
        [SettingPropertyGroup("{=BMSG_Rewards}4. Combat & Loot/Rewards & Multipliers", GroupOrder = 1)]
        public float GoldRewardMultiplier { get; set; } = 1.5f;

        [SettingPropertyFloatingInteger("{=BMS_RenownMult}Renown Multiplier", 0.5f, 5.0f, "0.0x", Order = 1, RequireRestart = false,
            HintText = "{=BMS_RenownMult_H}Multiplies Renown earned from mod battles.")]
        [SettingPropertyGroup("{=BMSG_Rewards}4. Combat & Loot/Rewards & Multipliers")]
        public float RenownRewardMultiplier { get; set; } = 1.5f;

        [SettingPropertyFloatingInteger("{=BMS_WarlordLoot}Warlord Loot Bonus", 1.0f, 5.0f, "0.0x", Order = 2, RequireRestart = false,
            HintText = "{=BMS_WarlordLoot_H}Additional loot multiplier exclusive to Warlord battles.")]
        [SettingPropertyGroup("{=BMSG_Rewards}4. Combat & Loot/Rewards & Multipliers")]
        public float WarlordRewardMultiplier { get; set; } = 2.0f;

        [SettingPropertyFloatingInteger("{=BMS_HardBattle}Hard Battle Bonus", 0.5f, 10f, "0.0x", Order = 3, RequireRestart = false,
            HintText = "{=BMS_HardBattle_H}Reward multiplier for hard-fought battles. 2.0 = double rewards.")]
        [SettingPropertyGroup("{=BMSG_Rewards}4. Combat & Loot/Rewards & Multipliers")]
        public float HardBattleBonusMultiplier { get; set; } = 2.0f;

        [SettingPropertyBool("{=BMS_AttrRewards}Attribute & Focus Rewards", Order = 0, RequireRestart = false,
            HintText = "{=BMS_AttrRewards_H}Small chance to gain an Attribute or Focus point from very challenging battles.")]
        [SettingPropertyGroup("{=BMSG_HeroicFeats}4. Combat & Loot/Heroic Feats", GroupOrder = 2)]
        public bool EnableHeroicAttributes { get; set; } = false;

        [SettingPropertyBool("{=BMS_QualityDrops}Quality Item Drops", Order = 1, RequireRestart = false,
            HintText = "{=BMS_QualityDrops_H}Chance to receive high-quality item drops from difficult battles.")]
        [SettingPropertyGroup("{=BMSG_HeroicFeats}4. Combat & Loot/Heroic Feats")]
        public bool HeroItemRewards { get; set; } = true;

        [SettingPropertyFloatingInteger("{=BMS_BanditDensity}Bandit Density Multiplier", 0.1f, 5.0f, "0.0x", Order = 0, RequireRestart = true,
            HintText = "{=BMS_BanditDensity_H}Scales total bandit population (vanilla + mod). High values significantly impact performance. Requires restart.")]
        [SettingPropertyGroup("{=BMSG_World}5. World & Environment")]
        public float BanditDensityMultiplier { get; set; } = 5.0f;

        [SettingPropertyFloatingInteger("{=BMS_PartySizeMult}Party Size Multiplier", 0.1f, 5.0f, "0.0x", Order = 1, RequireRestart = false,
            HintText = "{=BMS_PartySizeMult_H}Multiplies maximum party size for all bandit groups.")]
        [SettingPropertyGroup("{=BMSG_World}5. World & Environment")]
        public float BanditSizeMultiplier { get; set; } = 1.2f;

        [SettingPropertyFloatingInteger("{=BMS_WarZoneMult}War Zone Spawn Multiplier", 1.0f, 5.0f, "0.0x", Order = 2, RequireRestart = false,
            HintText = "{=BMS_WarZoneMult_H}Increases spawn rates in kingdoms currently at war. Chaos breeds bandits!")]
        [SettingPropertyGroup("{=BMSG_World}5. World & Environment")]
        public float WarSpawnMultiplier { get; set; } = 1.5f;

        [SettingPropertyFloatingInteger("{=BMS_TradeRouteMult}Trade Route Spawn Multiplier", 1.0f, 5.0f, "0.0x", Order = 3, RequireRestart = false,
            HintText = "{=BMS_TradeRouteMult_H}Increases spawn rates near heavy caravan routes and prosperous settlements.")]
        [SettingPropertyGroup("{=BMSG_World}5. World & Environment")]
        public float TradeSpawnMultiplier { get; set; } = 1.3f;

        [SettingPropertyFloatingInteger("{=BMS_NightAmbush}Night Ambush Bonus", 0f, 50f, "0 pts", Order = 0, RequireRestart = false,
            HintText = "{=BMS_NightAmbush_H}Flat AI aggression bonus during night hours.")]
        [SettingPropertyGroup("{=BMSG_Tactical}5. World & Environment/Tactical Modifiers", GroupOrder = 1)]
        public float NightAmbushBonus { get; set; } = 15f;

        [SettingPropertyFloatingInteger("{=BMS_ForestBonus}Forest Advantage Bonus", 0f, 50f, "0 pts", Order = 1, RequireRestart = false,
            HintText = "{=BMS_ForestBonus_H}Flat AI aggression bonus in forested terrain.")]
        [SettingPropertyGroup("{=BMSG_Tactical}5. World & Environment/Tactical Modifiers")]
        public float TerrainAdvantageBonus { get; set; } = 10f;

        [SettingPropertyFloatingInteger("{=BMS_PatrolAggression}Patrol Aggression Threshold", 0.5f, 5.0f, "0.0x", Order = 2, RequireRestart = false,
            HintText = "{=BMS_PatrolAggression_H}Strength ratio required before a militia will attack a lord's patrol. Lower = more aggressive.")]
        [SettingPropertyGroup("{=BMSG_Tactical}5. World & Environment/Tactical Modifiers")]
        public float AggressivePatrolThreshold { get; set; } = 1.5f;

        [SettingPropertyBool("{=BMS_FearSystem}Fear System", Order = 0, RequireRestart = false,
            HintText = "{=BMS_FearSystem_H}Villages pay tribute or betray warlords based on accumulated fear.")]
        [SettingPropertyGroup("{=BMSG_Powers}6. Warlord Powers")]
        public bool EnableFearSystem { get; set; } = true;

        [SettingPropertyBool("{=BMS_WarlordLegacy}Warlord Legacy", Order = 1, RequireRestart = false,
            HintText = "{=BMS_WarlordLegacy_H}Fallen warlords leave fear echoes that affect new spawns and villages.")]
        [SettingPropertyGroup("{=BMSG_Powers}6. Warlord Powers")]
        public bool EnableWarlordLegacy { get; set; } = true;

        [SettingPropertyBool("{=BMS_CrisisEvents}Crisis Events", Order = 1, RequireRestart = false,
            HintText = "{=BMS_CrisisEvents_H}Dynamic crisis events triggered by warlord actions.")]
        [SettingPropertyGroup("{=BMSG_Powers}6. Warlord Powers")]
        public bool EnableCrisisEvents { get; set; } = true;

        [SettingPropertyBool("{=BMS_Legitimacy}Legitimacy System", Order = 1, RequireRestart = false,
            HintText = "{=BMS_Legitimacy_H}Warlords can rise through ranks: Outlaw -> Rebel -> Famous Bandit -> Warlord -> Recognized Lord.")]
        [SettingPropertyGroup("{=BMSG_Powers}6. Warlord Powers")]
        public bool EnableLegitimacySystem { get; set; } = true;

        [SettingPropertyBool("{=BMS_BountySystem}Bounty System", Order = 2, RequireRestart = false,
            HintText = "{=BMS_BountySystem_H}Notorious warlords attract bounties and dedicated hunter parties.")]
        [SettingPropertyGroup("{=BMSG_Powers}6. Warlord Powers")]
        public bool EnableBountySystem { get; set; } = true;

        [SettingPropertyBool("{=BMS_MilitiaRaids}Militia Raids", Order = 3, RequireRestart = false,
            HintText = "{=BMS_MilitiaRaids_H}Militia parties can raid villages for loot and to spread fear.")]
        [SettingPropertyGroup("{=BMSG_Powers}6. Warlord Powers")]
        public bool EnableMilitiaRaids { get; set; } = true;

        [SettingPropertyFloatingInteger("{=BMS_RaidLoot}Raid Loot Multiplier", 0.5f, 5f, "0.0x", Order = 4, RequireRestart = false,
            HintText = "{=BMS_RaidLoot_H}Multiplies the gold earned from militia raids.")]
        [SettingPropertyGroup("{=BMSG_Powers}6. Warlord Powers")]
        public float RaidLootMultiplier { get; set; } = 1.0f;

        [SettingPropertyBool("{=BMS_Jailbreak}Jailbreak System", Order = 5, RequireRestart = false,
            HintText = "{=BMS_Jailbreak_H}Captured warlords attempt to escape imprisonment via bribes or underground contacts.")]
        [SettingPropertyGroup("{=BMSG_Powers}6. Warlord Powers")]
        public bool EnableJailbreakSystem { get; set; } = true;

        [SettingPropertyBool("{=BMS_Logistics}Warlord Logistics", Order = 6, RequireRestart = false,
            HintText = "{=BMS_Logistics_H}Warlords manage supply lines and resource extraction to sustain their armies.")]
        [SettingPropertyGroup("{=BMSG_Powers}6. Warlord Powers")]
        public bool EnableWarlordLogistics { get; set; } = true;

        [SettingPropertyInteger("{=BMS_CapLockHours}Escape Lock (Hours)", 0, 240, "0 h", Order = 0, RequireRestart = false,
            HintText = "{=BMS_CapLockHours_H}Minimum hours of captivity before escape attempts begin.")]
        [SettingPropertyGroup("{=BMSG_Captivity}9. Captivity & Surrender", GroupOrder = 9)]
        public int CaptivityEscapeLockHours { get; set; } = 24;

        [SettingPropertyInteger("{=BMS_CapMaxDays}Max Captivity (Days)", 1, 30, "0 days", Order = 1, RequireRestart = false,
            HintText = "{=BMS_CapMaxDays_H}Hard cap on captivity length. Player is released when exceeded.")]
        [SettingPropertyGroup("{=BMSG_Captivity}9. Captivity & Surrender")]
        public int CaptivityMaxDays { get; set; } = 10;

        [SettingPropertyFloatingInteger("{=BMS_CapBaseChance}Base Escape Chance / Hour", 0.0001f, 0.05f, "0.0000", Order = 2, RequireRestart = false,
            HintText = "{=BMS_CapBaseChance_H}Base hourly escape chance after the lock period.")]
        [SettingPropertyGroup("{=BMSG_Captivity}9. Captivity & Surrender")]
        public float CaptivityBaseChancePerHour { get; set; } = 0.003f;

        [SettingPropertyFloatingInteger("{=BMS_CapRogueryBonus}Roguery Bonus / Point", 0.0f, 0.005f, "0.0000", Order = 3, RequireRestart = false,
            HintText = "{=BMS_CapRogueryBonus_H}Additional hourly escape chance per Roguery skill point.")]
        [SettingPropertyGroup("{=BMSG_Captivity}9. Captivity & Surrender")]
        public float CaptivityRogueryBonusPerPoint { get; set; } = 0.00015f;

        [SettingPropertyFloatingInteger("{=BMS_CapMaxChance}Max Escape Chance / Hour", 0.01f, 0.50f, "0.00", Order = 4, RequireRestart = false,
            HintText = "{=BMS_CapMaxChance_H}Upper cap for hourly escape chance.")]
        [SettingPropertyGroup("{=BMSG_Captivity}9. Captivity & Surrender")]
        public float CaptivityMaxChancePerHour { get; set; } = 0.20f;

        [SettingPropertyInteger("{=BMS_CapRampDays}Ramp Duration (Days)", 1, 30, "0 days", Order = 5, RequireRestart = false,
            HintText = "{=BMS_CapRampDays_H}Days after lock ends to reach full escape chance.")]
        [SettingPropertyGroup("{=BMSG_Captivity}9. Captivity & Surrender")]
        public int CaptivityRampDays { get; set; } = 5;

        [SettingPropertyBool("{=BMS_ML}Enable Machine Learning", Order = 0, RequireRestart = false,
            HintText = "{=BMS_ML_H}Enables Q-Learning AI for militias. They learn from battle outcomes over time.")]
        [SettingPropertyGroup("{=BMSG_ML}7. Machine Learning")]
        public bool EnableMachineLearning { get; set; } = true;

        [SettingPropertyBool("{=BMS_MLData}Collect Battle Data", Order = 1, RequireRestart = false,
            HintText = "{=BMS_MLData_H}Logs battle samples to CSV for offline ML analysis.")]
        [SettingPropertyGroup("{=BMSG_ML}7. Machine Learning")]
        public bool EnableMLDataCollection { get; set; } = true;

        [SettingPropertyBool("{=BMS_MLObs}Observation Mode (No Training)", Order = 2, RequireRestart = false,
            HintText = "{=BMS_MLObs_H}Records ML data without updating Q-values. Useful for establishing baselines.")]
        [SettingPropertyGroup("{=BMSG_ML}7. Machine Learning")]
        public bool MLObservationMode { get; set; } = false;

        [SettingPropertyFloatingInteger("{=BMS_Underdog15}Underdog Bonus (1.5x enemy)", 1.0f, 5.0f, "0.0x", Order = 0, RequireRestart = false,
            HintText = "{=BMS_Underdog15_H}Q-Learning reward multiplier when militia beats an enemy 1.5x its size.")]
        [SettingPropertyGroup("{=BMSG_AdvML}7. Machine Learning/Advanced ML", GroupOrder = 1)]
        public float UnderdogBonus15x { get; set; } = 1.5f;

        [SettingPropertyFloatingInteger("{=BMS_Underdog20}Underdog Bonus (2.0x enemy)", 1.0f, 5.0f, "0.0x", Order = 1, RequireRestart = false,
            HintText = "{=BMS_Underdog20_H}Q-Learning reward multiplier when militia beats an enemy 2.0x its size.")]
        [SettingPropertyGroup("{=BMSG_AdvML}7. Machine Learning/Advanced ML")]
        public float UnderdogBonus20x { get; set; } = 2.0f;

        [SettingPropertyFloatingInteger("{=BMS_Underdog30}Underdog Bonus (3.0x enemy)", 1.0f, 5.0f, "0.0x", Order = 2, RequireRestart = false,
            HintText = "{=BMS_Underdog30_H}Q-Learning reward multiplier when militia beats an enemy 3.0x its size.")]
        [SettingPropertyGroup("{=BMSG_AdvML}7. Machine Learning/Advanced ML")]
        public float UnderdogBonus30x { get; set; } = 3.0f;

        [SettingPropertyInteger("{=BMS_MLLogN}Log Every N Battles", 1, 50, "0", Order = 3, RequireRestart = false,
            HintText = "{=BMS_MLLogN_H}Sampling interval for battle data logging. 1 = log every battle.")]
        [SettingPropertyGroup("{=BMSG_AdvML}7. Machine Learning/Advanced ML")]
        public int MLLogEveryNBattles { get; set; } = 1;

        [SettingPropertyBool("{=BMS_AutoPrune}Auto-Prune Q-Table", Order = 4, RequireRestart = false,
            HintText = "{=BMS_AutoPrune_H}Automatically removes low-value entries from the Q-Table to conserve memory.")]
        [SettingPropertyGroup("{=BMSG_AdvML}7. Machine Learning/Advanced ML")]
        // FIX-2: AutoPruneQTable default true — aksi halde QTable sınırsız büyür.
        public bool AutoPruneQTable { get; set; } = true;

        // FIX-2: Default 1000→3238 (log verisinden ölçülen gerçek ihtiyaç).
        // Üst sınır 5000→10000: güçlü donanımlarda daha zengin Q-Table desteklenir.
        // Ayar MCM'de kullanıcı tarafından hâlâ değiştirilebilir.
        [SettingPropertyInteger("{=BMS_MaxQTable}Max Q-Table Size", 100, 10000, "0 entries", Order = 5, RequireRestart = false,
            HintText = "{=BMS_MaxQTable_H}Maximum state-action pairs stored in the AI's memory. Higher values allow richer learning but use more RAM. (Default: 3238)")]
        [SettingPropertyGroup("{=BMSG_AdvML}7. Machine Learning/Advanced ML")]
        public int MaxQTableSize { get; set; } = 3238;

        [SettingPropertyBool("{=BMS_TestMode}Test Mode", Order = 0, RequireRestart = false,
            HintText = "{=BMS_TestMode_H}Writes detailed diagnostics to the game log. For debugging only — expect log spam.")]
        [SettingPropertyGroup("{=BMSG_Debug}8. Technical & Debug")]
        public bool TestingMode { get; set; } = false;

        [SettingPropertyBool("{=BMS_DevMode}Developer Data Collection", Order = 2, RequireRestart = false,
            HintText = "{=BMS_DevMode_H}DEVELOPER ONLY: Collects all test data to Documents/BanditMilitias_Dev/. Zero overhead when disabled. Disable before distributing the mod.")]
        [SettingPropertyGroup("{=BMSG_Debug}8. Technical & Debug")]
        // FIX-2: Shipping default false — DevMode=true olursa DevDataCollector saatlik 9.66ms harcar.
        public bool DevMode { get; set; } = false;

        [SettingPropertyInteger("{=BMS_AutoReport}Auto Diagnostic Interval (Hours)", 0, 168, "0 h", Order = 3, RequireRestart = false,
            HintText = "{=BMS_AutoReport_H}Automatically generates and saves a Full Sim Report every X in-game hours. 0 = Disabled.")]
        [SettingPropertyGroup("{=BMSG_Debug}8. Technical & Debug")]
        public int AutoDiagnosticReportInterval { get; set; } = 24;

        [SettingPropertyBool("{=BMS_Uninstall}UNINSTALL MODE (DANGER)", Order = 4, RequireRestart = false,
            HintText = "{=BMS_Uninstall_H}DANGER: Removes ALL mod parties for safe uninstallation. THIS CANNOT BE UNDONE.")]
        [SettingPropertyGroup("{=BMSG_Debug}8. Technical & Debug")]
        public bool UninstallMode { get; set; } = false;

        [SettingPropertyBool("{=BMS_AggressiveCleanup}Aggressive Cleanup", Order = 0, RequireRestart = false,
            HintText = "{=BMS_AggressiveCleanup_H}Removes idle or weak parties during cleanup passes. Enable only if experiencing lag.")]
        [SettingPropertyGroup("{=BMSG_AdvTech}8. Technical & Debug/Advanced Technical", GroupOrder = 1)]
        public bool EnableAggressiveCleanup { get; set; } = false;

        [SettingPropertyBool("{=BMS_FileLogging}File Logging", Order = 1, RequireRestart = false,
            HintText = "{=BMS_FileLogging_H}Writes all mod events to logs/BanditMilitias.log.")]
        [SettingPropertyGroup("{=BMSG_AdvTech}8. Technical & Debug/Advanced Technical")]
        public bool EnableFileLogging { get; set; } = false;

        [SettingPropertyBool("{=BMS_AIDecisionLog}AI Decision Logging", Order = 2, RequireRestart = false,
            HintText = "{=BMS_AIDecisionLog_H}Verbose logging of AI reasoning and decision outcomes.")]
        [SettingPropertyGroup("{=BMSG_AdvTech}8. Technical & Debug/Advanced Technical")]
        public bool EnableAIDecisionLogging { get; set; } = false;

        [SettingPropertyBool("{=BMS_ShowDebug}Show Debug Messages", Order = 3, RequireRestart = false,
            HintText = "{=BMS_ShowDebug_H}Displays [DEBUG] popups in-game for ongoing mod activity.")]
        [SettingPropertyGroup("{=BMSG_AdvTech}8. Technical & Debug/Advanced Technical")]
        public bool ShowTestMessages { get; set; } = false;

        [SettingPropertyBool("{=BMS_AIScheduler}AI Scheduler", Order = 4, RequireRestart = false,
            HintText = "{=BMS_AIScheduler_H}Spreads AI processing across ticks to avoid frame spikes.")]
        [SettingPropertyGroup("{=BMSG_AdvTech}8. Technical & Debug/Advanced Technical")]
        public bool EnableAIScheduler { get; set; } = true;

        [SettingPropertyInteger("{=BMS_MaxAITasks}Max AI Tasks Per Tick", 1, 100, "0 tasks", Order = 5, RequireRestart = false,
            HintText = "{=BMS_MaxAITasks_H}CPU budget for the AI Scheduler. Lower = smoother FPS, slower AI updates.")]
        [SettingPropertyGroup("{=BMSG_AdvTech}8. Technical & Debug/Advanced Technical")]
        public int MaxAITasksPerTick { get; set; } = 100;

        [SettingPropertyBool("{=BMS_AsyncAI}Allow Async AI (Experimental)", Order = 6, RequireRestart = false,
            HintText = "{=BMS_AsyncAI_H}Experimental: offloads non-critical AI tasks to background threads. May cause rare race conditions.")]
        [SettingPropertyGroup("{=BMSG_AdvTech}8. Technical & Debug/Advanced Technical")]
        public bool AllowAsyncAIProcessing { get; set; } = false;

        [SettingPropertyBool("{=BMS_SpatialAI}Spatial Awareness", Order = 7, RequireRestart = false,
            HintText = "{=BMS_SpatialAI_H}AI factors in territory control, battle history, and proximity when making decisions.")]
        [SettingPropertyGroup("{=BMSG_AdvTech}8. Technical & Debug/Advanced Technical")]
        public bool EnableSpatialAwareness { get; set; } = true;

        [SettingPropertyInteger("{=BMS_AIRecruit}AI Recruit Count", 1, 50, "0 men", Order = 8, RequireRestart = false,
            HintText = "{=BMS_AIRecruit_H}Troops recruited by AI per reinforcement cycle.")]
        [SettingPropertyGroup("{=BMSG_AdvTech}8. Technical & Debug/Advanced Technical")]
        public int RecruitCount { get; set; } = 5;

        [SettingPropertyInteger("{=BMS_UpgradeXp}Upgrade XP Requirement", 100, 5000, "0 XP", Order = 9, RequireRestart = false,
            HintText = "{=BMS_UpgradeXp_H}XP accumulated before a militia can upgrade its troops.")]
        [SettingPropertyGroup("{=BMSG_AdvTech}8. Technical & Debug/Advanced Technical")]
        public int UpgradeXp { get; set; } = 1000;

        [SettingPropertyInteger("{=BMS_WeakThreshold}Weak Party Threshold", 5, 100, "0 men", Order = 9, RequireRestart = false,
            HintText = "{=BMS_WeakThreshold_H}Troop count below which AI considers a party 'weak'. Used by ML state machine.")]
        [SettingPropertyGroup("{=BMSG_ML}7. Machine Learning")]
        public int WeakThreshold { get; set; } = 20;

        [SettingPropertyInteger("{=BMS_PoorThreshold}Poor Party Threshold", 50, 2000, "0 gold", Order = 9, RequireRestart = false,
            HintText = "{=BMS_PoorThreshold_H}Gold amount below which AI considers itself 'poor'. Used by ML state machine.")]
        [SettingPropertyGroup("{=BMSG_ML}7. Machine Learning")]
        public int PoorThreshold { get; set; } = 300;

        [SettingPropertyInteger("{=BMS_StrongThreshold}Strong Party Threshold", 10, 200, "0 men", Order = 10, RequireRestart = false,
            HintText = "{=BMS_StrongThreshold_H}Troop count at which AI considers a party 'strong' for strategic planning.")]
        [SettingPropertyGroup("{=BMSG_AdvTech}8. Technical & Debug/Advanced Technical")]
        public int StrongThreshold { get; set; } = 40;

        [SettingPropertyInteger("{=BMS_WealthyThreshold}Wealthy Threshold", 100, 5000, "0 gold", Order = 11, RequireRestart = false,
            HintText = "{=BMS_WealthyThreshold_H}Gold amount at which AI considers itself 'wealthy' and pursues aggressive expansion.")]
        [SettingPropertyGroup("{=BMSG_AdvTech}8. Technical & Debug/Advanced Technical")]
        public int WealthyThreshold { get; set; } = 1000;

        public int WarlordFallbackDays { get; set; } = 100;

        public float SpawnCooldownHours => HideoutCooldownDays * 24f;

        static Settings() { }

        public void ValidateAndClampSettings()
        {

            HideoutCooldownDays = (int)MathF.Clamp(HideoutCooldownDays, 1, 30);
            MaxTotalMilitias = (int)MathF.Clamp(MaxTotalMilitias, 10, 500);
            GlobalPerformancePartyLimit = (int)MathF.Clamp(GlobalPerformancePartyLimit, 1000, 5000);
            ActivationDelay = (int)MathF.Clamp(ActivationDelay, 2, 30);
            HideoutFormationCooldown = (int)MathF.Clamp(HideoutFormationCooldown, 1, 90);
            MaxPatrolDensityForHideout = MathF.Clamp(MaxPatrolDensityForHideout, 0f, 5f);

            MaxWarlordCount = (int)MathF.Clamp(MaxWarlordCount, 0, 100);
            WarlordMinTroops = (int)MathF.Clamp(WarlordMinTroops, 5, 500);
            WarlordMinDaysAlive = (int)MathF.Clamp(WarlordMinDaysAlive, 7, 120);
            WarlordMinBattlesWon = (int)MathF.Clamp(WarlordMinBattlesWon, 1, 20);
            FamousBanditFallbackDays = (int)MathF.Clamp(FamousBanditFallbackDays, 30, 250);
            WarlordFallbackDays = (int)MathF.Clamp(WarlordFallbackDays, 60, 500);
            MaxWarlordCount = (int)MathF.Clamp(MaxWarlordCount, 0, 50);
            AdaptiveDoctrineLearningRate = MathF.Clamp(AdaptiveDoctrineLearningRate, 0.05f, 1.00f);
            AdaptiveDoctrineSwitchCooldownHours = (int)MathF.Clamp(AdaptiveDoctrineSwitchCooldownHours, 6, 120);
            AdaptiveDoctrineAggressionBias = MathF.Clamp(AdaptiveDoctrineAggressionBias, -0.50f, 0.50f);

            PoliticsAllianceThreshold = (int)MathF.Clamp(PoliticsAllianceThreshold, -100, 100);
            PoliticsRivalryThreshold = (int)MathF.Clamp(PoliticsRivalryThreshold, -100, 100);
            PoliticsBetrayalThreshold = (int)MathF.Clamp(PoliticsBetrayalThreshold, -100, 100);
            PoliticsDailyDrift = MathF.Clamp(PoliticsDailyDrift, 0f, 0.20f);
            BribeCostPerMan = (int)MathF.Clamp(BribeCostPerMan, 10, 500);

            GoldRewardMultiplier = MathF.Clamp(GoldRewardMultiplier, 0.5f, 10.0f);
            RenownRewardMultiplier = MathF.Clamp(RenownRewardMultiplier, 0.5f, 10.0f);
            WarlordRewardMultiplier = MathF.Clamp(WarlordRewardMultiplier, 1.0f, 10.0f);
            HardBattleBonusMultiplier = MathF.Clamp(HardBattleBonusMultiplier, 0.5f, 10.0f);
            BanditSkillBoost = (int)MathF.Clamp(BanditSkillBoost, 0, 100);
            EquipmentQuality = (int)MathF.Clamp(EquipmentQuality, 0, 5);

            BanditDensityMultiplier = MathF.Clamp(BanditDensityMultiplier, 0.1f, 5.0f);
            BanditSizeMultiplier = MathF.Clamp(BanditSizeMultiplier, 0.1f, 5.0f);
            WarSpawnMultiplier = MathF.Clamp(WarSpawnMultiplier, 1.0f, 5.0f);
            TradeSpawnMultiplier = MathF.Clamp(TradeSpawnMultiplier, 1.0f, 5.0f);
            NightAmbushBonus = MathF.Clamp(NightAmbushBonus, 0f, 100f);
            TerrainAdvantageBonus = MathF.Clamp(TerrainAdvantageBonus, 0f, 100f);
            AggressivePatrolThreshold = MathF.Clamp(AggressivePatrolThreshold, 0.1f, 10.0f);

            RaidLootMultiplier = MathF.Clamp(RaidLootMultiplier, 0.5f, 10.0f);

            CaptivityEscapeLockHours = (int)MathF.Clamp(CaptivityEscapeLockHours, 0, 240);
            CaptivityMaxDays = (int)MathF.Clamp(CaptivityMaxDays, 1, 30);
            CaptivityBaseChancePerHour = MathF.Clamp(CaptivityBaseChancePerHour, 0.0001f, 0.05f);
            CaptivityRogueryBonusPerPoint = MathF.Clamp(CaptivityRogueryBonusPerPoint, 0f, 0.005f);
            CaptivityMaxChancePerHour = MathF.Clamp(CaptivityMaxChancePerHour, 0.01f, 0.50f);
            CaptivityRampDays = (int)MathF.Clamp(CaptivityRampDays, 1, 30);

            if (CaptivityMaxChancePerHour < CaptivityBaseChancePerHour)
                CaptivityMaxChancePerHour = CaptivityBaseChancePerHour;

            int maxLockHours = CaptivityMaxDays * 24;
            if (CaptivityEscapeLockHours > maxLockHours)
                CaptivityEscapeLockHours = maxLockHours;

            MLLogEveryNBattles = (int)MathF.Clamp(MLLogEveryNBattles, 1, 50);
            MaxQTableSize = (int)MathF.Clamp(MaxQTableSize, 100, 10000);
            UnderdogBonus15x = MathF.Clamp(UnderdogBonus15x, 1.0f, 4.0f);
            UnderdogBonus20x = MathF.Clamp(UnderdogBonus20x, 1.0f, 8.0f);
            UnderdogBonus30x = MathF.Clamp(UnderdogBonus30x, 1.0f, 16.0f);

            RecruitCount = (int)MathF.Clamp(RecruitCount, 1, 50);
            UpgradeXp = (int)MathF.Clamp(UpgradeXp, 100, 5000);
            StrongThreshold = (int)MathF.Clamp(StrongThreshold, 10, 200);
            WealthyThreshold = (int)MathF.Clamp(WealthyThreshold, 100, 5000);
            MaxAITasksPerTick = (int)MathF.Clamp(MaxAITasksPerTick, 1, 100);

            if (WarlordFallbackDays < FamousBanditFallbackDays)
                WarlordFallbackDays = FamousBanditFallbackDays;

            if (PoliticsAllianceThreshold <= PoliticsRivalryThreshold)
                PoliticsAllianceThreshold = (int)MathF.Clamp(PoliticsRivalryThreshold + 5, -100, 100);
        }

        public void ResetToDefaults()
        {
            // Spawning
            MilitiaSpawn = true;
            MaxTotalMilitias = 200;
            GlobalPerformancePartyLimit = 3000;
            HideoutCooldownDays = 1;
            ActivationDelay = 2;
            EnableSeaRaiders = true;
            EnableCustomBanditNames = true;
            EnableDynamicHideouts = true;
            EnableHardcoreDynamicHideouts = false;
            MinPartiesForHideout = 4;
            HideoutFormationCooldown = 30;
            MaxPatrolDensityForHideout = 1.0f;

            // Warlords & AI
            EnableWarlords = true;
            EnableCustomAI = true;
            EnableWarlordRegeneration = true;
            EnableWarlordTactics = true;
            WarlordMinTroops = 15;
            WarlordMinDaysAlive = 30;
            WarlordMinBattlesWon = 3;
            FamousBanditFallbackDays = 60;
            MaxWarlordCount = 5;
            AlwaysSpawnCaptain = true;
            EnableAdaptiveAIDoctrine = true;
            AdaptiveDoctrineLearningRate = 0.30f;
            AdaptiveDoctrineSwitchCooldownHours = 36;
            AdaptiveDoctrineAggressionBias = 0.00f;

            // Politics
            EnableBanditPolitics = true;
            EnablePropaganda = true;
            EnableBlackMarket = true;
            BribeCostPerMan = 50;
            PoliticsAllianceThreshold = 40;
            PoliticsRivalryThreshold = -35;
            PoliticsBetrayalThreshold = -55;
            PoliticsDailyDrift = 0.04f;

            // Combat & Loot
            EnhancedBandits = true;
            BanditSkillBoost = 30;
            EquipmentQuality = 2;
            EnableHeroicFeats = true;
            GoldRewardMultiplier = 1.5f;
            RenownRewardMultiplier = 1.5f;
            WarlordRewardMultiplier = 2.0f;
            HardBattleBonusMultiplier = 2.0f;
            HeroItemRewards = true;

            // World
            BanditDensityMultiplier = 1.5f;
            BanditSizeMultiplier = 1.2f;
            WarSpawnMultiplier = 1.5f;
            TradeSpawnMultiplier = 1.3f;
            NightAmbushBonus = 15f;
            TerrainAdvantageBonus = 10f;
            AggressivePatrolThreshold = 1.5f;

            // Powers
            EnableFearSystem = true;
            EnableWarlordLegacy = true;
            EnableCrisisEvents = true;
            EnableLegitimacySystem = true;
            EnableBountySystem = true;
            EnableMilitiaRaids = true;
            RaidLootMultiplier = 1.0f;
            EnableJailbreakSystem = true;
            EnableWarlordLogistics = true;
            AutoDiagnosticReportInterval = 24;

            // ML
            EnableMachineLearning = true;
            EnableMLDataCollection = true;
            MLObservationMode = false;
            UnderdogBonus15x = 1.5f;
            UnderdogBonus20x = 2.0f;
            UnderdogBonus30x = 3.0f;
            MLLogEveryNBattles = 1;
            AutoPruneQTable = true;
            MaxQTableSize = 3238;
            UpgradeXp = 1000;
            RecruitCount = 5;
            WeakThreshold = 20;
            PoorThreshold = 300;
            StrongThreshold = 40;
            WealthyThreshold = 1000;

            AutoDiagnosticReportInterval = (int)MathF.Clamp(AutoDiagnosticReportInterval, 0, 168);

            ValidateAndClampSettings();
        }

        public int ValidateAndClampSettingsWithDiagnostics(out string report)
        {
            int b_MaxTotalMilitias = MaxTotalMilitias;
            int b_WarlordMinDaysAlive = WarlordMinDaysAlive;
            int b_FamousBanditFallbackDays = FamousBanditFallbackDays;
            int b_WarlordFallbackDays = WarlordFallbackDays;
            int b_AllianceThreshold = PoliticsAllianceThreshold;
            int b_RivalryThreshold = PoliticsRivalryThreshold;
            int b_MLLogEveryNBattles = MLLogEveryNBattles;
            int b_MaxQTableSize = MaxQTableSize;

            ValidateAndClampSettings();

            var changes = new System.Collections.Generic.List<string>(8);
            if (b_MaxTotalMilitias != MaxTotalMilitias) changes.Add($"MaxMilitias: {b_MaxTotalMilitias}>{MaxTotalMilitias}");
            if (b_WarlordMinDaysAlive != WarlordMinDaysAlive) changes.Add($"MinDaysAlive: {b_WarlordMinDaysAlive}>{WarlordMinDaysAlive}");
            if (b_FamousBanditFallbackDays != FamousBanditFallbackDays) changes.Add($"FamousFallback: {b_FamousBanditFallbackDays}>{FamousBanditFallbackDays}");
            if (b_WarlordFallbackDays != WarlordFallbackDays) changes.Add($"WarlordFallback: {b_WarlordFallbackDays}>{WarlordFallbackDays}");
            if (b_AllianceThreshold != PoliticsAllianceThreshold) changes.Add($"AllianceThreshold: {b_AllianceThreshold}>{PoliticsAllianceThreshold}");
            if (b_RivalryThreshold != PoliticsRivalryThreshold) changes.Add($"RivalryThreshold: {b_RivalryThreshold}>{PoliticsRivalryThreshold}");
            if (b_MLLogEveryNBattles != MLLogEveryNBattles) changes.Add($"MLLogEveryN: {b_MLLogEveryNBattles}>{MLLogEveryNBattles}");
            if (b_MaxQTableSize != MaxQTableSize) changes.Add($"MaxQTableSize: {b_MaxQTableSize}>{MaxQTableSize}");

            if (changes.Count == 0)
            {
                report = "Settings validation: no corrections required.";
                return 0;
            }

            report = $"Settings auto-corrected ({changes.Count}): {string.Join(", ", changes)}";
            return changes.Count;
        }
    }


    // ── DynamicDifficulty ─────────────────────────────────────────
    /// <summary>
    /// Dinamik zorluk ayarlama sistemi
    /// Oyuncu gücüne göre otomatik spawn ayarları
    /// </summary>
    public static class DynamicDifficulty
    {
        /// <summary>
        /// Oyuncu gücüne göre spawn çarpanını hesapla
        /// </summary>
        public static float CalculateSpawnMultiplier()
        {
            var playerStrength = MobileParty.MainParty != null
                ? Infrastructure.CompatibilityLayer.GetTotalStrength(MobileParty.MainParty)
                : 100f;
            var avgMilitiaStrength = CalculateAverageMilitiaStrength();

            if (avgMilitiaStrength <= 0) return 1.0f;

            // Oyuncu güçlüyse daha güçlü militia spawn et
            if (playerStrength > avgMilitiaStrength * 2f)
                return 1.5f; // +50% güç

            // Oyuncu zayıfsa zayıf militia spawn et
            if (playerStrength < avgMilitiaStrength * 0.5f)
                return 0.7f; // -30% güç

            return 1.0f;
        }

        /// <summary>
        /// Oyuncu seviyesine ve dünya durumuna göre optimal militia sayısı
        /// </summary>
        public static int CalculateOptimalMilitiaCount()
        {
            if (Settings.Instance == null) return 30;

            var playerLevel = Hero.MainHero?.Level ?? 10;
            var settlementCount = Settlement.All?.Count(s => s.IsTown) ?? 0;
            var activeKingdoms = Kingdom.All?.Count(k => k?.IsEliminated == false) ?? 0;

            // Formül: 5 + (seviye/5) + (şehir_sayısı/3) + (krallık/2)
            var baseCount = 5 + (playerLevel / 5) + (settlementCount / 3) + (activeKingdoms / 2);

            // Min/max sınırları uygula (MathF.Clamp .NET Framework 4.7.2'de yok)
            if (baseCount < 10) baseCount = 10;
            if (baseCount > Settings.Instance.MaxTotalMilitias)
                baseCount = Settings.Instance.MaxTotalMilitias;

            return baseCount;
        }

        /// <summary>
        /// Günlük spawn şansını dinamik ayarla
        /// </summary>
        public static float CalculateAdjustedSpawnChance(float baseChance)
        {
            var currentCount = Infrastructure.ModuleManager.Instance?.GetMilitiaCount() ?? 0;
            var optimalCount = CalculateOptimalMilitiaCount();

            // Optimal sayının %80'i altındaysa spawn şansını artır
            if (currentCount < optimalCount * 0.8f)
            {
                return baseChance * 1.3f;
            }

            // Optimal sayının %120'si üzerindeyse spawn şansını azalt
            if (currentCount > optimalCount * 1.2f)
            {
                return baseChance * 0.5f;
            }

            return baseChance;
        }

        /// <summary>
        /// Militia güç çarpanını hesapla (troop sayısı ve kalitesi için)
        /// </summary>
        public static float CalculateMilitiaPowerMultiplier()
        {
            var playerStrength = MobileParty.MainParty != null
                ? Infrastructure.CompatibilityLayer.GetTotalStrength(MobileParty.MainParty)
                : 100f;
            var playerPartySize = MobileParty.MainParty?.MemberRoster?.TotalManCount ?? 50;

            float multiplier = Settings.Instance?.BanditSizeMultiplier ?? 1.0f;

            // Oyuncu partisi büyükse daha büyük militia'lar spawn et
            if (playerPartySize > 100)
                multiplier += 0.2f;
            if (playerPartySize > 200)
                multiplier += 0.3f;

            // Oyuncu güçlüyse daha kaliteli troop'lar
            if (playerStrength > 150)
                multiplier += 0.2f;

            // YENİ: Erken oyunda (ilk 20 gün) partiler kısıtlı olsun
            float elapsedDays = 0f;
            if (Campaign.Current != null)
            {
                var startTime = BanditMilitias.Infrastructure.CompatibilityLayer.GetCampaignStartTime();
                if (startTime.ToHours > 0.0) elapsedDays = (float)(CampaignTime.Now - startTime).ToDays;
                if (elapsedDays < 0f) elapsedDays = 0f;
            }
            if (elapsedDays < 20f)
            {
                return multiplier > 0.8f ? 0.8f : multiplier; // İlk 20 gün %80 parti gücü (%80 sayı/tier)
            }
            else if (elapsedDays < 50f)
            {
                return multiplier > 1.2f ? 1.2f : multiplier; // 20-50 gün arası normalize
            }

            // Max 2.0f
            return multiplier > 2.0f ? 2.0f : multiplier;
        }

        /// <summary>
        /// Ortalama militia gücünü hesapla
        /// </summary>
        private static float CalculateAverageMilitiaStrength()
        {
            try
            {
                var militias = Infrastructure.ModuleManager.Instance?.ActiveMilitias;
                if (militias == null || !militias.Any()) return 50f;

                return militias.Average(m => m != null
                    ? Infrastructure.CompatibilityLayer.GetTotalStrength(m)
                    : 50f);
            }
            catch
            {
                return 50f;
            }
        }

        /// <summary>
        /// Savaş durumuna göre spawn multiplier'ı
        /// </summary>
        public static float CalculateWarMultiplier(Settlement hideout)
        {
            if (Settings.Instance == null) return 1.0f;

            try
            {
                // FIX: Hideout'ların OwnerClan'ı bandit klanıdır → Kingdom yok.
                // Bunun yerine en yakın şehir/kale'nin krallığını bul.
                Kingdom? kingdom = hideout?.OwnerClan?.Kingdom;

                if (kingdom == null && hideout != null)
                {
                    var hideoutPos = Infrastructure.CompatibilityLayer.GetSettlementPosition(hideout);
                    if (hideoutPos.IsValid)
                    {
                        float bestDist = float.MaxValue;
                        foreach (var s in Settlement.All)
                        {
                            if (s == null || (!s.IsTown && !s.IsCastle)) continue;
                            var k = s.OwnerClan?.Kingdom;
                            if (k == null) continue;
                            var sPos = Infrastructure.CompatibilityLayer.GetSettlementPosition(s);
                            if (!sPos.IsValid) continue;
                            float dist = hideoutPos.DistanceSquared(sPos);
                            if (dist < bestDist)
                            {
                                bestDist = dist;
                                kingdom = k;
                            }
                        }
                    }
                }

                if (kingdom == null) return 1.0f;

                // Krallık savaşta mı?
                bool isAtWar = Kingdom.All != null && Kingdom.All.Any(k =>
                    k != null && k != kingdom && kingdom.IsAtWarWith(k));
                return isAtWar ? Settings.Instance.WarSpawnMultiplier : 1.0f;
            }
            catch
            {
                return 1.0f;
            }
        }

        /// <summary>
        /// Ticaret rotasına göre spawn multiplier'ı
        /// </summary>
        public static float CalculateTradeRouteMultiplier(Settlement hideout)
        {
            if (Settings.Instance == null) return 1.0f;

            try
            {
                // Etraftaki caravan sayısını kontrol et
                var hideoutPos = Infrastructure.CompatibilityLayer.GetSettlementPosition(hideout);
                if (!hideoutPos.IsValid) return 1.0f;

                var nearbyCaravans = MobileParty.All?
                    .Where(p => p?.IsCaravan == true)
                    .Count(p =>
                    {
                        var pos = Infrastructure.CompatibilityLayer.GetPartyPosition(p);
                        return pos.IsValid && pos.Distance(hideoutPos) < 50f;
                    }) ?? 0;

                if (nearbyCaravans >= 3)
                    return Settings.Instance.TradeSpawnMultiplier;

                return 1.0f;
            }
            catch
            {
                return 1.0f;
            }
        }
    }
}
