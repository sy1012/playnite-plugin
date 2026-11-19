using Playnite.SDK.Models;
using RomM.Saves.Titles;
using RomM.Saves.Units;
using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace RomM.Saves.Handlers
{
    /// <summary>
    /// Cemu's Wii U saves: the title's own folder <c>&lt;mlc&gt;/usr/save/&lt;high&gt;/&lt;low&gt;</c>,
    /// archived under the low word (Tender's and Argosy's layout).
    ///
    /// The MLC is <c>settings.xml</c>'s <c>mlc_path</c>, else <c>mlc01</c> beside it. Settings live
    /// in a <c>portable</c> folder beside Cemu.exe, else beside Cemu.exe where a settings.xml is
    /// already there (a 1.x-style install), else in <c>%APPDATA%\Cemu</c>.
    /// </summary>
    internal sealed class CemuSaveHandler : ISaveHandler
    {
        public string EmulatorTag => "cemu";

        public bool CanHandle(Emulator emulator)
        {
            if (emulator == null)
                return false;
            if (string.Equals(emulator.BuiltInConfigId, "cemu", StringComparison.OrdinalIgnoreCase))
                return true;
            return !string.IsNullOrEmpty(emulator.Name) &&
                   emulator.Name.IndexOf("cemu", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public SaveTarget ResolveTarget(SaveTargetRequest request)
        {
            try
            {
                var title = WiiUTitle.TitleId(request.ContentPath);
                if (!title.HasValue)
                {
                    request.Logger?.Warn($"[SaveSync] Could not read the Wii U title id of {request.ContentPath} " +
                                         "(a .wua or a folder dump's code/app.xml is needed).");
                    return null;
                }

                var mlc = MlcPath(request.EmulatorInstallDir, request.AppDataDir);
                var low = WiiUTitle.LowWord(title.Value);
                var root = Path.Combine(mlc, "usr", "save", WiiUTitle.HighWord(title.Value), low);

                return new UnitSaveTarget(EmulatorTag, SaveUnitNames.ZipName(request.ContentPath),
                    new OwnFolderUnit(root, low), request.BackupDirectory);
            }
            catch (Exception ex)
            {
                request.Logger?.Error(ex, $"[SaveSync] Failed to resolve Cemu save path for {request.Game?.Name}.");
                return null;
            }
        }

        internal static string MlcPath(string installDir, string appDataDir)
        {
            string configDir;
            if (!string.IsNullOrEmpty(installDir) && Directory.Exists(Path.Combine(installDir, "portable")))
                configDir = Path.Combine(installDir, "portable");
            else if (!string.IsNullOrEmpty(installDir) && File.Exists(Path.Combine(installDir, "settings.xml")))
                configDir = installDir;
            else
                configDir = Path.Combine(appDataDir ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cemu");

            var settings = Path.Combine(configDir, "settings.xml");
            if (File.Exists(settings))
            {
                var configured = XDocument.Load(settings).Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "mlc_path")?.Value?.Trim();
                if (!string.IsNullOrEmpty(configured))
                    return configured;
            }
            return Path.Combine(configDir, "mlc01");
        }
    }
}
