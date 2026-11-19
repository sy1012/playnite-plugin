using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RomM.Saves;
using RomM.Saves.Units;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Writers.Zip;
using Xunit;

namespace RomM.Tests
{
    public class UnitSaveTargetTests : IDisposable
    {
        private readonly string _dir;

        public UnitSaveTargetTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string P(params string[] parts) => Path.Combine(new[] { _dir }.Concat(parts).ToArray());

        private static void Write(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
        }

        private static byte[] Zip(params KeyValuePair<string, string>[] entries)
        {
            using (var ms = new MemoryStream())
            {
                using (var writer = new ZipWriter(ms, new ZipWriterOptions(CompressionType.Deflate) { LeaveStreamOpen = true }))
                {
                    foreach (var e in entries)
                        writer.Write(e.Key, new MemoryStream(Encoding.UTF8.GetBytes(e.Value)), DateTime.UtcNow);
                }
                return ms.ToArray();
            }
        }

        private static KeyValuePair<string, string> E(string name, string content) => new KeyValuePair<string, string>(name, content);

        private UnitSaveTarget Target(SaveUnit unit) => new UnitSaveTarget("eden", "Game.zip", unit, P("backups"));

        // Reference digests computed by Tender (romm-tender backend/domain/save_hash.py) for the same
        // entries: the two clients must agree on "unchanged" or every sync is a false conflict.
        [Fact]
        public void Own_folder_hash_matches_what_tender_computes_for_the_same_save()
        {
            Write(P("save", "0", "save.dat"), "switch-save");
            Write(P("save", "0", "sub", "meta.bin"), "meta");

            var target = Target(new OwnFolderUnit(P("save", "0"), "0100F2C0115B6000"));

            Assert.Equal("37802aaf39325fcdf6c0dc991e367e3a", target.ContentHash());
        }

        [Fact]
        public void Prefix_folders_hash_matches_what_tender_computes_for_the_same_save()
        {
            Write(P("savedata", "BLUS30443GAMEDATA", "PARAM.SFO"), "sfo");
            Write(P("savedata", "BLUS30443-AUTOSAVE", "DATA.BIN"), "data");
            Write(P("savedata", "BLUS99999DATA", "OTHER.BIN"), "another game");

            var target = Target(new PrefixFoldersUnit(P("savedata"), "BLUS30443"));

            Assert.Equal("4f4d5dfe53c0faebd03565146292a85b", target.ContentHash());
        }

        [Fact]
        public void Upload_is_a_zip_whose_server_hash_equals_the_reported_hash()
        {
            Write(P("save", "a.bin"), "one");
            Write(P("save", "dir", "b.bin"), "two");
            var target = Target(new OwnFolderUnit(P("save"), "0005000010101A00"));

            using (var upload = target.PrepareUpload())
            {
                Assert.Equal("Game.zip", upload.FileName);
                Assert.Equal(target.ContentHash(), SaveFileHash.ZipHexFile(upload.FilePath));
                using (var archive = ZipArchive.Open(upload.FilePath))
                    Assert.Equal(new[] { "0005000010101A00/a.bin", "0005000010101A00/dir/b.bin" },
                        archive.Entries.Select(e => e.Key).OrderBy(k => k, StringComparer.Ordinal));
            }
        }

        [Fact]
        public void Absent_until_the_unit_has_a_file()
        {
            Assert.False(Target(new OwnFolderUnit(P("missing"), "X")).Exists);
        }

        [Fact]
        public void Own_folder_restore_replaces_the_folder_and_keeps_a_backup()
        {
            Write(P("save", "old.bin"), "old");
            var target = Target(new OwnFolderUnit(P("save"), "0100F2C0115B6000"));
            var stamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

            target.ApplyDownload(Zip(E("0100F2C0115B6000/new.bin", "new"), E("0100F2C0115B6000/d/x.bin", "x")), stamp);

            Assert.False(File.Exists(P("save", "old.bin")));
            Assert.Equal("new", File.ReadAllText(P("save", "new.bin")));
            Assert.Equal("x", File.ReadAllText(P("save", "d", "x.bin")));
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(P("save", "new.bin")));
            var backup = Directory.GetFiles(P("backups")).Single();
            using (var archive = ZipArchive.Open(backup))
                Assert.Equal("0100F2C0115B6000/old.bin", archive.Entries.Single().Key);
            Assert.Empty(Directory.GetDirectories(_dir).Where(d => Path.GetFileName(d).StartsWith(".romm")));
        }

