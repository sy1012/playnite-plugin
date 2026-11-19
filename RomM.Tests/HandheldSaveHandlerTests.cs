using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Playnite.SDK.Models;
using RomM.Saves.Handlers;
using RomM.Saves.Titles;
using RomM.Saves.Units;
using Xunit;

namespace RomM.Tests
{
    public class HandheldSaveHandlerTests : IDisposable
    {
        private readonly string _dir;

        public HandheldSaveHandlerTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string P(params string[] parts) => Path.Combine(new[] { _dir }.Concat(parts).ToArray());

        private string Write(byte[] bytes, params string[] parts)
        {
            var path = P(parts);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private string Write(string text, params string[] parts) => Write(Encoding.UTF8.GetBytes(text), parts);

        private SaveTargetRequest Request(string install, string content) => new SaveTargetRequest
        {
            Game = new Game("Test"),
            EmulatorInstallDir = install,
            ContentPath = content,
            AppDataDir = P("AppData"),
            DocumentsDir = P("Documents"),
            BackupDirectory = P("backups"),
        };

        private static readonly string Zero = new string('0', 32);

        // ---- fixtures ----

        /// <summary>A .3ds card image: NCSD header, partition 0 an unencrypted NCCH with an exheader.</summary>
        private static byte[] Card3ds(ulong programId, ulong extdataId, bool encrypted = false)
        {
            var image = new byte[0x200 + 0x200 + 0x400];
            Encoding.ASCII.GetBytes("NCSD").CopyTo(image, 0x100);
            BitConverter.GetBytes(1u).CopyTo(image, 0x120);   // partition 0 at 1 * 0x200
            BitConverter.GetBytes(4u).CopyTo(image, 0x124);
            int ncch = 0x200;
            Encoding.ASCII.GetBytes("NCCH").CopyTo(image, ncch + 0x100);
            BitConverter.GetBytes(programId).CopyTo(image, ncch + 0x118);
            BitConverter.GetBytes(0x400u).CopyTo(image, ncch + 0x180);
            image[ncch + 0x18F] = (byte)(encrypted ? 0 : 0x04);
            BitConverter.GetBytes(extdataId).CopyTo(image, ncch + 0x200 + 0x230);
            return image;
        }

        /// <summary>A minimal ISO 9660 image: files under folders, each path given with '/'.</summary>
        internal static byte[] Iso(IDictionary<string, byte[]> files, int sectorSize = 2048, int userOffset = 0)
        {
            var sectors = new List<byte[]>();
            for (int i = 0; i < 16; i++) sectors.Add(new byte[2048]);
            sectors.Add(new byte[2048]); // 16: PVD
            sectors.Add(new byte[2048]); // 17: terminator

            // Lay out every directory then every file, each in its own sector.
            var dirs = new List<string> { "" };
            foreach (var path in files.Keys)
            {
                var parts = path.Split('/');
                for (int i = 1; i < parts.Length; i++)
                {
                    var d = string.Join("/", parts.Take(i));
                    if (!dirs.Contains(d)) dirs.Add(d);
                }
            }
            var lba = new Dictionary<string, int>();
            foreach (var d in dirs) { lba["D:" + d] = sectors.Count; sectors.Add(new byte[2048]); }
            foreach (var f in files)
            {
                lba["F:" + f.Key] = sectors.Count;
                for (int o = 0; o < Math.Max(1, f.Value.Length); o += 2048)
                {
                    var s = new byte[2048];
                    Buffer.BlockCopy(f.Value, o, s, 0, Math.Min(2048, f.Value.Length - o));
                    sectors.Add(s);
                }
            }

            Func<string, int, int, bool, byte[]> record = (name, at, size, isDir) =>
            {
                var n = Encoding.ASCII.GetBytes(name);
                var r = new byte[33 + n.Length + (n.Length % 2 == 0 ? 1 : 0)];
                r[0] = (byte)r.Length;
                BitConverter.GetBytes((uint)at).CopyTo(r, 2);
                BitConverter.GetBytes((uint)size).CopyTo(r, 10);
                r[25] = (byte)(isDir ? 2 : 0);
                r[32] = (byte)n.Length;
                n.CopyTo(r, 33);
                return r;
            };

            foreach (var d in dirs)
            {
                var sector = sectors[lba["D:" + d]];
                int pos = 0;
                var prefix = d.Length == 0 ? "" : d + "/";
                foreach (var child in dirs.Where(c => c != d && c.StartsWith(prefix) && !c.Substring(prefix.Length).Contains("/")))
                {
                    var r = record(child.Substring(prefix.Length), lba["D:" + child], 2048, true);
                    r.CopyTo(sector, pos); pos += r.Length;
                }
                foreach (var f in files.Where(f => f.Key.StartsWith(prefix) && !f.Key.Substring(prefix.Length).Contains("/")))
                {
                    var r = record(f.Key.Substring(prefix.Length) + ";1", lba["F:" + f.Key], f.Value.Length, false);
                    r.CopyTo(sector, pos); pos += r.Length;
                }
            }

            var pvd = sectors[16];
            pvd[0] = 1;
            Encoding.ASCII.GetBytes("CD001").CopyTo(pvd, 1);
            record("\0", lba["D:"], 2048, true).CopyTo(pvd, 156);
            sectors[17][0] = 255;
            Encoding.ASCII.GetBytes("CD001").CopyTo(sectors[17], 1);

            var image = new byte[sectors.Count * sectorSize];
            for (int i = 0; i < sectors.Count; i++)
                Buffer.BlockCopy(sectors[i], 0, image, i * sectorSize + userOffset, 2048);
            return image;
        }

        private static byte[] SfoWith(params KeyValuePair<string, string>[] entries)
        {
            var keys = new MemoryStream();
            var data = new MemoryStream();
            var index = new MemoryStream();
            var w = new BinaryWriter(index);
            foreach (var e in entries)
            {
                var value = Encoding.UTF8.GetBytes(e.Value + "\0");
                w.Write((ushort)keys.Length); w.Write((ushort)0x0204); w.Write(value.Length); w.Write(value.Length); w.Write((int)data.Length);
                var k = Encoding.ASCII.GetBytes(e.Key + "\0");
                keys.Write(k, 0, k.Length);
                data.Write(value, 0, value.Length);
            }
            var ms = new MemoryStream();
            var h = new BinaryWriter(ms);
            int keyTable = 0x14 + (int)index.Length;
            int dataTable = keyTable + (int)keys.Length;
            h.Write(new byte[] { 0, (byte)'P', (byte)'S', (byte)'F' });
            h.Write(0x0101); h.Write(keyTable); h.Write(dataTable); h.Write(entries.Length);
            h.Write(index.ToArray()); h.Write(keys.ToArray()); h.Write(data.ToArray());
            return ms.ToArray();
        }

        private static KeyValuePair<string, string> KV(string k, string v) => new KeyValuePair<string, string>(k, v);

        // ---- 3DS / Azahar ----

        [Fact]
        public void N3ds_card_names_program_and_extdata_ids()
        {
            var rom = N3dsRom.Read(Write(Card3ds(0x0004000000055D00, 0x0000055D), "pokemon.3ds"));

            Assert.Equal(0x0004000000055D00UL, rom.ProgramId);
            Assert.Equal(0x0000055DUL, rom.ExtdataId);
        }

        [Fact]
        public void N3ds_encrypted_card_names_nothing()
        {
            Assert.Null(N3dsRom.Read(Write(Card3ds(1, 2, encrypted: true), "enc.3ds")));
        }

        [Fact]
        public void Azahar_save_is_data_and_extdata_under_the_appdata_sdmc()
        {
            var rom = Write(Card3ds(0x0004000000055D00, 0x0000055D), "roms", "Pokemon X.3ds");

            var target = (UnitSaveTarget)new AzaharSaveHandler().ResolveTarget(Request(P("azahar"), rom));

            var roots = ((ArchiveRootsUnit)target.Unit).Roots;
            var container = P("AppData", "Azahar", "sdmc", "Nintendo 3DS", Zero, Zero);
            Assert.Equal(Path.Combine(container, "title", "00040000", "00055d00", "data"), roots["data"]);
            Assert.Equal(Path.Combine(container, "extdata", "00000000", "0000055D"), roots["extdata"]);
            Assert.Equal("Pokemon X.zip", target.FileName);
        }

        [Fact]
        public void Azahar_follows_portable_user_folder_and_custom_storage()
        {
            Write("[Data%20Storage]\nuse_custom_storage\\default=false\nuse_custom_storage=true\n" +
                  "sdmc_directory\\default=false\nsdmc_directory=" + P("sd").Replace('\\', '/') + "\n",
                  "azahar", "user", "config", "qt-config.ini");

            Assert.Equal(P("sd"), AzaharSaveHandler.SdmcDir(Request(P("azahar"), null)));
        }

        [Fact]
        public void Azahar_restore_replaces_only_the_roots_the_zip_carries()
        {
            Write("old save", "data", "00000001", "save.bin");
            Write("extdata", "ext", "00000000", "file.bin");
            var unit = new ArchiveRootsUnit(new Dictionary<string, string> { ["data"] = P("data"), ["extdata"] = P("ext") }, _dir);
            var staged = P("staged");
            Write("new save", "staged", "data", "00000001", "save.bin");

            Assert.Null(unit.RejectEntries(new[] { "data/00000001/save.bin" }));
            Assert.NotNull(unit.RejectEntries(new[] { "other/x.bin" }));
            unit.Restore(staged);

            Assert.Equal("new save", File.ReadAllText(P("data", "00000001", "save.bin")));
            Assert.Equal("extdata", File.ReadAllText(P("ext", "00000000", "file.bin")));
        }

        // ---- PSP / PPSSPP ----

        [Fact]
        public void Psp_iso_names_its_game_id_without_the_dash()
        {
            var iso = Iso(new Dictionary<string, byte[]> { ["PSP_GAME/PARAM.SFO"] = SfoWith(KV("DISC_ID", "ULUS-10064")) });
            Assert.Equal("ULUS10064", PspDisc.GameId(Write(iso, "game.iso")));
        }

        [Fact]
        public void Ppsspp_save_is_the_games_save_folders_in_the_portable_memstick_and_skips_installed_data()
        {
            var iso = Iso(new Dictionary<string, byte[]> { ["PSP_GAME/PARAM.SFO"] = SfoWith(KV("DISC_ID", "ULUS10064")) });
            var rom = Write(iso, "roms", "Monster Hunter.iso");
            var saveData = new[] { "ppsspp", "memstick", "PSP", "SAVEDATA" };
            Write(SfoWith(KV("SAVEDATA_PARAMS", "x"), KV("TITLE", "save")), saveData.Concat(new[] { "ULUS10064DATA00", "PARAM.SFO" }).ToArray());
            Write("data", saveData.Concat(new[] { "ULUS10064DATA00", "DATA.BIN" }).ToArray());
            Write(SfoWith(KV("TITLE", "installed")), saveData.Concat(new[] { "ULUS10064INSTALL", "PARAM.SFO" }).ToArray());
            Write("other", saveData.Concat(new[] { "ULJS00001DATA", "DATA.BIN" }).ToArray());

            var target = (UnitSaveTarget)new PpssppSaveHandler().ResolveTarget(Request(P("ppsspp"), rom));

            Assert.Equal(new[] { "ULUS10064DATA00/DATA.BIN", "ULUS10064DATA00/PARAM.SFO" },
                target.Unit.CurrentEntries().Select(e => e.EntryName).OrderBy(n => n, StringComparer.Ordinal));
            Assert.NotNull(target.Unit.RejectEntries(new[] { "ULUS10064INSTALL/PARAM.SFO" }));
        }

        [Fact]
        public void Ppsspp_installed_copy_uses_documents()
        {
            Write("", "ppsspp", "installed.txt");
            Assert.Equal(P("Documents", "PPSSPP"), PpssppSaveHandler.MemstickDir(Request(P("ppsspp"), null)));
        }

        // ---- PS2 / PCSX2 ----

        [Theory]
        [InlineData("BOOT2 = cdrom0:\\SLUS_201.52;1\r\nVER = 1.00\r\n", "SLUS-20152")]
        [InlineData("BOOT = cdrom:\\SLUS_000.01;1\r\n", null)]
        public void Ps2_serial_comes_from_the_boot2_line(string cnf, string serial)
        {
            Assert.Equal(serial, Ps2Disc.SerialFromSystemCnf(cnf));
        }

        [Theory]
        [InlineData(2048, 0)]
        [InlineData(2352, 24)]
        public void Ps2_disc_serial_is_read_from_system_cnf_in_either_sector_layout(int sectorSize, int userOffset)
        {
            var iso = Iso(new Dictionary<string, byte[]> { ["SYSTEM.CNF"] = Encoding.ASCII.GetBytes("BOOT2 = cdrom0:\\SLUS_201.52;1\n") },
                sectorSize, userOffset);
            Assert.Equal("SLUS-20152", Ps2Disc.DiscSerial(Write(iso, "game.iso")));
        }

        [Fact]
        public void Ps2_gamedb_memcard_filter_replaces_the_serial()
        {
            var iso = Iso(new Dictionary<string, byte[]> { ["SYSTEM.CNF"] = Encoding.ASCII.GetBytes("BOOT2 = cdrom0:\\SLES_820.12;1\n") });
            Assert.Equal("SLES-82011", Ps2Disc.CardSerial(Write(iso, "disc2.iso")));
        }

        [Fact]
        public void Pcsx2_save_is_the_games_folders_on_the_portable_folder_card()
        {
            Write("", "pcsx2", "portable.ini");
            Write("[MemoryCards]\nSlot1_Enable = true\nSlot1_Filename = Mcd001.ps2\nSlot2_Enable = false\n", "pcsx2", "inis", "PCSX2.ini");
            Write("sb", "pcsx2", "memcards", "Mcd001.ps2", "_pcsx2_superblock");
            Write("save", "pcsx2", "memcards", "Mcd001.ps2", "BASLUS-20152GTA", "save.bin");
            Write("other", "pcsx2", "memcards", "Mcd001.ps2", "BASLUS-99999", "x.bin");
            var iso = Iso(new Dictionary<string, byte[]> { ["SYSTEM.CNF"] = Encoding.ASCII.GetBytes("BOOT2 = cdrom0:\\SLUS_201.52;1\n") });
            var rom = Write(iso, "roms", "GTA.iso");

            var target = (UnitSaveTarget)new Pcsx2SaveHandler().ResolveTarget(Request(P("pcsx2"), rom));

            var unit = (PrefixFoldersUnit)target.Unit;
            Assert.Equal(P("pcsx2", "memcards", "Mcd001.ps2"), unit.Directory_);
            Assert.Equal("BASLUS-20152", unit.Prefix);
            Assert.Equal(new[] { "BASLUS-20152GTA/save.bin" }, unit.CurrentEntries().Select(e => e.EntryName));
        }

        [Fact]
        public void Pcsx2_region_letter_is_read_off_the_card_where_the_game_has_folders()
        {
            Directory.CreateDirectory(P("card", "BESLES-82011SAVE"));
            Assert.Equal("BESLES-82011", Pcsx2SaveHandler.Prefix(P("card"), "SLES-82011"));
            Assert.Equal("BASLUS-20152", Pcsx2SaveHandler.Prefix(P("card"), "SLUS-20152"));
        }

        [Fact]
        public void Pcsx2_with_only_a_file_card_is_not_synced()
        {
            Write("", "pcsx2", "portable.ini");
            Write(new byte[16], "pcsx2", "memcards", "Mcd001.ps2");
            Assert.Null(Pcsx2SaveHandler.FolderCard(Request(P("pcsx2"), null)));
        }
    }
}
