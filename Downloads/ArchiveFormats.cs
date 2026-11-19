using RomM.Games;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace RomM.Downloads
{
    /// <summary>
    /// Recognises and unpacks downloaded archives with the framework's own System.IO.Compression.
    ///
    /// Not SharpCompress: Playnite ships SharpCompress 0.26, and its Toolbox packer leaves a
    /// plugin's own copy of a DLL Playnite already has out of the .pext, so a SharpCompress call
    /// from the plugin fails inside Playnite with FileNotFoundException. The zip is the format
    /// that matters -- RomM serves every folder download as one -- and 7z/rar are 7-Zip's.
    /// </summary>
    internal static class ArchiveFormats
    {
        public enum Kind { None, Zip, SevenZip, Rar }

        public static Kind Detect(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return Kind.None;

            var head = new byte[6];
            int read;
            using (var f = File.OpenRead(path))
                read = f.Read(head, 0, head.Length);

            if (read >= 4 && head[0] == 'P' && head[1] == 'K' &&
                ((head[2] == 3 && head[3] == 4) || (head[2] == 5 && head[3] == 6)))
                return Kind.Zip;
            if (read >= 6 && head[0] == '7' && head[1] == 'z' && head[2] == 0xBC && head[3] == 0xAF && head[4] == 0x27 && head[5] == 0x1C)
                return Kind.SevenZip;
            if (read >= 6 && head[0] == 'R' && head[1] == 'a' && head[2] == 'r' && head[3] == '!' && head[4] == 0x1A && head[5] == 0x07)
                return Kind.Rar;
            return Kind.None;
        }

        /// <summary>The file entries' names of a zip -- directory entries left out.</summary>
        public static IList<string> ZipEntryNames(string path)
        {
            using (var archive = ZipFile.OpenRead(path))
                return FileEntries(archive).Select(e => e.FullName).ToList();
        }

        /// <summary>
        /// Unpacks a zip into <paramref name="installDir"/>. Entry names are untrusted: each is
        /// resolved inside the folder first, and the whole archive is checked before anything is
        /// written, so a hostile name refuses the archive rather than half-extracting it.
        /// </summary>
        public static void ExtractZip(string archivePath, string installDir, Action<int, int> progress, CancellationToken ct)
        {
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                var entries = FileEntries(archive).ToList();
                var destinations = entries.Select(e => RomMInstallPaths.ResolveWithin(installDir, e.FullName)).ToList();

                progress?.Invoke(0, entries.Count);
                for (int i = 0; i < entries.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(Path.GetDirectoryName(destinations[i]));
                    using (var input = entries[i].Open())
                    using (var output = File.Create(destinations[i]))
                        input.CopyTo(output);
                    progress?.Invoke(i + 1, entries.Count);
                }
            }
        }

        private static IEnumerable<ZipArchiveEntry> FileEntries(ZipArchive archive)
        {
            return archive.Entries.Where(e => !(e.Length == 0 && (e.FullName.EndsWith("/") || e.FullName.EndsWith("\\"))));
        }
    }
}
