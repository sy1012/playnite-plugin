using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RomM.Saves.Units
{
    /// <summary>One file of a save unit: the name it carries in the archive, and where it is on disk.</summary>
    internal sealed class UnitEntry
    {
        public UnitEntry(string entryName, string filePath)
        {
            EntryName = entryName;
            FilePath = filePath;
        }

        public string EntryName { get; }
        public string FilePath { get; }
    }

    /// <summary>
    /// A save made of several files that travels as one zip. Which files they are, how the zip
    /// names them, and what a restore may replace are the unit's; packing, hashing and staging
    /// are <see cref="UnitSaveTarget"/>'s.
    ///
    /// The archive layouts are the ones Tender (romm-tender) and Argosy write, so a save uploaded
    /// by either restores here and the other way round: RomM offers a save to every client by
    /// game, slot and file name, so the zip's entry names are the whole contract.
    /// </summary>
    internal abstract class SaveUnit
    {
        /// <summary>The files that are the save now, possibly none.</summary>
        public abstract IList<UnitEntry> CurrentEntries();

        /// <summary>
        /// Null when every entry name has the shape this unit's archive has, otherwise why not.
        /// Names arrive already checked as safe relative paths with '/' separators.
        /// </summary>
        public abstract string RejectEntries(IList<string> entryNames);

        /// <summary>
        /// Replaces the save with what <paramref name="stagedDir"/> holds -- the archive unpacked,
        /// on the same volume as the save so every replacement is a rename. Touches only what the
        /// unit owns; returns the files written.
        /// </summary>
        public abstract IList<string> Restore(string stagedDir);

        /// <summary>
        /// Where a staging folder may be made so that moving out of it is a rename, not a copy.
        /// </summary>
        public abstract string StagingParent { get; }

        internal static IEnumerable<string> FilesBelow(string root)
        {
            if (!Directory.Exists(root))
                return Enumerable.Empty<string>();
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
        }

        internal static string Relative(string root, string path)
        {
            return path.Substring(root.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace('\\', '/');
        }

        internal static string FirstSegment(string entryName)
        {
            var slash = entryName.IndexOf('/');
            return slash < 0 ? null : entryName.Substring(0, slash);
        }

        /// <summary>
        /// Moves each of <paramref name="replacements"/> (staged folder → live folder) into place,
        /// keeping every live folder it displaces until all are in, and putting them all back if
        /// one move fails.
        /// </summary>
        internal static void SwapFolders(IList<KeyValuePair<string, string>> replacements, IEnumerable<string> removals)
        {
            var asideSuffix = ".romm-old-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var movedAside = new List<KeyValuePair<string, string>>();
            var movedIn = new List<string>();

            try
            {
                foreach (var live in replacements.Select(r => r.Value).Concat(removals).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!Directory.Exists(live))
                        continue;
                    var aside = live + asideSuffix;
                    Directory.Move(live, aside);
                    movedAside.Add(new KeyValuePair<string, string>(live, aside));
                }

                foreach (var replacement in replacements)
                {
                    var parent = Path.GetDirectoryName(replacement.Value);
                    if (!string.IsNullOrEmpty(parent))
                        Directory.CreateDirectory(parent);
                    Directory.Move(replacement.Key, replacement.Value);
                    movedIn.Add(replacement.Value);
                }
            }
            catch
            {
                foreach (var live in movedIn)
                {
                    try { Directory.Delete(live, true); } catch { }
                }
                foreach (var pair in movedAside)
                {
                    try { Directory.Move(pair.Value, pair.Key); } catch { }
                }
                throw;
            }

            foreach (var pair in movedAside)
            {
                try { Directory.Delete(pair.Value, true); } catch { }
            }
        }
    }

    /// <summary>
    /// A save that is one folder the game owns whole -- a Switch application's save, a Wii or
    /// Wii U title's -- archived under a single top-level name (<see cref="ArchiveFolder"/>),
    /// which need not be the folder's own name on disk.
    /// </summary>
    internal sealed class OwnFolderUnit : SaveUnit
    {
        public OwnFolderUnit(string root, string archiveFolder)
        {
            Root = root;
            ArchiveFolder = archiveFolder;
        }

        public string Root { get; }
        public string ArchiveFolder { get; }

        public override string StagingParent => Path.GetDirectoryName(Root);

        public override IList<UnitEntry> CurrentEntries()
        {
            return FilesBelow(Root)
                .Select(f => new UnitEntry(ArchiveFolder + "/" + Relative(Root, f), f))
                .ToList();
        }

        public override string RejectEntries(IList<string> entryNames)
        {
            foreach (var name in entryNames)
            {
                if (!string.Equals(FirstSegment(name), ArchiveFolder, StringComparison.OrdinalIgnoreCase))
                    return $"entry '{name}' is not inside '{ArchiveFolder}/'";
            }
            return null;
        }

        public override IList<string> Restore(string stagedDir)
        {
            var staged = Directory.EnumerateDirectories(stagedDir).Single();
            SwapFolders(new[] { new KeyValuePair<string, string>(staged, Root) }, Enumerable.Empty<string>());
            return FilesBelow(Root).ToList();
        }
    }

    /// <summary>
    /// A save that is some of the files directly in a folder several games share -- a GameCube
    /// game's .gci files in Dolphin's GCI folder. The archive is flat. A restore writes the
    /// archive's files and removes the members it lacks; a file that is not a member is never
    /// replaced or removed.
    /// </summary>
    internal sealed class OwnFilesUnit : SaveUnit
    {
        private readonly Func<string, bool> _isMember;

        public OwnFilesUnit(string directory, Func<string, bool> isMember)
        {
            Directory_ = directory;
            _isMember = isMember;
        }

        public string Directory_ { get; }

        public override string StagingParent => Path.GetDirectoryName(Directory_);

        private IEnumerable<string> Members()
        {
            if (!Directory.Exists(Directory_))
                return Enumerable.Empty<string>();
            return Directory.EnumerateFiles(Directory_).Where(_isMember);
        }

        public override IList<UnitEntry> CurrentEntries()
        {
            return Members().Select(f => new UnitEntry(Path.GetFileName(f), f)).ToList();
        }

        public override string RejectEntries(IList<string> entryNames)
        {
            foreach (var name in entryNames)
            {
                if (name.IndexOf('/') >= 0)
                    return $"entry '{name}' is not a plain file name";
                var existing = Path.Combine(Directory_, name);
                if (File.Exists(existing) && !_isMember(existing))
                    return $"entry '{name}' would replace a file that is not this game's";
            }
            return null;
        }

        public override IList<string> Restore(string stagedDir)
        {
            Directory.CreateDirectory(Directory_);
            var incoming = Directory.EnumerateFiles(stagedDir).ToList();
            var incomingNames = new HashSet<string>(incoming.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            var written = new List<string>();

            foreach (var file in incoming)
            {
                var live = Path.Combine(Directory_, Path.GetFileName(file));
                if (File.Exists(live))
                    File.Delete(live);
                File.Move(file, live);
                written.Add(live);
            }

            foreach (var stale in Members().Where(m => !incomingNames.Contains(Path.GetFileName(m))).ToList())
                File.Delete(stale);

            return written;
        }
    }

    /// <summary>
    /// A save that is every folder directly in a shared directory whose name starts with the
    /// game's id -- a PS3 or PSP game's savedata folders, a PS2 game's folders on a folder memory
    /// card. Each sits at the archive's top. A folder starting with the id that is not a save (a
    /// PSP game's installed data) is no member: it is neither packed nor replaced, and a zip that
    /// would put a folder over it is refused. A restore replaces the members and nothing else.
    /// </summary>
    internal sealed class PrefixFoldersUnit : SaveUnit
    {
        private readonly Func<string, bool> _isMemberFolder;

        public PrefixFoldersUnit(string directory, string prefix, Func<string, bool> isMemberFolder = null)
        {
            Directory_ = directory;
            Prefix = prefix;
            _isMemberFolder = isMemberFolder ?? (_ => true);
        }

        public string Directory_ { get; }
        public string Prefix { get; }

        public override string StagingParent => Path.GetDirectoryName(Directory_);

        private bool HasPrefix(string name)
        {
            return !string.IsNullOrEmpty(name) && name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
        }

        private IEnumerable<string> MemberFolders()
        {
            if (!Directory.Exists(Directory_))
                return Enumerable.Empty<string>();
            return Directory.EnumerateDirectories(Directory_).Where(d => HasPrefix(Path.GetFileName(d)) && _isMemberFolder(d));
        }

        public override IList<UnitEntry> CurrentEntries()
        {
            return MemberFolders()
                .SelectMany(folder => FilesBelow(folder).Select(f =>
                    new UnitEntry(Path.GetFileName(folder) + "/" + Relative(folder, f), f)))
                .ToList();
        }

        public override string RejectEntries(IList<string> entryNames)
        {
            foreach (var name in entryNames)
            {
                var top = FirstSegment(name);
                if (!HasPrefix(top))
                    return $"entry '{name}' is not inside a folder starting with '{Prefix}'";
                var existing = Path.Combine(Directory_, top);
                if (Directory.Exists(existing) && !_isMemberFolder(existing))
                    return $"'{top}' is a folder of the game's that is not a save, and would be replaced";
            }
            return null;
        }

        public override IList<string> Restore(string stagedDir)
        {
            Directory.CreateDirectory(Directory_);
            var replacements = Directory.EnumerateDirectories(stagedDir)
                .Select(d => new KeyValuePair<string, string>(d, Path.Combine(Directory_, Path.GetFileName(d))))
                .ToList();
            SwapFolders(replacements, MemberFolders().ToList());
            return replacements.SelectMany(r => FilesBelow(r.Value)).ToList();
        }
    }

    /// <summary>
    /// A save that is several folders the game owns, each archived under a fixed top-level name --
    /// a 3DS title's save folder as <c>data/</c> and its extdata folder as <c>extdata/</c>. A
    /// restore replaces each folder whose root the archive carries and leaves the others, so a zip
    /// of <c>data/</c> alone keeps the extdata; a top-level name that is no root refuses the zip.
    /// </summary>
    internal sealed class ArchiveRootsUnit : SaveUnit
    {
        public ArchiveRootsUnit(IDictionary<string, string> roots, string stagingParent)
        {
            Roots = new Dictionary<string, string>(roots, StringComparer.OrdinalIgnoreCase);
            StagingParent = stagingParent;
        }

        /// <summary>Archive root name → folder on disk.</summary>
        public IDictionary<string, string> Roots { get; }

        public override string StagingParent { get; }

        public override IList<UnitEntry> CurrentEntries()
        {
            return Roots
                .SelectMany(r => FilesBelow(r.Value).Select(f => new UnitEntry(r.Key + "/" + Relative(r.Value, f), f)))
                .ToList();
        }

        public override string RejectEntries(IList<string> entryNames)
        {
            foreach (var name in entryNames)
            {
                var top = FirstSegment(name);
                if (top == null || !Roots.ContainsKey(top))
                    return $"entry '{name}' is not inside one of {string.Join(", ", Roots.Keys.Select(k => k + "/"))}";
            }
            return null;
        }

        public override IList<string> Restore(string stagedDir)
        {
            var replacements = Directory.EnumerateDirectories(stagedDir)
                .Select(d => new KeyValuePair<string, string>(d, Roots[Path.GetFileName(d)]))
                .ToList();
            SwapFolders(replacements, Enumerable.Empty<string>());
            return replacements.SelectMany(r => FilesBelow(r.Value)).ToList();
        }
    }
}
