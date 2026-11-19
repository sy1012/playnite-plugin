using RomM.Models.RomM.Save;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RomM.Saves
{
    /// <summary>
    /// Picks which of negotiate's operations act on the one local save this sync resolved.
    ///
    /// Negotiate answers for every save RomM holds for the ROM, and a ROM can have several -- a
    /// different file name from another client, another slot, the same name under another
    /// emulator tag. They all describe different saves, so applying each of them to the one
    /// local target writes them over each other in whatever order RomM lists them, and the last
    /// one wins whether or not it is the newest. Only an operation on this save's own file name
    /// and slot acts on it; of several such downloads, the newest one does.
    /// </summary>
    internal static class SaveOperationFilter
    {
        public static List<RomMSyncOperation> ForTarget(
            IEnumerable<RomMSyncOperation> operations, int romId, string fileName, string slot,
            out List<RomMSyncOperation> passedOver)
        {
            var forRom = (operations ?? Enumerable.Empty<RomMSyncOperation>()).Where(o => o != null && o.RomId == romId).ToList();
            var matching = forRom.Where(o => IsThisSave(o, fileName, slot)).ToList();

            var downloads = matching.Where(IsDownloadLike).ToList();
            var newestDownload = downloads
                .OrderByDescending(o => o.ServerUpdatedAt ?? DateTime.MinValue)
                .FirstOrDefault();

            var chosen = matching.Where(o => !IsDownloadLike(o) || o == newestDownload).ToList();
            passedOver = forRom.Where(o => !chosen.Contains(o)).ToList();
            return chosen;
        }

        private static bool IsDownloadLike(RomMSyncOperation op)
        {
            return op.Action == RomMSyncAction.Download || op.Action == RomMSyncAction.Conflict;
        }

        private static bool IsThisSave(RomMSyncOperation op, string fileName, string slot)
        {
            // An upload answers the save this client reported, so a missing name there is ours.
            if (string.IsNullOrEmpty(op.FileName))
                return op.Action == RomMSyncAction.Upload;

            return string.Equals(op.FileName, fileName, StringComparison.OrdinalIgnoreCase)
                   && (string.IsNullOrEmpty(op.Slot) || string.Equals(op.Slot, slot, StringComparison.OrdinalIgnoreCase));
        }

        public static string Describe(RomMSyncOperation op)
        {
            return $"{op.Action} '{op.FileName}' (save {op.SaveId?.ToString() ?? "-"}, slot {op.Slot ?? "-"}, " +
                   $"emulator {op.Emulator ?? "-"}, updated {op.ServerUpdatedAt?.ToString("u") ?? "-"}" +
                   (string.IsNullOrEmpty(op.Reason) ? "" : $", {op.Reason}") + ")";
        }
    }
}
