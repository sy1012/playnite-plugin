using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RomM.Saves.Titles;
using Xunit;

namespace RomM.Tests
{
    public class SaveTitleReaderTests : IDisposable
    {
        private readonly string _dir;

        public SaveTitleReaderTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string P(params string[] parts) => Path.Combine(new[] { _dir }.Concat(parts).ToArray());

        private string WriteBytes(byte[] bytes, params string[] parts)
        {
            var path = P(parts);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, bytes);
            return path;
        }

        // ---- Switch ----

        [Fact]
        public void Switch_id_comes_from_the_file_name_tag()
        {
            var path = WriteBytes(new byte[0], "Snipperclips [01008E20047DC000][JP][v0].nsp");
            Assert.Equal("01008E20047DC000", SwitchTitle.ApplicationId(path));
        }

        [Theory]
        [InlineData(0x01008E20047DC800UL, 0x01008E20047DC000UL)] // update
        [InlineData(0x01008E20047DD001UL, 0x01008E20047DC000UL)] // first add-on
        [InlineData(0x01008E20047DC000UL, 0x01008E20047DC000UL)] // application
        public void Switch_updates_and_addons_map_back_to_their_application(ulong id, ulong app)
        {
            Assert.Equal(app, SwitchTitle.ToApplication(id));
        }

        [Fact]
        public void Switch_id_comes_from_the_nsp_ticket_name_without_a_tag()
        {
            var names = new[] { "abc.nca", "0100F2C0115B60000000000000000005.tik" };
            var path = WriteBytes(Pfs0(names), "game.nsp");
            Assert.Equal("0100F2C0115B6000", SwitchTitle.ApplicationId(path));
        }

        [Fact]
        public void Switch_id_comes_from_sibling_files_in_a_multi_file_game()
        {
            WriteBytes(new byte[0], "Game", "Game [0100F2C0115B6800][v65536].nsp");
            var base_ = WriteBytes(new byte[0], "Game", "Game.xci");
            Assert.Equal("0100F2C0115B6000", SwitchTitle.ApplicationId(base_));
        }

