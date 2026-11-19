using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace RomM.Tests
{
    public class PluginDependencyTests
    {
        // Playnite ships SharpCompress 0.26 and its Toolbox packer leaves a plugin's own copy of a
        // DLL Playnite already has out of the .pext, so a SharpCompress call from the plugin throws
        // FileNotFoundException inside Playnite while every test here passes. The plugin uses
        // System.IO.Compression instead. (These tests may use SharpCompress: it stands in for the
        // other tools that write the zips the plugin reads.)
        [Fact]
        public void Plugin_code_does_not_use_sharpcompress()
        {
            var root = RepoRoot();
            var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !IsUnder(f, root, "RomM.Tests") && !IsUnder(f, root, "bin") && !IsUnder(f, root, "obj"))
                .Where(f => File.ReadAllLines(f).Any(l => l.TrimStart().StartsWith("using SharpCompress")))
                .Select(f => f.Substring(root.Length))
                .ToList();

            Assert.Empty(offenders);
            Assert.DoesNotContain("SharpCompress", File.ReadAllText(Path.Combine(root, "RomM.csproj")));
        }

        private static bool IsUnder(string file, string root, string folder)
        {
            return file.StartsWith(Path.Combine(root, folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static string RepoRoot([CallerFilePath] string self = "")
        {
            return Path.GetDirectoryName(Path.GetDirectoryName(self));
        }
    }
}
