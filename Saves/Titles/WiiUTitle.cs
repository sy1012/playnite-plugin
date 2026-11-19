using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace RomM.Saves.Titles
{
    /// <summary>
    /// Names the Wii U title Cemu runs for a piece of content, the way Tender's resolver
    /// (emu-atlas atlas/wua.py) does: a <c>.wua</c> archive's top-level title folders
    /// (<c>&lt;title id&gt;_v&lt;version&gt;</c>, read from the uncompressed ZArchive index), or a
    /// folder dump's <c>code/app.xml</c>. Exactly one bootable title must be named.
    /// </summary>
    internal static class WiiUTitle
    {
        private const int FooterSize = 144;
        private const uint FooterMagic = 0x169F52D6;
        private const uint FooterVersion = 0x61BF3A01;
        private const uint FileFlag = 0x80000000;
        private const uint NameMask = 0x7FFFFFFF;
        private const int MaxTopLevel = 4096;
        private static readonly byte[] BootableTypes = { 0x00, 0x02, 0x10, 0x30 };

        /// <summary>The title id, or null when it is not established.</summary>
        public static ulong? TitleId(string contentPath)
        {
            if (string.IsNullOrEmpty(contentPath))
                return null;

            try
            {
                if (File.Exists(contentPath) &&
                    string.Equals(Path.GetExtension(contentPath), ".wua", StringComparison.OrdinalIgnoreCase))
                {
                    using (var f = File.OpenRead(contentPath))
                    {
                        var bootable = ReadWuaTitles(f).Where(IsBootable).Distinct().ToList();
                        return bootable.Count == 1 ? bootable[0] : (ulong?)null;
                    }
                }

                var appXml = FindAppXml(contentPath);
                return appXml == null ? null : FromAppXml(File.ReadAllText(appXml));
            }
            catch (InvalidDataException) { return null; }
            catch (IOException) { return null; }
            catch (System.Xml.XmlException) { return null; }
        }

        public static string HighWord(ulong titleId) => (titleId >> 32).ToString("x8");
        public static string LowWord(ulong titleId) => (titleId & 0xFFFFFFFF).ToString("x8");

        private static bool IsBootable(ulong titleId) => BootableTypes.Contains((byte)((titleId >> 32) & 0xFF));

        /// <summary><c>code/app.xml</c> of the dump an <c>.rpx</c> (or the dump folder itself) belongs to.</summary>
        private static string FindAppXml(string contentPath)
        {
            string dump;
            if (Directory.Exists(contentPath))
                dump = contentPath;
            else if (string.Equals(Path.GetExtension(contentPath), ".rpx", StringComparison.OrdinalIgnoreCase))
                dump = Path.GetDirectoryName(Path.GetDirectoryName(contentPath));
            else
                return null;

            if (string.IsNullOrEmpty(dump))
                return null;
            var appXml = Path.Combine(dump, "code", "app.xml");
            return File.Exists(appXml) ? appXml : null;
        }

        internal static ulong? FromAppXml(string xml)
        {
            var value = XDocument.Parse(xml).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "title_id")?.Value?.Trim();
            ulong id;
            if (value != null && value.Length == 16 &&
                ulong.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id))
                return id;
            return null;
        }

        internal static IList<ulong> ReadWuaTitles(Stream f)
        {
            long size = f.Length;
            if (size <= FooterSize)
                throw new InvalidDataException("shorter than a ZArchive footer");
            var footer = At(f, size - FooterSize, FooterSize);
            var totalSize = BE64(footer, 6 * 16 + 32);
            var version = BE32(footer, 6 * 16 + 40);
            var magic = BE32(footer, 6 * 16 + 44);
            if (magic != FooterMagic || version != FooterVersion)
                throw new InvalidDataException("no ZArchive footer");
            if (totalSize != (ulong)size)
                throw new InvalidDataException("ZArchive size mismatch");

            ulong namesOffset = BE64(footer, 2 * 16), namesSize = BE64(footer, 2 * 16 + 8);
            ulong treeOffset = BE64(footer, 3 * 16), treeSize = BE64(footer, 3 * 16 + 8);
            if (namesOffset + namesSize > (ulong)size || treeOffset + treeSize > (ulong)size ||
                treeSize == 0 || treeSize % 16 != 0)
                throw new InvalidDataException("ZArchive sections out of bounds");

            var root = At(f, (long)treeOffset, 16);
            var rootFlag = BE32(root, 0);
            var start = BE32(root, 4);
            var children = BE32(root, 8);
            if ((rootFlag & FileFlag) != 0 || children > MaxTopLevel || (ulong)(start + children) > treeSize / 16)
                throw new InvalidDataException("ZArchive root not read");

            var titles = new List<ulong>();
            var block = At(f, (long)treeOffset + start * 16L, (int)children * 16);
            for (int i = 0; i < children; i++)
            {
                var flag = BE32(block, i * 16);
                if ((flag & FileFlag) != 0)
                    continue;
                var name = ReadName(f, (long)namesOffset, (long)namesSize, flag & NameMask);
                var title = ParseTitleFolder(name);
                if (title.HasValue)
                    titles.Add(title.Value);
            }
            return titles;
        }

        private static string ReadName(Stream f, long namesOffset, long namesSize, uint offset)
        {
            if (offset == NameMask || offset >= namesSize)
                return null;
            int header = At(f, namesOffset + offset, 1)[0];
            int length = header & 0x7F;
            long pos = offset;
            if ((header & 0x80) != 0)
            {
                if (pos + 1 >= namesSize)
                    return null;
                length = (length | (header << 7)) & 0xFFFF;
                pos += 2;
            }
            else
            {
                pos += 1;
            }
            if (length == 0 || length > 255 || pos + length > namesSize)
                return null;
            return Encoding.ASCII.GetString(At(f, namesOffset + pos, length));
        }

        /// <summary><c>&lt;16 hex&gt;_v&lt;version&gt;</c>, as Cemu's ParseWuaTitleFolderName reads it.</summary>
        internal static ulong? ParseTitleFolder(string name)
        {
            if (name == null || name.Length < 19 || name.Substring(16, 2) != "_v")
                return null;
            ulong id;
            if (!ulong.TryParse(name.Substring(0, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id))
                return null;
            var digits = name.Substring(18);
            if (digits.Length > 1 && digits[0] == '0')
                return null;
            uint version;
            if (!digits.All(char.IsDigit) || !uint.TryParse(digits, out version) || version > 0xFFFF)
                return null;
            return id;
        }

        private static byte[] At(Stream f, long offset, int count)
        {
            f.Seek(offset, SeekOrigin.Begin);
            var data = SwitchTitle.ReadExact(f, count);
            if (data == null)
                throw new InvalidDataException("file ends early");
            return data;
        }

        internal static uint BE32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
        internal static ulong BE64(byte[] b, int o) => (ulong)BE32(b, o) << 32 | BE32(b, o + 4);
    }
}
