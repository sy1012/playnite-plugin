using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using RomM.Downloads;
using SharpCompress.Common;
using SharpCompress.Writers.Zip;
using Xunit;

namespace RomM.Tests
{
    public class ArchiveFormatsTests : IDisposable
    {
        private readonly string _dir;

        public ArchiveFormatsTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string P(params string[] parts) => Path.Combine(new[] { _dir }.Concat(parts).ToArray());

        private string Zip(string name, params string[] entries)
        {
            var path = P(name);
            using (var stream = File.Create(path))
            using (var writer = new ZipWriter(stream, new ZipWriterOptions(CompressionType.Deflate)))
            {
                foreach (var entry in entries)
                    writer.Write(entry, new MemoryStream(Encoding.UTF8.GetBytes("content of " + entry)), DateTime.UtcNow);
            }
            return path;
        }

        [Fact]
        public void Recognises_a_zip_and_7z_and_not_a_disc_image()
        {
            Assert.Equal(ArchiveFormats.Kind.Zip, ArchiveFormats.Detect(Zip("a.zip", "x.bin")));

            var sevenZip = P("a.7z");
            File.WriteAllBytes(sevenZip, new byte[] { (byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C, 0, 4 });
            Assert.Equal(ArchiveFormats.Kind.SevenZip, ArchiveFormats.Detect(sevenZip));

            var iso = P("game.iso");
            File.WriteAllBytes(iso, new byte[0x100]);
            Assert.Equal(ArchiveFormats.Kind.None, ArchiveFormats.Detect(iso));
        }

        // RomM serves a Switch game's folder as one zip: the base game at the root, its update and
        // DLC in subfolders. Every one of them has to land, in the same layout.
        [Fact]
        public void Unpacks_a_folder_download_with_its_update_and_dlc_subfolders()
        {
            var zip = Zip("Snipperclips.zip",
                "Snipperclips [01008E20047DC000][v0].nsp",
                "update/Snipperclips [01008E20047DC800][v131072].nsp",
                "dlc/Snipperclips [01008E20047DD001][v0].nsp");
            int lastDone = -1, lastTotal = -1;

            ArchiveFormats.ExtractZip(zip, P("install"), (d, t) => { lastDone = d; lastTotal = t; }, CancellationToken.None);

            Assert.True(File.Exists(P("install", "Snipperclips [01008E20047DC000][v0].nsp")));
            Assert.Equal("content of update/Snipperclips [01008E20047DC800][v131072].nsp",
                File.ReadAllText(P("install", "update", "Snipperclips [01008E20047DC800][v131072].nsp")));
            Assert.True(File.Exists(P("install", "dlc", "Snipperclips [01008E20047DD001][v0].nsp")));
            Assert.Equal(3, lastDone);
            Assert.Equal(3, lastTotal);
        }

        [Fact]
        public void A_zip_with_an_entry_escaping_the_folder_writes_nothing()
        {
            var zip = Zip("evil.zip", "good.bin", "../escaped.bin");

            Assert.Throws<ArgumentException>(() =>
                ArchiveFormats.ExtractZip(zip, P("install"), null, CancellationToken.None));

            Assert.False(File.Exists(P("escaped.bin")));
            Assert.False(File.Exists(P("install", "good.bin")));
        }
    }
}
