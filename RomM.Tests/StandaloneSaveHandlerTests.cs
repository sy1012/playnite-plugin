using System;
using System.IO;
using System.Linq;
using System.Text;
using Playnite.SDK.Models;
using RomM.Saves.Handlers;
using RomM.Saves.Units;
using Xunit;

namespace RomM.Tests
{
    public class StandaloneSaveHandlerTests : IDisposable
    {
        private readonly string _dir;

        public StandaloneSaveHandlerTests()
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
            BackupDirectory = P("backups"),
        };

        // ---- Eden ----

        private static byte[] Profiles(params byte[][] uuids)
        {
            var data = new byte[0x10 + 8 * 0xC8];
            for (int i = 0; i < uuids.Length; i++)
                uuids[i].CopyTo(data, 0x10 + i * 0xC8);
            return data;
        }

        private static readonly byte[] UserA = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        private static readonly byte[] UserB = Enumerable.Range(101, 16).Select(i => (byte)i).ToArray();

        [Fact]
        public void Eden_user_folder_is_the_uuid_as_high_then_low_little_endian_words()
        {
            Assert.Equal("100F0E0D0C0B0A090807060504030201", EdenSaveHandler.UserFolder(UserA));
        }

        [Fact]
        public void Eden_save_is_the_current_users_application_folder_in_appdata()
        {
            var nand = P("AppData", "eden", "nand");
            Write(Profiles(UserA, UserB), "AppData", "eden", "nand", "system", "save", "8000000000000010", "su", "avators", "profiles.dat");
            Write("[System]\ncurrent_user\\default=false\ncurrent_user=1\n", "AppData", "eden", "config", "qt-config.ini");
            var rom = Write(new byte[0], "roms", "Snipperclips [01008E20047DC000][JP][v0].nsp");

            var target = (UnitSaveTarget)new EdenSaveHandler().ResolveTarget(Request(P("eden-install"), rom));

            var unit = (OwnFolderUnit)target.Unit;
            Assert.Equal(Path.Combine(nand, "user", "save", "0000000000000000", EdenSaveHandler.UserFolder(UserB), "01008E20047DC000"), unit.Root);
            Assert.Equal("01008E20047DC000", unit.ArchiveFolder);
            Assert.Equal("Snipperclips [01008E20047DC000][JP][v0].zip", target.FileName);
            Assert.Equal("eden", target.EmulatorTag);
        }

        [Fact]
        public void Eden_portable_user_folder_and_moved_nand_are_followed()
        {
            var moved = P("elsewhere", "nand");
            Write($"[Data%20Storage]\nnand_directory\\default=false\nnand_directory={moved.Replace('\\', '/')}\n",
                "eden-install", "user", "config", "qt-config.ini");
            Write(Profiles(UserA), "elsewhere", "nand", "system", "save", "8000000000000010", "su", "avators", "profiles.dat");
            var rom = Write(new byte[0], "roms", "Game [0100F2C0115B6000].nsp");

            var target = (UnitSaveTarget)new EdenSaveHandler().ResolveTarget(Request(P("eden-install"), rom));

            Assert.StartsWith(moved, ((OwnFolderUnit)target.Unit).Root);
        }

        [Fact]
        public void Eden_without_a_user_profile_is_not_synced()
        {
            var rom = Write(new byte[0], "roms", "Game [0100F2C0115B6000].nsp");
            Assert.Null(new EdenSaveHandler().ResolveTarget(Request(P("eden-install"), rom)));
        }

        // ---- Cemu ----

        [Fact]
        public void Cemu_save_is_the_titles_folder_in_the_configured_mlc()
        {
            Write("<?xml version=\"1.0\"?><content><mlc_path>" + P("mlc") + "</mlc_path></content>", "AppData", "Cemu", "settings.xml");
            Write("<app><title_id>0005000010145D00</title_id></app>", "roms", "Game", "code", "app.xml");
            var rpx = Write(new byte[0], "roms", "Game", "code", "game.rpx");

            var target = (UnitSaveTarget)new CemuSaveHandler().ResolveTarget(Request(P("cemu"), rpx));

            var unit = (OwnFolderUnit)target.Unit;
            Assert.Equal(P("mlc", "usr", "save", "00050000", "10145d00"), unit.Root);
            Assert.Equal("10145d00", unit.ArchiveFolder);
        }

