using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace RomM.Tests
{
    public class SaveCodeDependencyTests
    {
        // Playnite ships SharpCompress 0.26 and its Toolbox packer leaves the plugin's own 0.36 out
        // of the .pext, so a SharpCompress call from save code throws FileNotFoundException inside
        // Playnite while every test here passes. Save code uses System.IO.Compression instead.
        [Fact]
        public void Save_code_does_not_use_sharpcompress()
        {
            var offenders = Directory.EnumerateFiles(SavesDir(), "*.cs", SearchOption.AllDirectories)
                .Where(f => File.ReadAllText(f).Contains("SharpCompress"))
                .Where(f => File.ReadAllLines(f).Any(l => l.TrimStart().StartsWith("using SharpCompress")))
                .Select(Path.GetFileName)
                .ToList();

            Assert.Empty(offenders);
        }

        private static string SavesDir([CallerFilePath] string self = "")
        {
            return Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(self)), "Saves");
        }
    }
}
