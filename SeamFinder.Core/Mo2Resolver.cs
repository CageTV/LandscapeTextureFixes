// Resolves an MO2 instance/profile into (a) the active load order and (b)
// a physical file for each active plugin - without needing MO2's own VFS.
// Same idea as MO2's virtual file system, just done with plain file reads
// instead of a kernel driver, so it works from any process regardless of
// how it was launched.
//
// Format notes (validated against a real instance, C:\Wabbajack\TBA):
//   plugins.txt   - ONLY lists regular toggleable plugins. A line starting
//                    with '*' is explicitly active; a line with no '*' is
//                    explicitly INACTIVE. Master files (Skyrim.esm, DLC
//                    .esm's) and ALL Creation Club content (.esm/.esl) are
//                    never listed here at all - they're implicitly always
//                    active. So the correct active/inactive rule is: active
//                    unless explicitly listed WITHOUT '*'. Getting this
//                    backwards (treating "not mentioned" as inactive)
//                    silently drops every master and CC plugin - caught by
//                    testing against real data before shipping this.
//   loadorder.txt - full load order (top = loaded first = lowest priority),
//                    every plugin MO2 knows about regardless of active state.
//   modlist.txt   - mod folder priority order, top = HIGHEST priority
//                    (wins file conflicts). '+' prefix = enabled folder,
//                    '-' prefix = disabled (its files are never used even
//                    if present on disk).
//
// File resolution for a given active plugin filename: search enabled mod
// folders in priority order (top first) for a file with that exact name;
// first match wins. If no mod folder has it, fall back to the base game's
// Data folder (covers un-replaced vanilla masters).

using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

namespace SeamFinder.Core;

public static class Mo2Resolver
{
    public record ResolvedPlugin(string FileName, string FilePath);

    public record Result(
        List<ResolvedPlugin> LoadOrder,
        List<string> MissingPlugins,
        List<string> EnabledModsInPriorityOrder,
        string ModsDir,
        string GameDataPath)
    {
        // Resolves an ARBITRARY Data-relative asset path (mesh, texture, ...) to
        // its winning file on disk, using the exact same modlist.txt priority
        // order already computed for plugin resolution - added 2026-09-12 for
        // CollisionRaycaster, which needs to find the real winning NIF for a
        // placed reference's Model.File, something this tool never needed
        // before (it only ever resolved ESPs). Same fallback rule as plugin
        // resolution: first enabled mod folder (highest priority first) that
        // has the file wins; un-replaced vanilla assets fall back to the base
        // game's Data folder.
        //
        // TWO PASSES, not one, and this order matters - "loose files always
        // override BSAs" (this project's own CLAUDE.md gotcha #8) is a GLOBAL
        // rule, not per-mod: a lower-priority mod's loose override must win
        // over a HIGHER-priority mod's packed-only version of the same file.
        // Checking archives inside the same per-mod loop as loose files would
        // get that backwards whenever a later, lower-priority mod happens to
        // ship the file loose. So: check every enabled mod's LOOSE files in
        // priority order first (unchanged from before); only if that entire
        // pass finds nothing, check every enabled mod's ARCHIVES in priority
        // order.
        //
        // Added 2026-09-13 after this exact gap confirmed to be blinding
        // collision-awareness for a whole mod: `Forest Fragments - Forest
        // Debris Expansion` ships ALL its meshes packed in `Forest
        // Fragments.bsa` with zero loose files (confirmed via direct
        // Get-ChildItem on the mod folder) - every nearby rock/log/debris
        // mesh near the original motivating deep-pool test cell was silently
        // unresolvable before this, which is why collision-awareness kept
        // finding nothing there despite genuinely relevant geometry sitting
        // right next to the flagged candidates.
        public string? ResolveDataFile(string relativeDataPath)
        {
            var normalized = relativeDataPath.Replace('/', Path.DirectorySeparatorChar);
            foreach (var modName in EnabledModsInPriorityOrder)
            {
                var candidate = Path.Combine(ModsDir, modName, normalized);
                if (File.Exists(candidate)) return candidate;
            }
            foreach (var modName in EnabledModsInPriorityOrder)
            {
                var modDir = Path.Combine(ModsDir, modName);
                var fromArchive = Mo2Resolver.TryResolveFromArchives(modDir, normalized);
                if (fromArchive is not null) return fromArchive;
            }
            var vanilla = Path.Combine(GameDataPath, normalized);
            return File.Exists(vanilla) ? vanilla : null;
        }
    }

