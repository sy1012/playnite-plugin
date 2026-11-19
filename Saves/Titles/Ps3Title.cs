using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RomM.Saves.Titles
{
    /// <summary>
    /// Names the title id RPCS3 runs a PS3 game under -- the prefix of its savedata folders --
    /// the way Tender's resolver (emu-atlas atlas/ps3_title.py) does: <c>TITLE_ID</c> of the
    /// <c>PARAM.SFO</c> a folder dump boots with, the nine bytes at 55 of a PSN package's header,
    /// or <c>PS3_GAME/PARAM.SFO</c> inside an ISO 9660 image.
    /// </summary>
    internal static class Ps3Title
    {
        private static readonly Regex TitleIdShape = new Regex("^[A-Z]{4}[0-9]{5}$");
        private const int SfoLimit = 1 << 20;

        /// <summary>The title id, or null when it is not established.</summary>
        public static string TitleId(string contentPath)
        {
            if (string.IsNullOrEmpty(contentPath))
                return null;
            try
            {
                var id = Read(contentPath);
                return id != null && TitleIdShape.IsMatch(id) ? id : null;
            }
            catch (InvalidDataException) { return null; }
            catch (IOException) { return null; }
        }

        private static string Read(string contentPath)
        {
            if (Directory.Exists(contentPath))
            {
                var sfo = FirstExisting(Path.Combine(contentPath, "PS3_GAME", "PARAM.SFO"), Path.Combine(contentPath, "PARAM.SFO"));
                return sfo == null ? null : SfoTitleId(File.ReadAllBytes(sfo));
            }

            if (!File.Exists(contentPath))
                return null;

            using (var f = File.OpenRead(contentPath))
            {
                var head = SwitchTitle.ReadExact(f, 4) ?? new byte[0];
                if (Starts(head, 0x7F, (byte)'P', (byte)'K', (byte)'G'))
                    return PkgTitleId(f);
                if (Starts(head, 0, (byte)'P', (byte)'S', (byte)'F'))
                    return SfoTitleId(File.ReadAllBytes(contentPath));
                if (Starts(head, (byte)'S', (byte)'C', (byte)'E', 0) || Starts(head, 0x7F, (byte)'E', (byte)'L', (byte)'F'))
                    return ExecutableTitleId(contentPath);

                var sfoInIso = Iso9660.FileBytes(f, new[] { "PS3_GAME", "PARAM.SFO" }, SfoLimit);
                return sfoInIso == null ? null : SfoTitleId(sfoInIso);
            }
        }

        /// <summary>An EBOOT.BIN in <c>PS3_GAME/USRDIR</c> boots with <c>PS3_GAME/PARAM.SFO</c>.</summary>
        private static string ExecutableTitleId(string executable)
        {
            var usrdir = Path.GetDirectoryName(executable);
            var game = string.IsNullOrEmpty(usrdir) ? null : Path.GetDirectoryName(usrdir);
            if (string.IsNullOrEmpty(game))
                return null;
            var sfo = Path.Combine(game, "PARAM.SFO");
            return File.Exists(sfo) ? SfoTitleId(File.ReadAllBytes(sfo)) : null;
        }

        private static string PkgTitleId(Stream f)
        {
            f.Seek(0, SeekOrigin.Begin);
            var head = SwitchTitle.ReadExact(f, 0x30 + 48);
            if (head == null)
                return null;
            int type = head[4] << 8 | head[5];
            int platform = head[6] << 8 | head[7];
            if ((type != 0x8000 && type != 0x0000) || platform != 0x0001)
                return null;
            int end = 55;
            while (end < 64 && head[end] != 0)
                end++;
            return end == 55 ? null : Encoding.ASCII.GetString(head, 55, end - 55);
        }

        /// <summary><c>TITLE_ID</c> from a PARAM.SFO's key and data tables.</summary>
        internal static string SfoTitleId(byte[] sfo)
        {
            var values = SfoStrings(sfo);
            string id;
            return values.TryGetValue("TITLE_ID", out id) && id.Length > 0 ? id : null;
        }

        internal static Dictionary<string, string> SfoStrings(byte[] sfo)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (sfo == null || sfo.Length < 0x14 || !Starts(sfo, 0, (byte)'P', (byte)'S', (byte)'F'))
                return result;

            int keyTable = BitConverter.ToInt32(sfo, 8);
            int dataTable = BitConverter.ToInt32(sfo, 12);
            int count = BitConverter.ToInt32(sfo, 16);
            for (int i = 0; i < count; i++)
            {
                int e = 0x14 + i * 16;
                if (e + 16 > sfo.Length)
                    break;
                int keyOffset = BitConverter.ToUInt16(sfo, e);
                int format = BitConverter.ToUInt16(sfo, e + 2);
                int length = BitConverter.ToInt32(sfo, e + 4);
                int dataOffset = BitConverter.ToInt32(sfo, e + 12);
                var key = CString(sfo, keyTable + keyOffset, sfo.Length);
                if (key == null || format == 0x0404)
                    continue;
                int start = dataTable + dataOffset;
                if (start < 0 || start + length > sfo.Length)
                    continue;
                result[key] = CString(sfo, start, start + length) ?? string.Empty;
            }
            return result;
        }

        private static string CString(byte[] b, int start, int limit)
        {
            if (start < 0 || start >= b.Length)
                return null;
            int end = start;
            while (end < limit && end < b.Length && b[end] != 0)
                end++;
            return Encoding.UTF8.GetString(b, start, end - start);
        }

        private static bool Starts(byte[] b, params byte[] prefix)
        {
            if (b.Length < prefix.Length)
                return false;
            for (int i = 0; i < prefix.Length; i++)
                if (b[i] != prefix[i])
                    return false;
            return true;
        }

        private static string FirstExisting(params string[] paths)
        {
            foreach (var p in paths)
                if (File.Exists(p))
                    return p;
            return null;
        }
    }

    /// <summary>Looks a file up by path in an ISO 9660 image, reading directory records only.</summary>
    internal static class Iso9660
    {
        private const int Sector = 2048;

        public static byte[] FileBytes(Stream f, IList<string> path, int limit)
        {
            if (f.Length < 0x8000 + Sector)
                return null;
            var pvd = At(f, 0x8000, Sector);
            if (pvd == null || pvd[0] != 1 || Encoding.ASCII.GetString(pvd, 1, 5) != "CD001")
                return null;

            long extent = BitConverter.ToUInt32(pvd, 156 + 2);
            long size = BitConverter.ToUInt32(pvd, 156 + 10);

            for (int i = 0; i < path.Count; i++)
            {
                bool last = i == path.Count - 1;
                var found = Find(f, extent, size, path[i], wantDirectory: !last);
                if (found == null)
                    return null;
                extent = found[0];
                size = found[1];
            }

            if (size > limit)
                return null;
            return At(f, extent * Sector, (int)size);
        }

        private static long[] Find(Stream f, long extent, long size, string name, bool wantDirectory)
        {
            if (size > 1 << 20)
                return null;
            var dir = At(f, extent * Sector, (int)size);
            if (dir == null)
                return null;

            int pos = 0;
            while (pos < dir.Length)
            {
                int len = dir[pos];
                if (len == 0)
                {
                    pos = (pos / Sector + 1) * Sector;
                    continue;
                }
                if (pos + 33 > dir.Length || pos + len > dir.Length)
                    break;
                bool isDir = (dir[pos + 25] & 2) != 0;
                int nameLen = dir[pos + 32];
                var recordName = Encoding.ASCII.GetString(dir, pos + 33, Math.Min(nameLen, len - 33));
                var semi = recordName.IndexOf(';');
                if (semi >= 0)
                    recordName = recordName.Substring(0, semi);
                if (isDir == wantDirectory && string.Equals(recordName, name, StringComparison.OrdinalIgnoreCase))
                    return new long[] { BitConverter.ToUInt32(dir, pos + 2), BitConverter.ToUInt32(dir, pos + 10) };
                pos += len;
            }
            return null;
        }

        private static byte[] At(Stream f, long offset, int count)
        {
            if (offset + count > f.Length)
                return null;
            f.Seek(offset, SeekOrigin.Begin);
            return SwitchTitle.ReadExact(f, count);
        }
    }
}
