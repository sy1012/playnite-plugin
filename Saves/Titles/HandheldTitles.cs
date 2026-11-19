using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace RomM.Saves.Titles
{
    /// <summary>
    /// The ids a 3DS card image keys its saves by, read the way Tender's resolver (emu-atlas
    /// atlas/n3ds_rom.py, Azahar 2126.1.1) reads them: the program id from the NCCH header, and
    /// the extdata id the extended header grants. Only plain NCSD (.3ds/.cci) and NCCH (.cxi)
    /// images are read; a CIA, a compressed or an encrypted image names nothing.
    /// </summary>
    internal sealed class N3dsRom
    {
        public ulong ProgramId { get; private set; }

        /// <summary>The extdata id, or null where the image grants none.</summary>
        public ulong? ExtdataId { get; private set; }

        public static N3dsRom Read(string path)
        {
            try
            {
                using (var f = File.OpenRead(path))
                    return Read(f);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        internal static N3dsRom Read(Stream f)
        {
            const int programRead = 0x200 + 0x230 + 0x20;
            var head = At(f, 0, programRead);
            if (head == null || head.Length < 0x104)
                return null;

            var magic = Encoding.ASCII.GetString(head, 0x100, 4);
            if (magic == "NCCH")
                return Program(head);
            if (magic != "NCSD")
                return null;

            long offset = BitConverter.ToUInt32(head, 0x120);
            long length = BitConverter.ToUInt32(head, 0x124);
            if (offset == 0 || length == 0)
                return null;
            var program = At(f, offset * 0x200, programRead);
            if (program == null || Encoding.ASCII.GetString(program, 0x100, 4) != "NCCH")
                return null;
            return Program(program);
        }

        private static N3dsRom Program(byte[] data)
        {
            if (data.Length < 0x200 || (data[0x18F] & 0x04) == 0)
                return null; // encrypted: Azahar does not load it

            var rom = new N3dsRom { ProgramId = BitConverter.ToUInt64(data, 0x118) };
            var exheaderSize = BitConverter.ToUInt32(data, 0x180);
            if (exheaderSize == 0 || data.Length < 0x200 + 0x230 + 0x20)
                return rom;

            int storage = 0x200 + 0x230;
            ulong extSaveDataId = BitConverter.ToUInt64(data, storage);
            ulong accessible = BitConverter.ToUInt64(data, storage + 0x10);
            byte otherAttributes = data[storage + 0x1F];

            if ((otherAttributes >> 1) != 0)
            {
                foreach (var word in new[] { accessible, extSaveDataId })
                {
                    foreach (var shift in new[] { 40, 20, 0 })
                    {
                        var candidate = (word >> shift) & 0xFFFFF;
                        if (candidate != 0)
                        {
                            rom.ExtdataId = candidate;
                            return rom;
                        }
                    }
                }
                return rom;
            }

            if (extSaveDataId != 0)
                rom.ExtdataId = extSaveDataId;
            return rom;
        }

        private static byte[] At(Stream f, long offset, int count)
        {
            if (offset >= f.Length)
                return null;
            f.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[count];
            int read = 0, n;
            while (read < count && (n = f.Read(buffer, read, count - read)) > 0)
                read += n;
            if (read == count)
                return buffer;
            var cut = new byte[read];
            Buffer.BlockCopy(buffer, 0, cut, 0, read);
            return cut;
        }
    }

    /// <summary>
    /// A PSP disc's game id -- the prefix of its savedata folders -- read as Tender's resolver
    /// (emu-atlas atlas/psp_disc.py) reads it: <c>DISC_ID</c> of the plain ISO's
    /// <c>PSP_GAME/PARAM.SFO</c>, dash removed. A CSO, PBP or CHD is not read.
    /// </summary>
    internal static class PspDisc
    {
        private static readonly Regex GameIdShape = new Regex("^[A-Z]{4}[0-9]{5}$");

        /// <summary>Keys only a savedata write sets; a folder of installed game data carries neither.</summary>
        public static readonly string[] SavedataKeys = { "SAVEDATA_FILE_LIST", "SAVEDATA_PARAMS" };

        public static string GameId(string path)
        {
            try
            {
                using (var f = File.OpenRead(path))
                {
                    var head = SwitchTitle.ReadExact(f, 4);
                    if (head == null || Encoding.ASCII.GetString(head) == "CISO" || Encoding.ASCII.GetString(head, 1, 3) == "PBP")
                        return null;
                    var sfo = Iso9660.FileBytes(f, new[] { "PSP_GAME", "PARAM.SFO" }, 1 << 20);
                    if (sfo == null)
                        return null;
                    string id;
                    if (!Ps3Title.SfoStrings(sfo).TryGetValue("DISC_ID", out id) || string.IsNullOrEmpty(id))
                        return null;
                    if (id.Length == 10 && id[4] == '-')
                        id = id.Remove(4, 1);
                    return GameIdShape.IsMatch(id) ? id : null;
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        /// <summary>
        /// Whether a SAVEDATA folder is a save: one with no PARAM.SFO is (an install always writes
        /// one), one whose PARAM.SFO carries a savedata key is, and one without is installed data.
        /// An unreadable PARAM.SFO is treated as a save, so it is never silently left behind.
        /// </summary>
        public static bool IsSaveFolder(string folder)
        {
            var sfo = Path.Combine(folder, "PARAM.SFO");
            if (!File.Exists(sfo))
                return true;
            try
            {
                var keys = Ps3Title.SfoKeys(File.ReadAllBytes(sfo));
                return keys.Count == 0 || SavedataKeys.Any(keys.Contains);
            }
            catch (IOException)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// A PS2 disc's serial, read as Tender's resolver (emu-atlas atlas/ps2_disc.py, PCSX2 v2.8.2)
    /// reads it: the <c>BOOT2</c> line of a flat image's <c>SYSTEM.CNF</c>,
    /// <c>cdrom0:\SLUS_201.52;1</c> spelled <c>SLUS-20152</c>. A CHD, CSO, ZSO or gzipped image is
    /// not read. PCSX2's GameDB can name another serial a game's memory card folders are filed
    /// under (memcardFilters); where it names exactly one, that one is the game's.
    /// pcsx2_memcard_filters.json is emu-atlas's table (atlas/data, sy1012/emu-atlas a97fb7d),
    /// generated from PCSX2's GameIndex.yaml at fd9d310c, copied unchanged so both clients pick
    /// the same folders.
    /// </summary>
    internal static class Ps2Disc
    {
        private static readonly Regex ProductSerial = new Regex("^[A-Z]{4}-[0-9]{5}$");
        private static readonly Regex SerialMask = new Regex(@"^.{4}[_-].{3}\..{2,}$");
        private static readonly string[] NotRead = { ".chd", ".cso", ".zso", ".gz" };
        private static readonly int[][] Layouts =
        {
            new[] { 2048, 0, 0 }, new[] { 2336, 8, 0 }, new[] { 2352, 24, 0 }, new[] { 2448, 24, 0 },
            new[] { 2048, 0, 150 * 2048 }, new[] { 2352, 24, 150 * 2048 }, new[] { 2448, 24, 150 * 2048 },
        };

        private static Dictionary<string, string[]> _filters;

        /// <summary>The serial whose folders on a memory card are the game's, or null.</summary>
        public static string CardSerial(string path)
        {
            var serial = DiscSerial(path);
            if (serial == null)
                return null;
            string[] filters;
            if (!Filters().TryGetValue(serial.ToLowerInvariant(), out filters))
                return serial;
            return filters.Length == 1 && ProductSerial.IsMatch(filters[0]) ? filters[0] : null;
        }

        /// <summary>The save folder prefix for <paramref name="serial"/> by its family, or null.</summary>
        public static string RegionLetter(string serial)
        {
            switch (serial.Substring(0, 4))
            {
                case "SLUS": case "SCUS": return "A";
                case "SLES": case "SCES": return "E";
                case "SLPS": case "SLPM": case "SCPS": return "I";
                default: return null;
            }
        }

        internal static string DiscSerial(string path)
        {
            if (NotRead.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                return null;
            try
            {
                using (var f = File.OpenRead(path))
                {
                    var cnf = SystemCnf(f);
                    return cnf == null ? null : SerialFromSystemCnf(Encoding.GetEncoding(28591).GetString(cnf));
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        internal static string SerialFromSystemCnf(string text)
        {
            string boot = null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                var eq = line.IndexOf('=');
                if (eq < 0)
                    continue;
                var key = line.Substring(0, eq).Trim();
                var value = line.Substring(eq + 1).Trim();
                if (key == "BOOT2" && value.Length > 0)
                    boot = value;
                else if (key == "BOOT" && value.Length > 0)
                    boot = null; // a PlayStation disc
            }
            if (boot == null)
                return null;

            var cut = boot.LastIndexOf('\\');
            if (cut < 0)
                cut = boot.LastIndexOf(':');
            var name = cut >= 0 ? boot.Substring(cut + 1) : boot;
            var semicolon = name.LastIndexOf(';');
            if (semicolon >= 0)
                name = name.Substring(0, semicolon);
            if (!SerialMask.IsMatch(name))
                return null;
            var serial = new string(name.Where(c => c != '.').Select(c => c == '_' ? '-' : char.ToUpperInvariant(c)).ToArray());
            return ProductSerial.IsMatch(serial) ? serial : null;
        }

        private static byte[] SystemCnf(Stream f)
        {
            foreach (var layout in Layouts)
            {
                Func<long, byte[]> block = lsn =>
                {
                    long at = layout[2] + lsn * layout[0] + layout[1];
                    if (at + 2048 > f.Length)
                        return null;
                    f.Seek(at, SeekOrigin.Begin);
                    return SwitchTitle.ReadExact(f, 2048);
                };

                var pvd = block(16);
                if (pvd == null || pvd[0] != 1 || Encoding.ASCII.GetString(pvd, 1, 5) != "CD001")
                    continue;

                long rootLsn = BitConverter.ToUInt32(pvd, 156 + 2);
                long rootSize = BitConverter.ToUInt32(pvd, 156 + 10);
                for (long i = 0; i * 2048 < rootSize && i < 64; i++)
                {
                    var dir = block(rootLsn + i);
                    if (dir == null)
                        return null;
                    int pos = 0;
                    while (pos + 33 <= dir.Length && dir[pos] >= 33)
                    {
                        int len = dir[pos];
                        int nameLen = dir[pos + 32];
                        var recordName = Encoding.ASCII.GetString(dir, pos + 33, Math.Min(nameLen, Math.Max(0, len - 33)));
                        var stop = recordName.IndexOfAny(new[] { ';', '\0' });
                        if (stop >= 0)
                            recordName = recordName.Substring(0, stop);
                        bool isDir = (dir[pos + 25] & 2) != 0;
                        if (!isDir && string.Equals(recordName, "SYSTEM.CNF", StringComparison.OrdinalIgnoreCase))
                        {
                            long lsn = BitConverter.ToUInt32(dir, pos + 2);
                            int size = (int)Math.Min(BitConverter.ToUInt32(dir, pos + 10), 64 * 1024);
                            var output = new MemoryStream();
                            for (long b = 0; output.Length < size; b++)
                            {
                                var data = block(lsn + b);
                                if (data == null)
                                    return null;
                                output.Write(data, 0, (int)Math.Min(2048, size - output.Length));
                            }
                            return output.ToArray();
                        }
                        pos += len;
                    }
                }
                return null;
            }
            return null;
        }

        private static Dictionary<string, string[]> Filters()
        {
            if (_filters != null)
                return _filters;

            var table = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var assembly = typeof(Ps2Disc).Assembly;
            var resource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("pcsx2_memcard_filters.json"));
            if (resource != null)
            {
                using (var stream = assembly.GetManifestResourceStream(resource))
                using (var reader = new StreamReader(stream))
                {
                    var document = JObject.Parse(reader.ReadToEnd());
                    foreach (var entry in (JObject)document["filters"])
                    {
                        var key = entry.Key.ToLowerInvariant();
                        if (!table.ContainsKey(key))
                            table[key] = entry.Value.Select(v => (string)v).ToArray();
                    }
                }
            }
            _filters = table;
            return table;
        }
    }
}
