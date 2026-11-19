namespace RomM.Saves
{
    /// <summary>
    /// What a finished sync tells the player, or null when it did nothing worth interrupting them
    /// for. A sync runs at every launch and exit, so "nothing changed" stays silent; a transfer
    /// and a failure are said, because a failure is otherwise only in the log.
    /// </summary>
    internal static class SaveSyncMessages
    {
        public static string Title(string gameName) => string.IsNullOrEmpty(gameName) ? "RomM save sync" : gameName;

        public static string Describe(int uploaded, int downloaded, int conflicts, int failed, string message)
        {
            if (failed > 0)
                return "Save sync failed" + (string.IsNullOrEmpty(message) ? "." : ": " + message);

            if (downloaded > 0)
                return conflicts > 0
                    ? "Downloaded the newer save from RomM (both sides had changed)."
                    : "Downloaded the newer save from RomM.";

            if (uploaded > 0)
                return conflicts > 0
                    ? "Uploaded this save to RomM (it was newer than RomM's)."
                    : "Uploaded this save to RomM.";

            return null;
        }
    }
}
