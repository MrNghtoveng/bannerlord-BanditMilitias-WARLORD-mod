using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace BanditMilitias.Tests
{
    /// <summary>
    /// Kayit defteri denetim testleri.
    /// TaleWorlds DLL'leri olmadan calisir; kaynak dosyalari okuyarak statik analiz yapar.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class RegistryAuditTests
    {
        public TestContext TestContext { get; set; } = null!;

        private IEnumerable<(string rel, string content)> GetSourceFiles()
            => TestSourceHelper.EnumerateSourceFiles().Select(x => (x.RelativePath, x.Content));

        private string AllSourceCode()
            => TestSourceHelper.AllSourceCode();

        [TestMethod]
        public void All_Module_Classes_Must_Be_Registered()
        {
            var moduleClasses = new HashSet<string>();
            foreach (var (_, content) in GetSourceFiles())
            {
                foreach (Match match in Regex.Matches(
                    content,
                    @"public\s+(?:sealed\s+)?(?:partial\s+)?class\s+(\w+)\s*:[^{]*MilitiaModuleBase"))
                {
                    moduleClasses.Add(match.Groups[1].Value);
                }
            }

            string submoduleContent = GetSourceFiles()
                .FirstOrDefault(x => x.rel.EndsWith("SubModule.cs", System.StringComparison.OrdinalIgnoreCase)).content ?? string.Empty;

            var registered = new HashSet<string>(
                Regex.Matches(
                        submoduleContent,
                        @"RegisterSafe\(\s*\(\)\s*=>\s*(?:new\s+)?(?:[\w]+\.)+(\w+)(?:\.Instance|\(\))")
                    .Cast<Match>()
                    .Select(match => match.Groups[1].Value));

            var ghosts = moduleClasses.Except(registered).OrderBy(x => x).ToList();

            TestContext.WriteLine($"Bulunan modul: {moduleClasses.Count}");
            TestContext.WriteLine($"Kayitli modul: {registered.Count}");
            foreach (string ghost in ghosts)
            {
                TestContext.WriteLine($"Ghost module: {ghost}");
            }

            Assert.AreEqual(0, ghosts.Count, $"Ghost modules: {string.Join(", ", ghosts)}");
        }

        [TestMethod]
        public void All_Events_Must_Have_Publisher()
        {
            string allCode = AllSourceCode();

            var eventClasses = Regex.Matches(
                    allCode,
                    @"public\s+class\s+(\w+Event)\s*:\s*(?:IPoolableEvent|IGameEvent)")
                .Cast<Match>()
                .Select(match => match.Groups[1].Value)
                .ToHashSet();

            var dead = new List<string>();
            foreach (string evt in eventClasses.OrderBy(x => x))
            {
                bool hasPublish =
                    Regex.IsMatch(allCode, $@"Get<(?:[\w]+\.)*{Regex.Escape(evt)}>", RegexOptions.Multiline) ||
                    Regex.IsMatch(allCode, $@"new\s+(?:[\w]+\.)*{Regex.Escape(evt)}\b") ||
                    Regex.IsMatch(allCode, $@"Fire\w*{Regex.Escape(evt[..^5])}");

                if (!hasPublish)
                {
                    dead.Add(evt);
                }
            }

            foreach (string evt in dead)
            {
                TestContext.WriteLine($"Missing publisher: {evt}");
            }

            Assert.AreEqual(0, dead.Count, $"Events without publisher: {string.Join(", ", dead)}");
        }

        [TestMethod]
        public void All_Events_Must_Have_Subscriber()
        {
            string allCode = AllSourceCode();

            var eventClasses = Regex.Matches(
                    allCode,
                    @"public\s+class\s+(\w+Event)\s*:\s*(?:IPoolableEvent|IGameEvent)")
                .Cast<Match>()
                .Select(match => match.Groups[1].Value)
                .ToHashSet();

            var noSubscribers = new List<string>();
            foreach (string evt in eventClasses.OrderBy(x => x))
            {
                bool hasSubscriber = Regex.IsMatch(allCode, $@"Subscribe<(?:[\w]+\.)*{Regex.Escape(evt)}>");
                if (!hasSubscriber)
                {
                    noSubscribers.Add(evt);
                }
            }

            foreach (string evt in noSubscribers)
            {
                TestContext.WriteLine($"Missing subscriber: {evt}");
            }

            Assert.AreEqual(0, noSubscribers.Count, $"Events without subscriber: {string.Join(", ", noSubscribers)}");
        }

        [TestMethod]
        public void Every_Subscribe_Must_Have_Unsubscribe()
        {
            string allCode = AllSourceCode();

            var subscribed = Regex.Matches(allCode, @"Subscribe<(?:[\w]+\.)*(\w+Event)>")
                .Cast<Match>()
                .Select(match => match.Groups[1].Value)
                .ToList();

            var unsubscribed = Regex.Matches(allCode, @"Unsubscribe<(?:[\w]+\.)*(\w+Event)>")
                .Cast<Match>()
                .Select(match => match.Groups[1].Value)
                .ToList();

            var subscribeCounts = subscribed.GroupBy(x => x).ToDictionary(group => group.Key, group => group.Count());
            var unsubscribeCounts = unsubscribed.GroupBy(x => x).ToDictionary(group => group.Key, group => group.Count());

            var leaks = new List<string>();
            foreach (KeyValuePair<string, int> entry in subscribeCounts)
            {
                int unsubscribeCount = unsubscribeCounts.TryGetValue(entry.Key, out int value) ? value : 0;
                if (entry.Value != unsubscribeCount)
                {
                    leaks.Add($"{entry.Key} (Subscribe={entry.Value}, Unsubscribe={unsubscribeCount})");
                }
            }

            foreach (string leak in leaks)
            {
                TestContext.WriteLine($"Event leak: {leak}");
            }

            Assert.AreEqual(0, leaks.Count, $"Event leaks: {string.Join(", ", leaks)}");
        }

        [TestMethod]
        public void SyncData_Keys_Must_Be_Unique_Across_Files()
        {
            var keyToFiles = new Dictionary<string, List<string>>();

            foreach (var (rel, content) in GetSourceFiles())
            {
                foreach (Match match in Regex.Matches(content, @"dataStore\.SyncData\(""([^""]+)"""))
                {
                    string key = match.Groups[1].Value;
                    if (!keyToFiles.ContainsKey(key))
                    {
                        keyToFiles[key] = new List<string>();
                    }

                    keyToFiles[key].Add(rel);
                }
            }

            var clashes = keyToFiles
                .Where(pair => pair.Value.Distinct().Count() > 1)
                .ToList();

            foreach (KeyValuePair<string, List<string>> clash in clashes)
            {
                TestContext.WriteLine($"SyncData clash: {clash.Key}");
                foreach (string file in clash.Value.Distinct())
                {
                    TestContext.WriteLine($"  {file}");
                }
            }

            Assert.AreEqual(0, clashes.Count, $"SyncData clashes: {string.Join(", ", clashes.Select(x => x.Key))}");
        }

        [TestMethod]
        public void SaveableField_IDs_Must_Be_Unique_Within_Class()
        {
            var clashes = new List<string>();

            foreach (var (rel, content) in GetSourceFiles())
            {
                var classMatches = Regex.Matches(
                    content,
                    @"class\s+\w+[^{]*\{((?:[^{}]|\{[^{}]*\})*)\}",
                    RegexOptions.Singleline);

                foreach (Match cls in classMatches)
                {
                    string body = cls.Groups[1].Value;
                    var ids = Regex.Matches(body, @"\[SaveableField\((\d+)\)\]")
                        .Cast<Match>()
                        .Select(match => match.Groups[1].Value)
                        .ToList();

                    var dupes = ids
                        .GroupBy(x => x)
                        .Where(group => group.Count() > 1)
                        .Select(group => group.Key);

                    foreach (string dupe in dupes)
                    {
                        clashes.Add($"{rel}: SaveableField({dupe}) tekrar ediyor");
                    }
                }
            }

            foreach (string clash in clashes)
            {
                TestContext.WriteLine(clash);
            }

            Assert.AreEqual(0, clashes.Count, $"Duplicate saveable IDs: {string.Join(", ", clashes)}");
        }

        [TestMethod]
        public void No_Old_Namespace_References()
        {
            string[] banned =
            {
                "Systems.Legitimacy",
                "Systems.Career",
                "Systems.Victory",
                "Domain.Warlords",
                "Domain.AI",
                "Core.Interfaces",
                "Core.Attributes",
            };

            var found = new List<string>();
            foreach (var (rel, content) in GetSourceFiles())
            {
                foreach (string ns in banned)
                {
                    string pattern = $"BanditMilitias.{ns}";
                    if (content.Contains(pattern))
                    {
                        var line = content.Split('\n')
                            .Select((text, index) => (text, index))
                            .First(match => match.text.Contains(pattern));
                        found.Add($"{rel}:{line.index + 1} -> {pattern}");
                    }
                }
            }

            foreach (string item in found)
            {
                TestContext.WriteLine(item);
            }

            Assert.AreEqual(0, found.Count, $"Legacy namespace references: {string.Join(", ", found)}");
        }
    }
}
