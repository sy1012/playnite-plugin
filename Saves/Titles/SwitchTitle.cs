using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RomM.Saves.Titles
{
    /// <summary>
    /// Names a Switch game's application id -- the id its save folder is keyed by -- from its
    /// content, the way Tender's resolver (emu-atlas atlas/switch_title.py) does: a
    /// <c>[01…]</c> tag in the file name, else the ticket names an NSP/NSZ header lists, else the
    /// tags of the other files in its folder. An update's or add-on's id is taken back to its
    /// application. XCI headers are encrypted and not read.
    /// </summary>
    internal static class SwitchTitle
    {
        private static readonly Regex TitleTag = new Regex(@"\[(01[0-9A-Fa-f]{14})\]");
        private static readonly Regex TicketName = new Regex(@"^([0-9A-Fa-f]{32})\.tik$", RegexOptions.IgnoreCase);
        private const int Pfs0HeaderLimit = 1 << 20;

        /// <summary>The application id as sixteen uppercase hex digits, or null when it is not established.</summary>
        public static string ApplicationId(string contentPath)
        {
            if (string.IsNullOrEmpty(contentPath))
                return null;

            var fromName = Single(TaggedIds(Path.GetFileName(contentPath)));
            if (fromName != null)
                return fromName;

            var ext = Path.GetExtension(contentPath).ToLowerInvariant();
            if ((ext == ".nsp" || ext == ".nsz") && File.Exists(contentPath))
            {
                try
                {
                    using (var f = File.OpenRead(contentPath))
                    {
                        var fromTickets = Single(TicketIds(ReadPfs0Names(f)));
                        if (fromTickets != null)
                            return fromTickets;
                    }
                }
                catch (InvalidDataException) { }
                catch (IOException) { }
            }

            var folder = Path.GetDirectoryName(contentPath);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                var siblings = Directory.EnumerateFiles(folder).Select(Path.GetFileName).SelectMany(TaggedIds);
                return Single(siblings);
            }

            return null;
        }

        private static string Single(IEnumerable<ulong> titleIds)
        {
            var apps = titleIds.Select(ToApplication).Where(a => a.HasValue).Select(a => a.Value).Distinct().ToList();
            return apps.Count == 1 ? apps[0].ToString("X16") : null;
        }

        internal static IEnumerable<ulong> TaggedIds(string name)
        {
            foreach (Match m in TitleTag.Matches(name ?? string.Empty))
                yield return ulong.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        internal static IEnumerable<ulong> TicketIds(IEnumerable<string> names)
        {
            foreach (var name in names)
            {
                var m = TicketName.Match(name);
                if (m.Success)
                    yield return ulong.Parse(m.Groups[1].Value.Substring(0, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// The application an id belongs to: itself, its patch's (+0x800) or delta's (+0xC00)
        /// parent, or an add-on's ((app &amp; ~0xFFF) + 0x1000 + index 1..2000). Null for none.
        /// </summary>
        internal static ulong? ToApplication(ulong titleId)
        {
            var low = titleId & 0xFFF;
            if (low == 0)
                return titleId;
            if (low == 0x800 || low == 0xC00)
                return titleId - low;
            if (low <= 2000)
            {
                var b = titleId - low;
                return b < 0x1000 ? (ulong?)null : b - 0x1000;
            }
            return null;
        }

        /// <summary>The file names a PFS0 package's header lists -- the header and string table only.</summary>
        internal static IList<string> ReadPfs0Names(Stream f)
        {
            var head = ReadExact(f, 16);
            if (head == null || Encoding.ASCII.GetString(head, 0, 4) != "PFS0")
                throw new InvalidDataException("no PFS0 header");
            var count = BitConverter.ToUInt32(head, 4);
            var tableSize = BitConverter.ToUInt32(head, 8);
            long entriesSize = (long)count * 0x18;
            if (16 + entriesSize + tableSize > Pfs0HeaderLimit)
                throw new InvalidDataException("PFS0 header past the bound");
            var body = ReadExact(f, (int)(entriesSize + tableSize));
            if (body == null)
                throw new InvalidDataException("PFS0 header cut short");

            var names = new List<string>();
            for (int i = 0; i < count; i++)
            {
                var offset = BitConverter.ToUInt32(body, i * 0x18 + 0x10);
                if (offset >= tableSize)
                    throw new InvalidDataException("PFS0 name outside the string table");
                int start = (int)(entriesSize + offset);
                int end = start;
                while (end < body.Length && body[end] != 0)
                    end++;
                names.Add(Encoding.UTF8.GetString(body, start, end - start));
            }
            return names;
        }

        internal static byte[] ReadExact(Stream f, int count)
        {
            var buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = f.Read(buffer, read, count - read);
                if (n <= 0)
                    return null;
                read += n;
            }
            return buffer;
        }
    }
}
