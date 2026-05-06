using BanditMilitias.Debug;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Localization;

namespace BanditMilitias.Systems.Spawning
{

    public static class MilitiaNameGenerator
    {
        private static readonly Dictionary<string, string[]> _prefixes = new()
        {

            ["default"] = new[] { "Rogue", "Outlaw", "Renegade", "Wild", "Dark", "Lost", "Black", "Red" },

            ["looters"] = new[] { "Ragged", "Starving", "Desperate", "Broken", "Scavenger", "Stone", "Mud" },
            ["sea_raiders"] = new[] { "Tide", "Storm", "Salt", "North", "Frost", "Iron", "Wave", "Mist" },
            ["mountain_bandits"] = new[] { "High", "Peak", "Cliff", "Rock", "Stone", "Cold", "Bear", "Wolf" },
            ["forest_bandits"] = new[] { "Green", "Wood", "Leaf", "Shadow", "Moss", "Oak", "Thorn", "Fox" },
            ["desert_bandits"] = new[] { "Sand", "Dust", "Sun", "Dune", "Heat", "Scorpion", "Viper", "Gold" },
            ["steppe_bandits"] = new[] { "Wind", "Swift", "Horse", "Grass", "Sky", "Lightning", "Hawk", "Arrow" }
        };

        private static readonly Dictionary<string, string[]> _suffixes = new()
        {
            ["default"] = new[] { "Band", "Gang", "Mob", "Crew", "Raiders", "Cutthroats", "Militia", "Pack" },

            ["looters"] = new[] { "Scavengers", "Throwers", "Looters", "Rats", "Walkers", "Beggars", "Gatherers" },
            ["sea_raiders"] = new[] { "Reavers", "Vikings", "Sailors", "Oarsmen", "Invaders", "Breakers", "Mariners" },
            ["mountain_bandits"] = new[] { "Hillmen", "Climbers", "Highlanders", "Stalkers", "Watchers", "Guardians" },
            ["forest_bandits"] = new[] { "Archers", "Hunters", "Rangers", "Ambushers", "Striders", "Bowmen" },
            ["desert_bandits"] = new[] { "Nomads", "Riders", "Assassins", "Dervishes", "Mirages", "Wanderers" },
            ["steppe_bandits"] = new[] { "Horde", "Kheshigs", "Lancers", "Marauders", "Outriders", "Chasers" }
        };

        private static readonly string[] _formats = new[]
        {
            "{0} {1}",
            "{1} of {2}",
            "{0} {1} of {2}",
            "{2}'s {0} {1}",
            "{3}'s {1}"
        };

        public static TextObject GenerateName(Settlement hideout, Clan banditClan)
        {
            try
            {
                string key = "default";
                string clanId = banditClan?.StringId?.ToLower() ?? "";

                if (clanId.Contains("sea_raider")) key = "sea_raiders";
                else if (clanId.Contains("mountain_bandit")) key = "mountain_bandits";
                else if (clanId.Contains("forest_bandit")) key = "forest_bandits";
                else if (clanId.Contains("desert_bandit")) key = "desert_bandits";
                else if (clanId.Contains("steppe_bandit")) key = "steppe_bandits";
                else if (clanId.Contains("looter")) key = "looters";

                var prefixes = _prefixes.ContainsKey(key) ? _prefixes[key] : _prefixes["default"];
                var suffixes = _suffixes.ContainsKey(key) ? _suffixes[key] : _suffixes["default"];

                string prefix = prefixes[MBRandom.RandomInt(prefixes.Length)];
                string suffix = suffixes[MBRandom.RandomInt(suffixes.Length)];

                string settlementName = hideout?.Name?.ToString() ?? "Wilderness";
                string clanName = banditClan?.Name?.ToString() ?? "Bandit";

                float roll = MBRandom.RandomFloat;
                string format;

                if (roll < 0.4f) format = _formats[0];
                else if (roll < 0.7f) format = _formats[1];
                else if (roll < 0.9f) format = _formats[2];
                else format = _formats[3];

                string finalName = format
                    .Replace("{0}", prefix)
                    .Replace("{1}", suffix)
                    .Replace("{2}", settlementName)
                    .Replace("{3}", clanName);

                return new TextObject(finalName);
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("NameGenerator", $"Fallback name used: {ex.Message}");

                return new TextObject($"{banditClan?.Name ?? new TextObject("Bandit")} Militia");
            }
        }
    }
}