        private static byte[] Pfs0(IList<string> names)
        {
            var table = new MemoryStream();
            var offsets = new List<int>();
            foreach (var n in names)
            {
                offsets.Add((int)table.Length);
                var b = Encoding.UTF8.GetBytes(n);
                table.Write(b, 0, b.Length);
                table.WriteByte(0);
            }
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("PFS0"));
            w.Write((uint)names.Count);
            w.Write((uint)table.Length);
            w.Write(0u);
            foreach (var o in offsets)
            {
                w.Write(0UL); w.Write(0UL); w.Write((uint)o); w.Write(0u);
            }
            w.Write(table.ToArray());
            return ms.ToArray();
        }

        // ---- Wii U ----

        [Fact]
        public void WiiU_id_comes_from_the_bootable_title_folder_of_a_wua()
        {
            var path = WriteBytes(Wua("0005000010101a00_v0", "0005000e10101a00_v16"), "game.wua");
            Assert.Equal(0x0005000010101A00UL, WiiUTitle.TitleId(path));
        }

        [Fact]
        public void WiiU_wua_with_two_bootable_titles_names_none()
        {
            var path = WriteBytes(Wua("0005000010101a00_v0", "0005000010102b00_v0"), "two.wua");
            Assert.Null(WiiUTitle.TitleId(path));
        }

        [Fact]
        public void WiiU_id_comes_from_a_dumps_app_xml()
        {
            WriteBytes(Encoding.UTF8.GetBytes(
                "<?xml version=\"1.0\"?><app type=\"complex\"><title_id type=\"hexBinary\" length=\"8\">0005000010145D00</title_id></app>"),
                "Dump", "code", "app.xml");
            var rpx = WriteBytes(new byte[0], "Dump", "code", "game.rpx");
            Assert.Equal(0x0005000010145D00UL, WiiUTitle.TitleId(rpx));
        }

        /// <summary>A ZArchive holding only the named top-level folders.</summary>
        private static byte[] Wua(params string[] folders)
        {
            var names = new MemoryStream();
            var nameOffsets = new List<uint>();
            foreach (var f in folders)
            {
                nameOffsets.Add((uint)names.Length);
                names.WriteByte((byte)f.Length);
                var b = Encoding.ASCII.GetBytes(f);
                names.Write(b, 0, b.Length);
            }
            var tree = new MemoryStream();
            BE(tree, 0x7FFFFFFF); BE(tree, 1); BE(tree, (uint)folders.Length); BE(tree, 0);
            foreach (var o in nameOffsets)
            {
                BE(tree, o); BE(tree, 0); BE(tree, 0); BE(tree, 0);
            }

            var file = new MemoryStream();
            var records = new byte[40];
            file.Write(records, 0, records.Length);
            long namesAt = file.Length; file.Write(names.ToArray(), 0, (int)names.Length);
            long treeAt = file.Length; file.Write(tree.ToArray(), 0, (int)tree.Length);
            long total = file.Length + 144;
            BE64(file, 0); BE64(file, 0);
            BE64(file, 0); BE64(file, 40);
            BE64(file, (ulong)namesAt); BE64(file, (ulong)names.Length);
            BE64(file, (ulong)treeAt); BE64(file, (ulong)tree.Length);
            BE64(file, 0); BE64(file, 0);
            BE64(file, 0); BE64(file, 0);
            file.Write(new byte[32], 0, 32);
            BE64(file, (ulong)total);
            BE(file, 0x61BF3A01);
            BE(file, 0x169F52D6);
            return file.ToArray();
        }

        private static void BE(Stream s, uint v)
        {
            s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
        }

        private static void BE64(Stream s, ulong v)
        {
            BE(s, (uint)(v >> 32)); BE(s, (uint)v);
        }

        // ---- PS3 ----

        internal static byte[] Sfo(string titleId)
        {
            var key = Encoding.ASCII.GetBytes("TITLE_ID\0");
            var data = Encoding.ASCII.GetBytes(titleId + "\0");
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            int keyTable = 0x14 + 16, dataTable = keyTable + 12;
            w.Write(new byte[] { 0, (byte)'P', (byte)'S', (byte)'F' });
            w.Write(0x0101); w.Write(keyTable); w.Write(dataTable); w.Write(1);
            w.Write((ushort)0); w.Write((ushort)0x0204); w.Write(data.Length); w.Write(16); w.Write(0);
            w.Write(key); w.Write(new byte[12 - key.Length]);
            w.Write(data); w.Write(new byte[16 - data.Length]);
            return ms.ToArray();
        }

        [Fact]
        public void Ps3_id_comes_from_a_folder_dumps_param_sfo_via_its_eboot()
        {
            WriteBytes(Sfo("BLUS30443"), "Demon", "PS3_GAME", "PARAM.SFO");
            var eboot = WriteBytes(Encoding.ASCII.GetBytes("SCE\0rest"), "Demon", "PS3_GAME", "USRDIR", "EBOOT.BIN");

            Assert.Equal("BLUS30443", Ps3Title.TitleId(eboot));
            Assert.Equal("BLUS30443", Ps3Title.TitleId(P("Demon")));
        }

        [Fact]
        public void Ps3_id_comes_from_a_psn_package_header()
        {
            var head = new byte[0x30 + 48];
            Encoding.ASCII.GetBytes("\u007fPKG").CopyTo(head, 0);
            head[4] = 0x80; head[5] = 0x00; head[6] = 0x00; head[7] = 0x01;
            Encoding.ASCII.GetBytes("UP0006-NPUB30058_00-0000000000000000").CopyTo(head, 0x30);
            var pkg = WriteBytes(head, "game.pkg");

            Assert.Equal("NPUB30058", Ps3Title.TitleId(pkg));
        }

        [Fact]
        public void Ps3_id_of_another_shape_is_not_taken()
        {
            WriteBytes(Sfo("not-an-id"), "X", "PS3_GAME", "PARAM.SFO");
            Assert.Null(Ps3Title.TitleId(P("X")));
        }

        // ---- GameCube / Wii ----

        internal static byte[] GameCubeIso(string gameId, uint regionCode)
        {
            var disc = new byte[0x460];
            Encoding.ASCII.GetBytes(gameId).CopyTo(disc, 0);
            new byte[] { 0xC2, 0x33, 0x9F, 0x3D }.CopyTo(disc, 0x1C);
            disc[0x458] = (byte)(regionCode >> 24); disc[0x459] = (byte)(regionCode >> 16);
            disc[0x45A] = (byte)(regionCode >> 8); disc[0x45B] = (byte)regionCode;
            return disc;
        }

        internal static byte[] WiiRvz(string gameId)
        {
            var file = new byte[0x48 + 0x10 + 0x80];
            Encoding.ASCII.GetBytes("RVZ\u0001").CopyTo(file, 0);
            uint h2 = 0xDC;
            file[12] = (byte)(h2 >> 24); file[13] = (byte)(h2 >> 16); file[14] = (byte)(h2 >> 8); file[15] = (byte)h2;
            Encoding.ASCII.GetBytes(gameId).CopyTo(file, 0x48 + 0x10);
            new byte[] { 0x5D, 0x1C, 0x9E, 0xA3 }.CopyTo(file, 0x48 + 0x10 + 0x18);
            return file;
        }

        [Fact]
        public void GameCube_disc_names_its_game_code_and_region_folder()
        {
            var disc = DolphinDisc.Read(WriteBytes(GameCubeIso("GALE01", 1), "melee.iso"));

            Assert.False(disc.IsWii);
            Assert.Equal("GALE", disc.GameCode);
            Assert.Equal("USA", disc.GameCubeRegionFolder);
        }

        [Fact]
        public void Wii_rvz_names_its_title_low_word()
        {
            var disc = DolphinDisc.Read(WriteBytes(WiiRvz("RMGE01"), "galaxy.rvz"));

            Assert.True(disc.IsWii);
            Assert.Equal("524d4745", disc.WiiLowWord);
        }

        [Fact]
        public void A_psp_iso_is_no_dolphin_disc()
        {
            Assert.Null(DolphinDisc.Read(WriteBytes(new byte[0x8000], "psp.iso")));
        }
    }
}
