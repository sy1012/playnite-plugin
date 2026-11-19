using RomM.Saves.Titles;
using RomM.Saves.Units;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace RomM.Saves.Handlers
{
    /// <summary>
    /// GameCube and Wii saves in a Dolphin user folder -- RetroArch's Dolphin core keeps one at
    /// <c>&lt;saves&gt;/dolphin-emu/User</c>. Laid out as Tender and Argosy lay them:
    ///
    ///   * GameCube, GCI-folder card in slot A: the <c>.gci</c> files in
    ///     <c>GC/&lt;region&gt;/Card A</c> whose first four bytes are the disc's game code, flat.
    ///   * Wii: the title's folder <c>Wii/title/&lt;high&gt;/&lt;low&gt;</c>, under the low word.
    ///
    /// A raw memory card is shared by every game and is not synced.
    /// </summary>
    internal static class DolphinSaves
    {
        public const string EmulatorTag = "dolphin";

        private static readonly string[] DiscExtensions = { ".iso", ".gcm", ".ciso", ".gcz", ".wbfs", ".wia", ".rvz" };
        private const string DiscTitleHigh = "00010000";
        private const int GciFolderDevice = 8;

        public static bool LooksLikeDisc(string contentPath)
        {
            var ext = Path.GetExtension(contentPath ?? string.Empty).ToLowerInvariant();
            return DiscExtensions.Contains(ext);
        }

        public static SaveTarget Resolve(string userDir, string contentPath, string backupDir, Playnite.SDK.ILogger logger)
        {
            var disc = DolphinDisc.Read(contentPath);
            if (disc == null)
                return null;

            var ini = IniFile.Read(Path.Combine(userDir, "Config", "Dolphin.ini"));
            var zipName = SaveUnitNames.ZipName(contentPath);

            if (disc.IsWii)
            {
                string nandRoot;
                if (!ini.TryGetValue(IniFile.Key("General", "NANDRootPath"), out nandRoot) || string.IsNullOrWhiteSpace(nandRoot))
                    nandRoot = Path.Combine(userDir, "Wii");

                var titles = Path.Combine(nandRoot, "title");
                var low = disc.WiiLowWord;
                var highs = Directory.Exists(titles)
                    ? Directory.EnumerateDirectories(titles).Where(h => Directory.Exists(Path.Combine(h, low))).ToList()
                    : new System.Collections.Generic.List<string>();
                var high = highs.Count == 1 ? Path.GetFileName(highs[0]) : DiscTitleHigh;

                return new UnitSaveTarget(EmulatorTag, zipName,
                    new OwnFolderUnit(Path.Combine(titles, high, low), low), backupDir);
            }

            string slotA;
            if (ini.TryGetValue(IniFile.Key("Core", "SlotA"), out slotA) && slotA.Trim() != GciFolderDevice.ToString())
            {
                logger?.Info($"[SaveSync] Dolphin's slot A is not a GCI folder (SlotA = {slotA}); a shared card is not synced.");
                return null;
            }
            if (disc.GameCubeRegionFolder == null)
            {
                logger?.Warn($"[SaveSync] Could not tell the region of {contentPath}.");
                return null;
            }

            string folder;
            if (!ini.TryGetValue(IniFile.Key("Core", "GCIFolderAPath"), out folder) || string.IsNullOrWhiteSpace(folder))
                folder = Path.Combine(userDir, "GC", disc.GameCubeRegionFolder, "Card A");

            var code = disc.GameCode;
            return new UnitSaveTarget(EmulatorTag, zipName,
                new OwnFilesUnit(folder, f => IsGciOf(f, code)), backupDir);
        }

        private static bool IsGciOf(string path, string gameCode)
        {
            if (!string.Equals(Path.GetExtension(path), ".gci", StringComparison.OrdinalIgnoreCase))
                return false;
            try
            {
                using (var f = File.OpenRead(path))
                {
                    var head = SwitchTitle.ReadExact(f, 4);
                    return head != null && Encoding.ASCII.GetString(head) == gameCode;
                }
            }
            catch (IOException)
            {
                return false;
            }
        }
    }
}
