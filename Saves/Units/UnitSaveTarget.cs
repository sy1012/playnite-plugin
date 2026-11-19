using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Writers.Zip;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RomM.Saves.Units
{
    /// <summary>
    /// A save of several files (<see cref="SaveUnit"/>) carried as one zip named
    /// <c>&lt;rom file stem&gt;.zip</c>, the name Tender and Argosy upload it under.
    ///
    /// Packing is deterministic -- entries sorted, fixed timestamps -- and the hash is RomM's
    /// per-entry zip hash, so it does not depend on the bytes of any one packing. A restore keeps
    /// a zip of what it replaces in <c>backupDir</c> first: the local save may exist nowhere else.
    /// </summary>
    internal sealed class UnitSaveTarget : SaveTarget
    {
        private const int BackupsKept = 10;
        private static readonly DateTime FixedEntryTime = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly SaveUnit _unit;
        private readonly string _backupDir;

        public UnitSaveTarget(string emulatorTag, string fileName, SaveUnit unit, string backupDir)
        {
            EmulatorTag = emulatorTag;
            FileName = fileName;
            _unit = unit;
            _backupDir = backupDir;
        }

        public SaveUnit Unit => _unit;

        public override string EmulatorTag { get; }

        public override string Slot => "autosave";

        public override string FileName { get; }

        public override bool Exists => _unit.CurrentEntries().Count > 0;

        public override DateTime UpdatedAtUtc =>
            _unit.CurrentEntries().Select(e => File.GetLastWriteTimeUtc(e.FilePath)).DefaultIfEmpty(DateTime.MinValue).Max();

        public override long SizeBytes
        {
            get
            {
                var temp = PackToTemp(_unit.CurrentEntries());
                try { return new FileInfo(temp).Length; }
                finally { TryDelete(temp); }
            }
        }

        public override string ContentHash()
        {
            return SaveFileHash.EntriesAsZipHex(_unit.CurrentEntries()
                .Select(e => new KeyValuePair<string, string>(e.EntryName, e.FilePath)));
        }

        public override PreparedUpload PrepareUpload()
        {
            return new PreparedUpload(PackToTemp(_unit.CurrentEntries()), FileName, isTemporary: true);
        }

        public override void ApplyDownload(byte[] payload, DateTime? serverUpdatedAtUtc)
        {
            var parent = _unit.StagingParent;
            Directory.CreateDirectory(parent);
            var staging = Path.Combine(parent, ".romm-staging-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(staging);

            try
            {
                var zipPath = Path.Combine(staging, "download.zip");
                File.WriteAllBytes(zipPath, payload);
                var unpacked = Path.Combine(staging, "files");
                Unpack(zipPath, unpacked);

                BackUpCurrent();

                var written = _unit.Restore(unpacked);
                if (serverUpdatedAtUtc.HasValue)
                {
                    var stamp = serverUpdatedAtUtc.Value.ToUniversalTime();
                    foreach (var file in written)
                        File.SetLastWriteTimeUtc(file, stamp);
                }
            }
            finally
            {
                try { Directory.Delete(staging, true); } catch { }
            }
        }

        /// <summary>
        /// Unpacks the archive into <paramref name="dest"/> after checking every name, so nothing
        /// is written outside it and nothing lands where this unit does not own it.
        /// </summary>
        private void Unpack(string zipPath, string dest)
        {
            using (var archive = ZipArchive.Open(zipPath))
            {
                var files = archive.Entries.Where(e => !e.IsDirectory).ToList();
                if (files.Count == 0)
                    throw new InvalidDataException("The downloaded save archive holds no files.");

                var names = files.Select(e => SafeEntryName(e.Key)).ToList();
                var rejection = _unit.RejectEntries(names);
                if (rejection != null)
                    throw new InvalidDataException("The downloaded save archive is not laid out for this game: " + rejection);

                for (int i = 0; i < files.Count; i++)
                {
                    var target = Path.Combine(dest, names[i].Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var output = File.Create(target))
                        files[i].WriteTo(output);
                }
            }
        }

        /// <summary>
        /// The entry name as a relative '/'-separated path, or an exception when it could escape
        /// the folder it is unpacked into.
        /// </summary>
        internal static string SafeEntryName(string key)
        {
            if (string.IsNullOrEmpty(key) || key.IndexOf('\\') >= 0 || key.IndexOf(':') >= 0 || key[0] == '/')
                throw new InvalidDataException($"Unsafe entry name '{key}' in the downloaded save archive.");

            var segments = key.Split('/');
            if (segments.Any(s => s.Length == 0 || s == "." || s == ".." ||
                                  s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                throw new InvalidDataException($"Unsafe entry name '{key}' in the downloaded save archive.");

            return key;
        }

        private void BackUpCurrent()
        {
            var entries = _unit.CurrentEntries();
            if (entries.Count == 0 || string.IsNullOrEmpty(_backupDir))
                return;

            Directory.CreateDirectory(_backupDir);
            var stem = Path.GetFileNameWithoutExtension(FileName);
            var backup = Path.Combine(_backupDir, $"{stem}.{DateTime.UtcNow:yyyyMMddTHHmmssfff}.zip");
            var temp = PackToTemp(entries);
            try { File.Copy(temp, backup, overwrite: true); }
            finally { TryDelete(temp); }

            foreach (var old in Directory.EnumerateFiles(_backupDir, stem + ".*.zip")
                         .OrderByDescending(f => f, StringComparer.Ordinal)
                         .Skip(BackupsKept))
            {
                try { File.Delete(old); } catch { }
            }
        }

        // The upload is filed under FileName by PreparedUpload, not by this path's name.
        private static string PackToTemp(IList<UnitEntry> entries)
        {
            var path = Path.Combine(Path.GetTempPath(), "romm-save-" + Guid.NewGuid().ToString("N") + ".zip");
            Pack(entries, path);
            return path;
        }

        internal static void Pack(IEnumerable<UnitEntry> entries, string path)
        {
            using (var stream = File.Create(path))
            using (var writer = new ZipWriter(stream, new ZipWriterOptions(CompressionType.Deflate)))
            {
                foreach (var entry in entries.OrderBy(e => e.EntryName, StringComparer.Ordinal))
                {
                    using (var source = File.OpenRead(entry.FilePath))
                        writer.Write(entry.EntryName, source, FixedEntryTime);
                }
            }
        }

        private static void TryDelete(string file)
        {
            try { File.Delete(file); } catch { }
        }
    }
}
