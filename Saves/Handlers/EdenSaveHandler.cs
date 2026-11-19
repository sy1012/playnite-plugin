using Playnite.SDK.Models;
using RomM.Saves.Titles;
using RomM.Saves.Units;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RomM.Saves.Handlers
{
    /// <summary>
    /// Eden's Switch saves: one folder per application under the active user,
    /// <c>&lt;save root&gt;/user/save/0000000000000000/&lt;user&gt;/&lt;application id&gt;</c>, archived
    /// under the application id (Tender's and Argosy's layout).
    ///
    /// Where Eden keeps its data, which user is active and how both are spelled follow Tender's
    /// resolver (emu-atlas atlas/eden.py, eden-emu/eden v0.2.1): a <c>user</c> folder beside the
    /// executable is portable mode, otherwise <c>%APPDATA%\eden</c>; <c>qt-config.ini</c> may move
    /// the NAND or the save directory; the user is the one <c>profiles.dat</c> lists at
    /// <c>[System] current_user</c>.
    /// </summary>
    internal sealed class EdenSaveHandler : ISaveHandler
    {
        public string EmulatorTag => "eden";

        public bool CanHandle(Emulator emulator)
        {
            if (emulator == null)
                return false;
            if (string.Equals(emulator.BuiltInConfigId, "eden", StringComparison.OrdinalIgnoreCase))
                return true;
            return !string.IsNullOrEmpty(emulator.Name) &&
                   emulator.Name.IndexOf("eden", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public SaveTarget ResolveTarget(SaveTargetRequest request)
        {
            try
            {
                var appId = SwitchTitle.ApplicationId(request.ContentPath);
                if (appId == null)
                {
                    request.Logger?.Warn($"[SaveSync] Could not read the Switch application id of {request.ContentPath}.");
                    return null;
                }

                var root = SaveFolder(request.EmulatorInstallDir, request.AppDataDir, appId, request.Logger);
                if (root == null)
                    return null;

                return new UnitSaveTarget(EmulatorTag, SaveUnitNames.ZipName(request.ContentPath),
                    new OwnFolderUnit(root, appId), request.BackupDirectory);
            }
            catch (Exception ex)
            {
                request.Logger?.Error(ex, $"[SaveSync] Failed to resolve Eden save path for {request.Game?.Name}.");
                return null;
            }
        }

        /// <summary>The application's save folder, or null where the active user is not established.</summary>
        internal static string SaveFolder(string installDir, string appDataDir, string appId, Playnite.SDK.ILogger logger)
        {
            var dataRoot = !string.IsNullOrEmpty(installDir) && Directory.Exists(Path.Combine(installDir, "user"))
                ? Path.Combine(installDir, "user")
                : Path.Combine(appDataDir ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "eden");

            var ini = IniFile.Read(Path.Combine(dataRoot, "config", "qt-config.ini"));
            var nand = IniFile.NonDefaultPath(ini, "Data%20Storage", "nand_directory") ?? Path.Combine(dataRoot, "nand");
            var saveRoot = IniFile.NonDefaultPath(ini, "Data%20Storage", "save_directory");
            if (saveRoot == null || !Directory.Exists(saveRoot))
                saveRoot = nand;

            var profiles = Path.Combine(nand, "system", "save", "8000000000000010", "su", "avators", "profiles.dat");
            var users = File.Exists(profiles) ? ParseProfiles(File.ReadAllBytes(profiles)) : new List<byte[]>();
            if (users.Count == 0)
            {
                logger?.Warn($"[SaveSync] Eden has no user profiles at {profiles}; start Eden once so it creates one.");
                return null;
            }

            int index = CurrentUserIndex(ini);
            int slot = Math.Min(Math.Max(index, 0), 7);
            var user = users[slot < users.Count ? slot : 0];

            var legacy = Path.Combine(saveRoot, "user", "save", "0000000000000000", UserFolder(user), appId);
            ulong app = Convert.ToUInt64(appId, 16);
            var future = Path.Combine(saveRoot, "user", "save", "account", UuidRaw(user), (app & ~0xFFUL).ToString("X16"), "0");
            return Directory.Exists(future) ? future : legacy;
        }

        internal static List<byte[]> ParseProfiles(byte[] data)
        {
            var users = new List<byte[]>();
            if (data.Length < 0x10 + 8 * 0xC8)
                return users;
            for (int i = 0; i < 8; i++)
            {
                var uuid = new byte[16];
                Buffer.BlockCopy(data, 0x10 + i * 0xC8, uuid, 0, 16);
                if (uuid.Any(b => b != 0))
                    users.Add(uuid);
            }
            return users;
        }

        /// <summary><c>{high:016X}{low:016X}</c> of the UUID read as two little-endian u64s.</summary>
        internal static string UserFolder(byte[] uuid)
        {
            var low = BitConverter.ToUInt64(uuid, 0);
            var high = BitConverter.ToUInt64(uuid, 8);
            return high.ToString("X16") + low.ToString("X16");
        }

        private static string UuidRaw(byte[] uuid) => string.Concat(uuid.Select(b => b.ToString("x2")));

        internal static int CurrentUserIndex(Dictionary<string, string> ini)
        {
            string isDefault;
            if (!ini.TryGetValue(IniFile.Key("System", "current_user\\default"), out isDefault) || IniFile.Bool(isDefault, true))
                return 0;
            string raw;
            int value;
            return ini.TryGetValue(IniFile.Key("System", "current_user"), out raw) && int.TryParse(raw.Trim(), out value) ? value : 0;
        }
    }

    /// <summary>The upload name every client gives a save unit: the ROM file's stem plus ".zip".</summary>
    internal static class SaveUnitNames
    {
        public static string ZipName(string contentPath)
        {
            var trimmed = contentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.GetFileNameWithoutExtension(trimmed) + ".zip";
        }
    }

    /// <summary>A minimal INI reader keyed by lower-cased "section\0key", last value winning.</summary>
    internal static class IniFile
    {
        public static string Key(string section, string key) => (section + "\0" + key).ToLowerInvariant();

        public static Dictionary<string, string> Read(string path)
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : new Dictionary<string, string>();
        }

        public static Dictionary<string, string> Parse(string text)
        {
            var result = new Dictionary<string, string>();
            var section = string.Empty;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#')
                    continue;
                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }
                var eq = line.IndexOf('=');
                if (eq > 0)
                    result[Key(section, line.Substring(0, eq).Trim())] = line.Substring(eq + 1).Trim();
            }
            return result;
        }

        /// <summary>A path setting Qt-style: used only when its <c>\default</c> companion is false.</summary>
        public static string NonDefaultPath(Dictionary<string, string> ini, string section, string key)
        {
            string isDefault, value;
            if (!ini.TryGetValue(Key(section, key + "\\default"), out isDefault) || Bool(isDefault, true))
                return null;
            if (!ini.TryGetValue(Key(section, key), out value))
                return null;
            value = value.Replace("\"", string.Empty).Trim();
            return value.Length == 0 ? null : value.Replace('/', Path.DirectorySeparatorChar);
        }

        public static bool Bool(string value, bool fallback)
        {
            if (string.IsNullOrEmpty(value))
                return fallback;
            var first = char.ToLowerInvariant(value[0]);
            if ("ty1".IndexOf(first) >= 0) return true;
            if ("fn0".IndexOf(first) >= 0) return false;
            return fallback;
        }
    }
}
