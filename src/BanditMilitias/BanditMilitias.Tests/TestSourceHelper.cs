using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BanditMilitias.Tests
{
    internal static class TestSourceHelper
    {
        public static string ProjectRoot { get; } = FindProjectRoot();

        public static string ReadProjectFile(string relativePath)
        {
            string fullPath = GetProjectPath(relativePath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"Expected source file not found: {fullPath}");
            }

            return File.ReadAllText(fullPath);
        }

        public static string ReadProjectFile(params string[] relativeParts)
        {
            if (relativeParts == null || relativeParts.Length == 0)
            {
                throw new ArgumentException("At least one relative path segment is required.", nameof(relativeParts));
            }

            string fullPath = Path.Combine(new[] { ProjectRoot }.Concat(relativeParts).ToArray());
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"Expected source file not found: {fullPath}");
            }

            return File.ReadAllText(fullPath);
        }

        public static IEnumerable<(string RelativePath, string Content)> EnumerateSourceFiles(bool includeTests = false)
        {
            if (!Directory.Exists(ProjectRoot))
            {
                yield break;
            }

            foreach (string file in Directory.GetFiles(ProjectRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}.vs{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                if (!includeTests && file.Contains($"{Path.DirectorySeparatorChar}BanditMilitias.Tests{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                string relativePath = file.Replace(ProjectRoot, string.Empty).TrimStart(Path.DirectorySeparatorChar);
                string content;
                try
                {
                    content = File.ReadAllText(file);
                }
                catch
                {
                    continue;
                }

                yield return (relativePath, content);
            }
        }

        public static string AllSourceCode(bool includeTests = false)
            => string.Concat(EnumerateSourceFiles(includeTests).Select(x => x.Content));

        public static string ResolveGameRoot()
        {
            string[] candidates =
            {
                Environment.GetEnvironmentVariable("BANNERLORD_GAME_DIR") ?? string.Empty,
                @"C:\Program Files\Epic Games\MountAndBlade2",
                @"C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord",
            };

            foreach (string candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                if (Directory.Exists(Path.Combine(candidate, "Modules")))
                {
                    return candidate;
                }
            }

            throw new DirectoryNotFoundException("Bannerlord oyun dizini bulunamadi.");
        }

        private static string GetProjectPath(string relativePath)
            => Path.Combine(ProjectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

        private static string FindProjectRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "BanditMilitias.csproj")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate project root containing BanditMilitias.csproj.");
        }
    }
}