        [Fact]
        public void Own_folder_restore_creates_the_folder_on_a_fresh_machine()
        {
            var target = Target(new OwnFolderUnit(P("mlc01", "usr", "save", "00050000", "10101a00"), "10101a00"));

            target.ApplyDownload(Zip(E("10101a00/user/common/s.dat", "s")), null);

            Assert.Equal("s", File.ReadAllText(P("mlc01", "usr", "save", "00050000", "10101a00", "user", "common", "s.dat")));
        }

        [Fact]
        public void A_zip_laid_out_for_another_game_is_refused_and_nothing_changes()
        {
            Write(P("save", "keep.bin"), "keep");
            var target = Target(new OwnFolderUnit(P("save"), "0100F2C0115B6000"));

            Assert.Throws<InvalidDataException>(() => target.ApplyDownload(Zip(E("0100AAAAAAAAA000/x.bin", "x")), null));

            Assert.Equal("keep", File.ReadAllText(P("save", "keep.bin")));
            Assert.False(Directory.Exists(P("backups")));
        }

        [Fact]
        public void An_entry_climbing_out_of_the_folder_is_refused()
        {
            var target = Target(new OwnFolderUnit(P("save"), "A"));

            Assert.Throws<InvalidDataException>(() => target.ApplyDownload(Zip(E("A/../../evil.bin", "x")), null));
            Assert.False(File.Exists(P("evil.bin")));
        }

        [Fact]
        public void Own_files_restore_touches_only_this_games_files()
        {
            Write(P("Card A", "01-GALE-old.gci"), "GALEold");
            Write(P("Card A", "01-GALE-gone.gci"), "GALEgone");
            Write(P("Card A", "01-GZLE-zelda.gci"), "GZLEzelda");
            var unit = new OwnFilesUnit(P("Card A"), f => File.ReadAllText(f).StartsWith("GALE"));
            var target = Target(unit);

            target.ApplyDownload(Zip(E("01-GALE-old.gci", "GALEnew")), null);

            Assert.Equal("GALEnew", File.ReadAllText(P("Card A", "01-GALE-old.gci")));
            Assert.False(File.Exists(P("Card A", "01-GALE-gone.gci")));
            Assert.Equal("GZLEzelda", File.ReadAllText(P("Card A", "01-GZLE-zelda.gci")));
        }

        [Fact]
        public void Own_files_restore_will_not_replace_another_games_file()
        {
            Write(P("Card A", "01-GZLE-zelda.gci"), "GZLEzelda");
            var target = Target(new OwnFilesUnit(P("Card A"), f => File.ReadAllText(f).StartsWith("GALE")));

            Assert.Throws<InvalidDataException>(() => target.ApplyDownload(Zip(E("01-GZLE-zelda.gci", "GALE")), null));
            Assert.Equal("GZLEzelda", File.ReadAllText(P("Card A", "01-GZLE-zelda.gci")));
        }

        [Fact]
        public void Prefix_restore_replaces_the_games_folders_and_leaves_the_rest()
        {
            Write(P("savedata", "BLUS30443GAMEDATA", "OLD.BIN"), "old");
            Write(P("savedata", "BLUS30443-STALE", "S.BIN"), "stale");
            Write(P("savedata", "BLUS99999DATA", "OTHER.BIN"), "other");
            var target = Target(new PrefixFoldersUnit(P("savedata"), "BLUS30443"));

            target.ApplyDownload(Zip(E("BLUS30443GAMEDATA/NEW.BIN", "new")), null);

            Assert.Equal("new", File.ReadAllText(P("savedata", "BLUS30443GAMEDATA", "NEW.BIN")));
            Assert.False(File.Exists(P("savedata", "BLUS30443GAMEDATA", "OLD.BIN")));
            Assert.False(Directory.Exists(P("savedata", "BLUS30443-STALE")));
            Assert.Equal("other", File.ReadAllText(P("savedata", "BLUS99999DATA", "OTHER.BIN")));
        }

        [Fact]
        public void Prefix_restore_refuses_a_file_at_the_top_of_the_zip()
        {
            var target = Target(new PrefixFoldersUnit(P("savedata"), "BLUS30443"));

            Assert.Throws<InvalidDataException>(() => target.ApplyDownload(Zip(E("BLUS30443.BIN", "x")), null));
        }
    }
}
