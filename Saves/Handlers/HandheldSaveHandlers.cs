using Playnite.SDK.Models;
using RomM.Saves.Titles;
using RomM.Saves.Units;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RomM.Saves.Handlers
{
    internal static class EmulatorNames
    {
        public static bool Matches(Emulator emulator, string id)
        {
            if (emulator == null)
                return false;
            if (string.Equals(emulator.BuiltInConfigId, id, StringComparison.OrdinalIgnoreCase))
                return true;
            return !string.IsNullOrEmpty(emulator.Name) &&
                   emulator.Name.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static string AppData(SaveTargetRequest r) =>
            r.AppDataDir ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        public static string Documents(SaveTargetRequest r) =>
            r.DocumentsDir ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    /// <summary>
    /// Azahar's 3DS saves: the title's <c>data</c> folder and, where the image grants one, its
    /// extdata folder, archived as <c>data/</c> and <c>extdata/</c> (Tender's and Argosy's layout).
    /// The emulated SD is <c>sdmc</c> in Azahar's user folder -- <c>user</c> beside azahar.exe
    /// (portable), else <c>%APPDATA%\Azahar</c> -- unless <c>qt-config.ini</c> turns on custom
    /// storage and names another.
    /// </summary>
    internal sealed class AzaharSaveHandler : ISaveHandler
    {
        private const string ZeroId = "00000000000000000000000000000000";

        public string EmulatorTag => "azahar";

        public bool CanHandle(Emulator emulator) => EmulatorNames.Matches(emulator, "azahar");

        public SaveTarget ResolveTarget(SaveTargetRequest request)
        {
            try
            {
                var rom = N3dsRom.Read(request.ContentPath);
                if (rom == null)
                {
                    request.Logger?.Warn($"[SaveSync] Could not read the 3DS title of {request.ContentPath} " +
                                         "(a decrypted .3ds, .cci or .cxi is needed; a .cia is not read).");
                    return null;
                }

                var container = Path.Combine(SdmcDir(request), "Nintendo 3DS", ZeroId, ZeroId);
                var roots = new Dictionary<string, string>
                {
                    ["data"] = Path.Combine(container, "title",
                        (rom.ProgramId >> 32).ToString("x8"), (rom.ProgramId & 0xFFFFFFFF).ToString("x8"), "data"),
                };
                if (rom.ExtdataId.HasValue)
                {
                    roots["extdata"] = Path.Combine(container, "extdata",
                        (rom.ExtdataId.Value >> 32).ToString("X8"), (rom.ExtdataId.Value & 0xFFFFFFFF).ToString("X8"));
                }

                return new UnitSaveTarget(EmulatorTag, SaveUnitNames.ZipName(request.ContentPath),
                    new ArchiveRootsUnit(roots, container), request.BackupDirectory);
            }
            catch (Exception ex)
            {
                request.Logger?.Error(ex, $"[SaveSync] Failed to resolve Azahar save path for {request.Game?.Name}.");
                return null;
            }
        }

        internal static string SdmcDir(SaveTargetRequest request)
        {
            var install = request.EmulatorInstallDir;
            var userDir = !string.IsNullOrEmpty(install) && Directory.Exists(Path.Combine(install, "user"))
                ? Path.Combine(install, "user")
                : Path.Combine(EmulatorNames.AppData(request), "Azahar");

            var ini = IniFile.Read(Path.Combine(userDir, "config", "qt-config.ini"));
            var custom = Setting(ini, "use_custom_storage");
            var configured = Setting(ini, "sdmc_directory");
            if (string.Equals(custom, "true", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(configured))
                return configured.Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(userDir, "sdmc");
        }

        /// <summary>A setting as Azahar reads it: <c>\default=true</c> makes the default win.</summary>
        private static string Setting(Dictionary<string, string> ini, string key)
        {
            string isDefault, value;
            if (ini.TryGetValue(IniFile.Key("Data%20Storage", key + "\\default"), out isDefault) &&
                string.Equals(isDefault.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                return null;
            return ini.TryGetValue(IniFile.Key("Data%20Storage", key), out value) ? value.Replace("\"", "").Trim() : null;
        }
    }

    /// <summary>
    /// PPSSPP's PSP saves: every folder in <c>PSP/SAVEDATA</c> starting with the disc's game id
    /// that is a save, each at the archive's top (Tender's and Argosy's layout). The memory stick
    /// is <c>memstick</c> beside PPSSPPWindows.exe, unless an <c>installed.txt</c> there marks an
    /// installed copy, which uses <c>Documents\PPSSPP</c> or the folder the file names.
    /// </summary>
    internal sealed class PpssppSaveHandler : ISaveHandler
    {
        public string EmulatorTag => "ppsspp";

        public bool CanHandle(Emulator emulator) => EmulatorNames.Matches(emulator, "ppsspp");

        public SaveTarget ResolveTarget(SaveTargetRequest request)
        {
            try
            {
                var gameId = PspDisc.GameId(request.ContentPath);
                if (gameId == null)
                {
                    request.Logger?.Warn($"[SaveSync] Could not read the PSP game id of {request.ContentPath} " +
                                         "(a plain .iso is needed; a .cso, .pbp or .chd is not read).");
                    return null;
                }

                var saveData = Path.Combine(MemstickDir(request), "PSP", "SAVEDATA");
                return new UnitSaveTarget(EmulatorTag, SaveUnitNames.ZipName(request.ContentPath),
                    new PrefixFoldersUnit(saveData, gameId, PspDisc.IsSaveFolder), request.BackupDirectory);
            }
            catch (Exception ex)
            {
                request.Logger?.Error(ex, $"[SaveSync] Failed to resolve PPSSPP save path for {request.Game?.Name}.");
                return null;
            }
        }

        internal static string MemstickDir(SaveTargetRequest request)
        {
            var install = request.EmulatorInstallDir ?? string.Empty;
            var installed = Path.Combine(install, "installed.txt");
            if (!File.Exists(installed))
                return Path.Combine(install, "memstick");

            var named = File.ReadAllLines(installed).Select(l => l.Trim().TrimStart('﻿')).FirstOrDefault(l => l.Length > 0);
            if (!string.IsNullOrEmpty(named) && Directory.Exists(named))
                return named;
            return Path.Combine(EmulatorNames.Documents(request), "PPSSPP");
        }
    }

    /// <summary>
    /// PCSX2's PS2 saves on a folder memory card: the card's directories that begin with
    /// <c>B</c>, a region letter and the game's serial (<c>BASLUS-20152…</c>), each at the
    /// archive's top (Tender's layout). The data folder is PCSX2's own when a
    /// <c>portable.ini</c>/<c>portable.txt</c> sits beside it, else <c>Documents\PCSX2</c>; the
    /// card is the one enabled slot of <c>inis/PCSX2.ini</c> whose file is a folder. A file card
    /// is shared by every game and is not synced.
    /// </summary>
    internal sealed class Pcsx2SaveHandler : ISaveHandler
    {
        public string EmulatorTag => "pcsx2";

        public bool CanHandle(Emulator emulator) =>
            EmulatorNames.Matches(emulator, "pcsx2") && !EmulatorNames.Matches(emulator, "retroarch");

        public SaveTarget ResolveTarget(SaveTargetRequest request)
        {
            try
            {
                var serial = Ps2Disc.CardSerial(request.ContentPath);
                if (serial == null)
                {
                    request.Logger?.Warn($"[SaveSync] Could not read the PS2 serial of {request.ContentPath} " +
                                         "(a flat .iso/.bin is needed; a .chd or .cso is not read).");
                    return null;
                }

                var card = FolderCard(request);
                if (card == null)
                {
                    request.Logger?.Warn("[SaveSync] PCSX2 has no single folder memory card in its slots; " +
                                         "set slot 1 to a folder card to sync PS2 saves.");
                    return null;
                }

                var prefix = Prefix(card, serial);
                if (prefix == null)
                {
                    request.Logger?.Warn($"[SaveSync] The PS2 save folder prefix of {serial} is not established.");
                    return null;
                }

                return new UnitSaveTarget(EmulatorTag, SaveUnitNames.ZipName(request.ContentPath),
                    new PrefixFoldersUnit(card, prefix), request.BackupDirectory);
            }
            catch (Exception ex)
            {
                request.Logger?.Error(ex, $"[SaveSync] Failed to resolve PCSX2 save path for {request.Game?.Name}.");
                return null;
            }
        }

        internal static string DataRoot(SaveTargetRequest request)
        {
            var install = request.EmulatorInstallDir ?? string.Empty;
            if (File.Exists(Path.Combine(install, "portable.ini")) || File.Exists(Path.Combine(install, "portable.txt")))
                return install;
            return Path.Combine(EmulatorNames.Documents(request), "PCSX2");
        }

        /// <summary>The one enabled slot's card that is a folder, or null.</summary>
        internal static string FolderCard(SaveTargetRequest request)
        {
            var root = DataRoot(request);
            var ini = IniFile.Read(Path.Combine(root, "inis", "PCSX2.ini"));

            string folder;
            if (!ini.TryGetValue(IniFile.Key("Folders", "MemoryCards"), out folder) || string.IsNullOrWhiteSpace(folder))
                folder = "memcards";
            folder = folder.Replace('/', Path.DirectorySeparatorChar);
            if (!Path.IsPathRooted(folder))
                folder = Path.Combine(root, folder);

            var cards = new List<string>();
            foreach (var slot in new[] { 1, 2 })
            {
                string enabled, name;
                if (ini.TryGetValue(IniFile.Key("MemoryCards", $"Slot{slot}_Enable"), out enabled) && !IniFile.Bool(enabled, true))
                    continue;
                if (!ini.TryGetValue(IniFile.Key("MemoryCards", $"Slot{slot}_Filename"), out name) || string.IsNullOrWhiteSpace(name))
                    name = $"Mcd00{slot}.ps2";
                var path = Path.Combine(folder, name);
                if (Directory.Exists(path))
                    cards.Add(path);
            }
            var distinct = cards.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return distinct.Count == 1 ? distinct[0] : null;
        }

        /// <summary>
        /// <c>B</c>, the region letter and the serial: read off the game's directories already on
        /// the card where there are some, else the letter of the serial's family.
        /// </summary>
        internal static string Prefix(string card, string serial)
        {
            var shown = Directory.EnumerateDirectories(card).Select(Path.GetFileName)
                .Where(n => !n.StartsWith("_pcsx2_", StringComparison.Ordinal) && n.Contains(serial))
                .ToList();
            if (shown.Count > 0)
            {
                var leads = shown.Select(n => n.Length >= 2 ? n.Substring(0, 2) : n).Distinct().ToList();
                if (leads.Count == 1 && leads[0][0] == 'B' && shown.All(n => n.Substring(2).StartsWith(serial, StringComparison.Ordinal)))
                    return leads[0] + serial;
                return null;
            }
            var letter = Ps2Disc.RegionLetter(serial);
            return letter == null ? null : "B" + letter + serial;
        }
    }
}