    // Archive-reader cache (one Mutagen IArchiveReader per unique .bsa/.ba2
    // path, opened once and reused - re-opening per lookup would be far too
    // slow across the thousands of ResolveDataFile calls a real run makes).
    // A null value means "tried to open this archive and failed" (corrupt or
    // unsupported format), cached the same way to avoid retrying every call.
    static readonly Dictionary<string, IArchiveReader?> _archiveReaderCache = new(StringComparer.OrdinalIgnoreCase);

    // Extracted-file cache, keyed by "archivePath|relativePath" - many
    // candidates near the same rock/log cluster resolve to the SAME nearby
    // mesh, so extracting it once and reusing the extracted copy matters.
    // A null value means "looked, not in this archive" (cached the same way
    // as the reader cache, for the same reason).
    static readonly Dictionary<string, string?> _archiveExtractCache = new(StringComparer.OrdinalIgnoreCase);

    static readonly string _archiveExtractDir = Path.Combine(Path.GetTempPath(), "FloatingObjectArchiveCache");

    // Which .bsa/.ba2 files a mod folder has, computed once per mod folder -
    // a real run calls TryResolveFromArchives thousands of times (once per
    // unresolved loose-file lookup, across every enabled mod each time), and
    // re-listing the same folder's contents on every one of those calls adds
    // up fast. An empty list is cached too (most mods have no archives at
    // all), so a mod folder is only ever listed once regardless of outcome.
    static readonly Dictionary<string, List<string>> _modArchiveListCache = new(StringComparer.OrdinalIgnoreCase);

    static string? TryResolveFromArchives(string modDir, string normalizedRelativePath)
    {
        if (!_modArchiveListCache.TryGetValue(modDir, out var archivePaths))
        {
            archivePaths = [];
            if (Directory.Exists(modDir))
            {
                try
                {
                    archivePaths = Directory.EnumerateFiles(modDir, "*.bsa")
                        .Concat(Directory.EnumerateFiles(modDir, "*.ba2"))
                        .ToList();
                }
                catch (IOException) { /* safe miss, matches every other resolution failure in this file */ }
            }
            _modArchiveListCache[modDir] = archivePaths;
        }

        foreach (var archivePath in archivePaths)
        {
            var cacheKey = archivePath + "|" + normalizedRelativePath;
            if (_archiveExtractCache.TryGetValue(cacheKey, out var cachedExtractedPath))
            {
                if (cachedExtractedPath is not null) return cachedExtractedPath;
                continue;
            }

            if (!_archiveReaderCache.TryGetValue(archivePath, out var reader))
            {
                try { reader = Archive.CreateReader(GameRelease.SkyrimSE, archivePath); }
                catch { reader = null; } // corrupt/unreadable archive - safe miss
                _archiveReaderCache[archivePath] = reader;
            }
            if (reader is null) continue;

            string? extractedPath = null;
            try
            {
                var fileName = Path.GetFileName(normalizedRelativePath);
                var folderPath = Path.GetDirectoryName(normalizedRelativePath) ?? "";
                IArchiveFile? entry = null;
                if (reader.TryGetFolder(folderPath, out var folder))
                {
                    entry = folder.Files.FirstOrDefault(f =>
                        string.Equals(Path.GetFileName(f.Path), fileName, StringComparison.OrdinalIgnoreCase));
                }
                // Fallback added 2026-09-13: the strict folder-path match above
                // requires the ARCHIVE's internal folder to exactly match the
                // Data-relative folder Model.File implies - the same class of
                // mismatch already confirmed once in this project (the
                // AssetLinkGetter implicit-conversion "Meshes\" prefix bug in
                // CollisionRaycaster). Confirmed via direct reflection against
                // Mutagen.Bethesda.Archives.Bsa.BsaReader that `reader.Files`
                // exposes every file in the archive flatly, independent of
                // TryGetFolder - so when the exact-folder lookup misses (or the
                // folder doesn't exist in this archive at all), fall back to a
                // filename-only scan across the WHOLE archive before giving up.
                // Safe: this only fires when the strict match already failed,
                // never overrides a correct folder-scoped hit, and a same-named
                // file living in an unexpected folder inside ONE mod's own
                // archive is a far smaller coincidence risk than the resolution
                // failure it replaces (a genuinely-nearby collision mesh being
                // silently invisible to the whole subsystem).
                entry ??= reader.Files.FirstOrDefault(f =>
                    string.Equals(Path.GetFileName(f.Path), fileName, StringComparison.OrdinalIgnoreCase));
                if (entry is not null)
                {
                    Directory.CreateDirectory(_archiveExtractDir);
                    // Filename collisions across different archives/relative
                    // paths are real (two different mods can both ship a
                    // "rock01.nif") - hash the full cache key into the
                    // extracted filename so they never clobber each other.
                    var safeName = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
                        System.Text.Encoding.UTF8.GetBytes(cacheKey))) + "_" + fileName;
                    extractedPath = Path.Combine(_archiveExtractDir, safeName);
                    if (!File.Exists(extractedPath))
                    {
                        using var outStream = File.Create(extractedPath);
                        entry.AsStream().CopyTo(outStream);
                    }
                }
            }
            catch
            {
                extractedPath = null; // safe miss - a malformed entry shouldn't crash the whole resolution pass
            }

