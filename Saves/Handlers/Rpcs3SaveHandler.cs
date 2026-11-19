using Playnite.SDK.Models;
using RomM.Saves.Titles;
using RomM.Saves.Units;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RomM.Saves.Handlers
{
    /// <summary>
    /// RPCS3's PS3 saves: every folder in <c>dev_hdd0/home/&lt;user&gt;/savedata</c> whose name
    /// starts with the game's title id, each at the archive's top (Tender's and Argosy's layout).
    ///
    /// <c>dev_hdd0</c> is where <c>config/vfs.yml</c> puts it, else beside rpcs3.exe. Exactly one
    /// user account must exist: which one RPCS3 runs is recorded nowhere a restore could trust.
    /// </summary>
    internal sealed class Rpcs3SaveHandler : ISaveHandler
    {
        private static readonly Regex UserFolder = new Regex("^[0-9]{8}$");

        public string EmulatorTag => "rpcs3";

        public bool CanHandle(Emulator emulator)
        {
            if (emulator == null)
                return false;
            if (string.Equals(emulator.BuiltInConfigId, "rpcs3", StringComparison.OrdinalIgnoreCase))
                return true;
            return !string.IsNullOrEmpty(emulator.Name) &&
                   emulator.Name.IndexOf("rpcs3", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public SaveTarget ResolveTarget(SaveTargetRequest request)
        {
            try
            {
                var titleId = Ps3Title.TitleId(request.ContentPath);
                if (titleId == null)
                {
                    request.Logger?.Warn($"[SaveSync] Could not read the PS3 title id of {request.ContentPath}.");
                    return null;
                }

                var home = Path.Combine(DevHdd0(request.EmulatorInstallDir), "home");
                var users = Directory.Exists(home)
                    ? Directory.EnumerateDirectories(home).Where(d => UserFolder.IsMatch(Path.GetFileName(d))).ToList()
                    : new System.Collections.Generic.List<string>();
                if (users.Count != 1)
                {
                    request.Logger?.Warn($"[SaveSync] RPCS3 has {users.Count} user accounts under {home}; " +
                                         "save sync needs exactly one.");
                    return null;
                }

                return new UnitSaveTarget(EmulatorTag, SaveUnitNames.ZipName(request.ContentPath),
                    new PrefixFoldersUnit(Path.Combine(users[0], "savedata"), titleId), request.BackupDirectory);
            }
            catch (Exception ex)
            {
                request.Logger?.Error(ex, $"[SaveSync] Failed to resolve RPCS3 save path for {request.Game?.Name}.");
                return null;
            }
        }

        private static readonly Regex VfsHdd0 = new Regex(@"^\s*""?/dev_hdd0/""?\s*:\s*(.+?)\s*$", RegexOptions.Multiline);

        internal static string DevHdd0(string installDir)
        {
            foreach (var vfs in new[] { Path.Combine(installDir, "config", "vfs.yml"), Path.Combine(installDir, "vfs.yml") })
            {
                if (!File.Exists(vfs))
                    continue;
                var m = VfsHdd0.Match(File.ReadAllText(vfs));
                if (!m.Success)
                    continue;
                var value = m.Groups[1].Value.Trim().Trim('"');
                var emulatorDir = installDir.TrimEnd('\\', '/') + "/";
                value = value.Replace("$(EmulatorDir)", emulatorDir);
                if (value.Length > 0)
                    return Path.GetFullPath(value.Replace('/', Path.DirectorySeparatorChar));
            }
            return Path.Combine(installDir, "dev_hdd0");
        }
    }
}
