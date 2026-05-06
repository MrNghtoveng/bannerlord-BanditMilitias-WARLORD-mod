using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace BanditMilitias.Systems.Spawning
{
    public static class MilitiaNameGenerator
    {

        private static readonly Dictionary<string, string[]> _prefixes = new()
        {
            ["default"]          = new[] { "Rogue", "Outlaw", "Renegade", "Wild", "Dark", "Lost", "Black", "Red" },
            ["looters"]          = new[] { "Ragged", "Starving", "Desperate", "Broken", "Scavenger", "Stone", "Mud" },
            ["sea_raiders"]      = new[] { "Tide", "Storm", "Salt", "North", "Frost", "Iron", "Wave", "Mist" },
            ["mountain_bandits"] = new[] { "High", "Peak", "Cliff", "Rock", "Stone", "Cold", "Bear", "Wolf" },
            ["forest_bandits"]   = new[] { "Green", "Wood", "Leaf", "Shadow", "Moss", "Oak", "Thorn", "Fox" },
            ["desert_bandits"]   = new[] { "Sand", "Dust", "Sun", "Dune", "Heat", "Scorpion", "Viper", "Gold" },
            ["steppe_bandits"]   = new[] { "Wind", "Swift", "Horse", "Grass", "Sky", "Lightning", "Hawk", "Arrow" }
        };


        private static readonly Dictionary<string, string[]> _suffixes = new()
        {
            ["default"]          = new[] { "Band", "Gang", "Mob", "Crew", "Raiders", "Cutthroats", "Militia", "Pack" },
            ["looters"]          = new[] { "Scavengers", "Throwers", "Looters", "Rats", "Walkers", "Beggars", "Gatherers" },
            ["sea_raiders"]      = new[] { "Reavers", "Vikings", "Sailors", "Oarsmen", "Invaders", "Breakers", "Mariners" },
            ["mountain_bandits"] = new[] { "Hillmen", "Climbers", "Highlanders", "Stalkers", "Watchers", "Guardians" },
            ["forest_bandits"]   = new[] { "Archers", "Hunters", "Rangers", "Ambushers", "Striders", "Bowmen" },
            ["desert_bandits"]   = new[] { "Nomads", "Riders", "Assassins", "Dervishes", "Mirages", "Wanderers" },
            ["steppe_bandits"]   = new[] { "Horde", "Kheshigs", "Lancers", "Marauders", "Outriders", "Chasers" }
        };


        private static readonly string[] _formats = new[]
        {
            "{0} {1}",

            "{1} of {2}",

            "{0} {1} of {2}",

            "{2}'s {0} {1}",

            "{3}'s {1}",

            "{0} {1} — {2}"

        };


        private static readonly Dictionary<string, string> _regionLabelCache = new();


        public static TextObject GenerateName(Settlement hideout, Clan banditClan)
        {
            try
            {
                string key    = ResolveClanKey(banditClan);
                var prefixes  = _prefixes.ContainsKey(key) ? _prefixes[key] : _prefixes["default"];
                var suffixes  = _suffixes.ContainsKey(key) ? _suffixes[key] : _suffixes["default"];

                string prefix   = prefixes[MBRandom.RandomInt(prefixes.Length)];
                string suffix   = suffixes[MBRandom.RandomInt(suffixes.Length)];
                string region   = ResolveRegionLabel(hideout);
                string clanName = banditClan?.Name?.ToString() ?? "Bandit";

                string format    = PickFormat(region);
                string finalName = format
                    .Replace("{0}", prefix)
                    .Replace("{1}", suffix)
                    .Replace("{2}", region)
                    .Replace("{3}", clanName);

                return new TextObject(finalName);
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("NameGenerator", $"Fallback name used: {ex.Message}");
                return new TextObject($"{banditClan?.Name ?? new TextObject("Bandit")} Militia");
            }
        }


        private static string ResolveClanKey(Clan? banditClan)
        {
            string clanId = banditClan?.StringId?.ToLower() ?? "";
            if (clanId.Contains("sea_raider"))       return "sea_raiders";
            if (clanId.Contains("mountain_bandit"))  return "mountain_bandits";
            if (clanId.Contains("forest_bandit"))    return "forest_bandits";
            if (clanId.Contains("desert_bandit"))    return "desert_bandits";
            if (clanId.Contains("steppe_bandit"))    return "steppe_bandits";
            if (clanId.Contains("looter"))           return "looters";
            return "default";
        }

        private static string ResolveRegionLabel(Settlement? hideout)
        {
            if (hideout == null) return "";

            if (_regionLabelCache.TryGetValue(hideout.StringId, out string? cached))
                return cached;

            string label = "";
            try
            {


                var dataCache = BanditMilitias.Intelligence.AI.Components.StaticDataCache.Instance;
                if (dataCache != null)
                {
                    Vec2 pos = CompatibilityLayer.GetSettlementPosition(hideout);
                    Settlement? nearestTown = null;
                    float bestSq = float.MaxValue;

                    foreach (var town in dataCache.AllTowns)
                    {
                        if (town == null || !town.IsActive) continue;
                        float dSq = CompatibilityLayer.GetSettlementPosition(town).DistanceSquared(pos);
                        if (dSq < bestSq) { bestSq = dSq; nearestTown = town; }
                    }

                    if (nearestTown != null)
                    {
                        string townName = nearestTown.Name?.ToString() ?? "";
                        if (!string.IsNullOrWhiteSpace(townName) && !IsGenericHideoutWord(townName))
                            label = townName;
                    }
                }


                if (string.IsNullOrWhiteSpace(label))
                {
                    string cultureName = hideout.Culture?.Name?.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(cultureName) && !IsGenericHideoutWord(cultureName))
                        label = cultureName;
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("NameGenerator",
                    $"ResolveRegionLabel failed for {hideout.StringId}: {ex.Message}");
                label = "";
            }

            _regionLabelCache[hideout.StringId] = label;
            return label;
        }

        private static bool IsGenericHideoutWord(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            string lower = name.ToLowerInvariant().Trim();
            return lower == "hideout"
                || lower == "refugio"
                || lower == "repaire"
                || lower == "versteck"
                || lower.StartsWith("hideout");
        }

        private static string PickFormat(string regionLabel)
        {
            if (string.IsNullOrWhiteSpace(regionLabel))
                return _formats[0];


            float roll = MBRandom.RandomFloat;
            if (roll < 0.25f) return _formats[1];

            if (roll < 0.55f) return _formats[2];

            if (roll < 0.75f) return _formats[3];

            if (roll < 0.90f) return _formats[5];

            return _formats[0];

        }

        public static void ClearCache() => _regionLabelCache.Clear();
    }
}