            _archiveExtractCache[cacheKey] = extractedPath;
            if (extractedPath is not null) return extractedPath;
        }

        return null;
    }

    public static Result Resolve(string instancePath, string profileName, string gameDataPath)
    {
        var profileDir = Path.Combine(instancePath, "profiles", profileName);
        var pluginsTxtPath = Path.Combine(profileDir, "plugins.txt");
        var loadOrderTxtPath = Path.Combine(profileDir, "loadorder.txt");
        var modlistTxtPath = Path.Combine(profileDir, "modlist.txt");
        return ResolveFromExplicitPaths(pluginsTxtPath, loadOrderTxtPath, modlistTxtPath, instancePath, gameDataPath);
    }

    /// Same as Resolve, but takes explicit paths to the three profile files
    /// instead of deriving them from instancePath+profileName - lets a UI
    /// override individual file locations if a profile is laid out
    /// differently than the standard <instance>\profiles\<name>\ structure.
    /// instancePath is still needed to locate the "mods" folder.
    public static Result ResolveFromExplicitPaths(
        string pluginsTxtPath, string loadOrderTxtPath, string modlistTxtPath,
        string instancePath, string gameDataPath)
    {
        var modsDir = Path.Combine(instancePath, "mods");

        if (!File.Exists(pluginsTxtPath)) throw new FileNotFoundException("plugins.txt not found - check the MO2 instance path and profile name.", pluginsTxtPath);
        if (!File.Exists(loadOrderTxtPath)) throw new FileNotFoundException("loadorder.txt not found - check the MO2 instance path and profile name.", loadOrderTxtPath);
        if (!File.Exists(modlistTxtPath)) throw new FileNotFoundException("modlist.txt not found - check the MO2 instance path and profile name.", modlistTxtPath);

        var explicitlyInactive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(pluginsTxtPath))
        {
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith('*'))
                explicitlyInactive.Add(line.Trim());
        }

        var activeLoadOrder = new List<string>();
        foreach (var line in File.ReadAllLines(loadOrderTxtPath))
        {
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line)) continue;
            var name = line.Trim();
            if (!explicitlyInactive.Contains(name))
                activeLoadOrder.Add(name);
        }

        var enabledModsInPriorityOrder = new List<string>();
        foreach (var line in File.ReadAllLines(modlistTxtPath))
        {
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith('+'))
                enabledModsInPriorityOrder.Add(line[1..].Trim());
        }

        var resolved = new List<ResolvedPlugin>();
        var missing = new List<string>();
        foreach (var fileName in activeLoadOrder)
        {
            string? path = null;
            foreach (var modName in enabledModsInPriorityOrder)
            {
                var candidate = Path.Combine(modsDir, modName, fileName);
                if (File.Exists(candidate)) { path = candidate; break; }
            }
            path ??= Path.Combine(gameDataPath, fileName);

            if (File.Exists(path))
                resolved.Add(new ResolvedPlugin(fileName, path));
            else
                missing.Add(fileName);
        }

        return new Result(resolved, missing, enabledModsInPriorityOrder, modsDir, gameDataPath);
    }

    /// Copies every resolved plugin file into destFolder under its original
    /// filename, so a plain single-folder-scanning environment builder can
    /// load them as if they were all physically in one merged Data folder.
    /// Plain copy (not a symlink/hardlink) for reliability regardless of
    /// which drives the MO2 instance and destination happen to be on.
    public static void MaterializeMergedFolder(List<ResolvedPlugin> loadOrder, string destFolder)
    {
        Directory.CreateDirectory(destFolder);
        foreach (var plugin in loadOrder)
        {
            var dest = Path.Combine(destFolder, plugin.FileName);
            File.Copy(plugin.FilePath, dest, overwrite: true);
        }
    }
}
