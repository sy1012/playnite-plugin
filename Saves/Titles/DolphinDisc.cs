using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace RomM.Saves.Titles
{
    /// <summary>
    /// What a GameCube or Wii disc image says about itself that keys its saves -- the game id and,
    /// for GameCube, the region -- read through the image's container the way Tender's resolver
    /// (emu-atlas atlas/dolphin_disc.py) reads it: plain ISO/GCM/NKit, CISO, GCZ, WBFS, and the
    /// uncompressed disc-header copy of WIA and RVZ.
    /// </summary>
    internal sealed class DolphinDisc
    {
        public bool IsWii { get; private set; }

        /// <summary>The six-character game id, filtered as Dolphin filters it.</summary>
        public string GameId { get; private set; }

        /// <summary>The first four characters of <see cref="GameId"/>, which a .gci header carries.</summary>
        public string GameCode => GameId.Substring(0, 4);

        /// <summary>For GameCube: "USA", "EUR" or "JAP", Dolphin's card folder; null where not established.</summary>
        public string GameCubeRegionFolder { get; private set; }

        /// <summary>A Wii disc title's low word: the game code's four bytes as lowercase hex.</summary>
        public string WiiLowWord
        {
            get
            {
                var sb = new StringBuilder();
                foreach (var b in Encoding.ASCII.GetBytes(GameId.Substring(0, 4)))
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private delegate byte[] DiscRead(long offset, int length);

        /// <summary>The disc's facts, or null when the file is no GameCube or Wii image read here.</summary>
        public static DolphinDisc Read(string path)
        {
            try
            {
                using (var f = File.OpenRead(path))
                    return Read(f);
            }
            catch (IOException) { return null; }
            catch (InvalidDataException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        internal static DolphinDisc Read(Stream f)
        {
            var read = Container(f);
            if (read == null)
                return null;
            var header = read(0, 0x20);
            if (header == null)
                return null;

            bool wii = WiiUTitle.BE32(header, 0x18) == 0x5D1C9EA3;
            bool gc = WiiUTitle.BE32(header, 0x1C) == 0xC2339F3D;
            if (!wii && !gc)
                return null;

            var id = new char[6];
            for (int i = 0; i < 6; i++)
            {
                var c = (char)header[i];
                id[i] = c < 128 && char.IsLetterOrDigit(c) ? c : '-';
            }

            var disc = new DolphinDisc { IsWii = wii, GameId = new string(id) };
            if (gc)
            {
                var code = read(0x458, 4);
                int? region = code == null ? (int?)null : (int)WiiUTitle.BE32(code, 0);
                disc.GameCubeRegionFolder = RegionFolder(region, header[3], header[7]);
            }
            return disc;
        }

        private static string RegionFolder(int? regionCode, byte country, byte revision)
        {
            int? region = null;
            if (regionCode.HasValue)
            {
                if (regionCode == 0 || regionCode == 4) region = 0;
                else if (regionCode == 1) region = 1;
                else if (regionCode == 2) region = 2;
            }
            else
            {
                var c = (char)country;
                if (c == 'E') region = revision >= 0x30 ? 0 : 1;
                else if ("JKQT".IndexOf(c) >= 0) region = 0;
                else if ("BN".IndexOf(c) >= 0) region = 1;
                else if ("DFHILMPRSUV".IndexOf(c) >= 0) region = 2;
            }

            if (region == 0) return "JAP";
            if (region == 1) return "USA";
            if (region == 2) return "EUR";
            return null;
        }

        private static DiscRead Container(Stream f)
        {
            long size = f.Length;
            var magic = At(f, 0, 4, size);
            if (magic == null)
                return null;
            var m = Encoding.ASCII.GetString(magic);

            if (m == "CISO")
                return Ciso(f, size);
            if (BitConverter.ToUInt32(magic, 0) == 0xB10BC001)
                return Gcz(f, size);
            if (m == "WBFS")
                return Wbfs(f, size);
            if (m == "WIA\u0001" || m == "RVZ\u0001")
                return Wia(f, size);
            return (o, l) => At(f, o, l, size);
        }

        private static DiscRead Blocked(long blockSize, long dataSize, Func<long, long, int, byte[]> piece)
        {
            return (offset, length) =>
            {
                if (offset + length > dataSize)
                    return null;
                var output = new byte[length];
                int done = 0;
                while (done < length)
                {
                    long block = (offset + done) / blockSize, within = (offset + done) % blockSize;
                    int take = (int)Math.Min(blockSize - within, length - done);
                    var data = piece(block, within, take);
                    if (data == null)
                        return null;
                    Buffer.BlockCopy(data, 0, output, done, take);
                    done += take;
                }
                return output;
            };
        }

        private static DiscRead Ciso(Stream f, long size)
        {
            var header = At(f, 0, 0x8000, size);
            if (header == null)
                return null;
            long blockSize = BitConverter.ToUInt32(header, 4);
            if (blockSize == 0)
                return null;
            const int mapSize = 0x8000 - 8;
            return Blocked(blockSize, mapSize * blockSize, (block, within, length) =>
            {
                if (block >= mapSize || header[8 + block] != 1)
                    return new byte[length];
                long stored = 0;
                for (int i = 0; i < block; i++)
                    if (header[8 + i] == 1) stored++;
                return At(f, 0x8000 + stored * blockSize + within, length, size);
            });
        }

        private static DiscRead Gcz(Stream f, long size)
        {
            var h = At(f, 0, 32, size);
            if (h == null)
                return null;
            long compressedSize = (long)BitConverter.ToUInt64(h, 8);
            long discSize = (long)BitConverter.ToUInt64(h, 16);
            long blockSize = BitConverter.ToUInt32(h, 24);
            long blocks = BitConverter.ToUInt32(h, 28);
            long dataOffset = 32 + blocks * 12;
            if (blocks == 0 || blockSize == 0 || blockSize > 1 << 24 || dataOffset + compressedSize > size)
                return null;
            const ulong raw = 1UL << 63;

            Func<long, ulong> pointer = b => BitConverter.ToUInt64(At(f, 32 + b * 8, 8, size), 0);

            return Blocked(blockSize, discSize, (block, within, length) =>
            {
                if (block >= blocks)
                    return null;
                ulong start = pointer(block);
                long end = block < blocks - 1 ? (long)(pointer(block + 1) & ~raw) : compressedSize;
                long stored = (long)(start & ~raw);
                long len = end - stored;
                bool isRaw = (start & raw) != 0;
                if (len < 0 || len > blockSize + 64 || (isRaw && len != blockSize))
                    return null;
                var data = At(f, dataOffset + stored, (int)len, size);
                if (data == null)
                    return null;
                byte[] whole = data;
                if (!isRaw)
                {
                    if (data.Length < 2)
                        return null;
                    whole = new byte[blockSize];
                    using (var z = new DeflateStream(new MemoryStream(data, 2, data.Length - 2), CompressionMode.Decompress))
                    {
                        if (SwitchTitle.ReadExact(z, (int)blockSize) is byte[] inflated)
                            whole = inflated;
                        else
                            return null;
                    }
                }
                var slice = new byte[length];
                Buffer.BlockCopy(whole, (int)within, slice, 0, length);
                return slice;
            });
        }

        private static DiscRead Wbfs(Stream f, long size)
        {
            var h = At(f, 0, 13, size);
            if (h == null)
                return null;
            long hdSectors = WiiUTitle.BE32(h, 4);
            long hdSector = 1L << h[8], wbfsSector = 1L << h[9];
            if (wbfsSector < 0x8000 || size > hdSectors * hdSector || h[12] == 0)
                return null;
            const long wiiDiscSize = 143432L * 2 * 0x8000;
            long clusters = (wiiDiscSize + wbfsSector - 1) / wbfsSector;
            return Blocked(wbfsSector, wiiDiscSize, (cluster, within, length) =>
            {
                if (cluster >= clusters)
                    return null;
                var entry = At(f, hdSector + 256 + cluster * 2, 2, size);
                if (entry == null)
                    return null;
                long stored = (entry[0] << 8 | entry[1]) * wbfsSector + within;
                return At(f, stored, length, size);
            });
        }

        /// <summary>WIA and RVZ: only the 0x80-byte disc header copy in header 2 is uncompressed.</summary>
        private static DiscRead Wia(Stream f, long size)
        {
            var h1 = At(f, 0, 0x48, size);
            if (h1 == null)
                return null;
            long header2Size = WiiUTitle.BE32(h1, 12);
            if (header2Size < 0x10 + 0x80)
                return null;
            var h2 = At(f, 0x48, (int)Math.Min(header2Size, 0x10 + 0x80), size);
            if (h2 == null)
                return null;
            var discHeader = new byte[0x80];
            Buffer.BlockCopy(h2, 0x10, discHeader, 0, 0x80);
            return (offset, length) =>
            {
                if (offset + length > 0x80)
                    return null;
                var slice = new byte[length];
                Buffer.BlockCopy(discHeader, (int)offset, slice, 0, length);
                return slice;
            };
        }

        private static byte[] At(Stream f, long offset, int length, long size)
        {
            if (offset < 0 || offset + length > size)
                return null;
            f.Seek(offset, SeekOrigin.Begin);
            return SwitchTitle.ReadExact(f, length);
        }
    }
}