        [Fact]
        public void Cemu_portable_folder_wins_and_mlc_defaults_beside_its_settings()
        {
            Directory.CreateDirectory(P("cemu", "portable"));
            Assert.Equal(P("cemu", "portable", "mlc01"), CemuSaveHandler.MlcPath(P("cemu"), P("AppData")));
            Assert.Equal(P("AppData", "Cemu", "mlc01"), CemuSaveHandler.MlcPath(P("cemu2"), P("AppData")));
        }

        // ---- RPCS3 ----

        [Fact]
        public void Rpcs3_save_is_the_title_prefixed_folders_of_the_one_user()
        {
            Directory.CreateDirectory(P("rpcs3", "dev_hdd0", "home", "00000001", "savedata"));
            Write(SaveTitleReaderTests.Sfo("BLUS30443"), "roms", "Demon", "PS3_GAME", "PARAM.SFO");

            var target = (UnitSaveTarget)new Rpcs3SaveHandler().ResolveTarget(Request(P("rpcs3"), P("roms", "Demon")));

            var unit = (PrefixFoldersUnit)target.Unit;
            Assert.Equal(P("rpcs3", "dev_hdd0", "home", "00000001", "savedata"), unit.Directory_);
            Assert.Equal("BLUS30443", unit.Prefix);
            Assert.Equal("Demon.zip", target.FileName);
        }

        [Fact]
        public void Rpcs3_with_two_users_is_not_synced()
        {
            Directory.CreateDirectory(P("rpcs3", "dev_hdd0", "home", "00000001"));
            Directory.CreateDirectory(P("rpcs3", "dev_hdd0", "home", "00000002"));
            Write(SaveTitleReaderTests.Sfo("BLUS30443"), "roms", "Demon", "PS3_GAME", "PARAM.SFO");

            Assert.Null(new Rpcs3SaveHandler().ResolveTarget(Request(P("rpcs3"), P("roms", "Demon"))));
        }

        [Fact]
        public void Rpcs3_follows_vfs_yml()
        {
            Write("/dev_hdd0/: $(EmulatorDir)hdd/\n/dev_hdd1/: $(EmulatorDir)dev_hdd1/\n", "rpcs3", "config", "vfs.yml");
            Assert.Equal(P("rpcs3", "hdd") + Path.DirectorySeparatorChar, Rpcs3SaveHandler.DevHdd0(P("rpcs3")));
        }

        // ---- Dolphin (RetroArch core) ----

        private string RetroArch()
        {
            Write("savefile_directory = \":\\saves\"\nsort_savefiles_enable = \"true\"\n", "RetroArch", "retroarch.cfg");
            return P("RetroArch");
        }

        [Fact]
        public void RetroArch_dolphin_gamecube_save_is_the_games_gci_files_in_card_a()
        {
            var ra = RetroArch();
            var card = new[] { "RetroArch", "saves", "dolphin-emu", "User", "GC", "USA", "Card A" };
            Write("GALE....", card.Concat(new[] { "01-GALE-SuperSmashBros.gci" }).ToArray());
            Write("GZLE....", card.Concat(new[] { "01-GZLE-Zelda.gci" }).ToArray());
            var rom = Write(SaveTitleReaderTests.GameCubeIso("GALE01", 1), "roms", "Melee", "Super Smash Bros. Melee.iso");

            var target = (UnitSaveTarget)new RetroArchSaveHandler().ResolveTarget(Request(ra, rom));

            Assert.Equal("dolphin", target.EmulatorTag);
            Assert.Equal("Super Smash Bros. Melee.zip", target.FileName);
            Assert.Equal(new[] { "01-GALE-SuperSmashBros.gci" }, target.Unit.CurrentEntries().Select(e => e.EntryName));
        }

        [Fact]
        public void RetroArch_dolphin_wii_save_is_the_titles_folder_under_its_low_word()
        {
            var ra = RetroArch();
            var rom = Write(SaveTitleReaderTests.WiiRvz("RMGE01"), "roms", "Galaxy.rvz");

            var target = (UnitSaveTarget)new RetroArchSaveHandler().ResolveTarget(Request(ra, rom));

            var unit = (OwnFolderUnit)target.Unit;
            Assert.Equal(P("RetroArch", "saves", "dolphin-emu", "User", "Wii", "title", "00010000", "524d4745"), unit.Root);
            Assert.Equal("524d4745", unit.ArchiveFolder);
        }

        [Fact]
        public void RetroArch_iso_that_is_no_dolphin_disc_stays_an_srm()
        {
            var ra = RetroArch();
            var rom = Write(new byte[0x8000], "roms", "psp.iso");

            var target = new RetroArchSaveHandler().ResolveTarget(Request(ra, rom));

            Assert.Equal("psp.srm", target.FileName);
        }
    }
}
