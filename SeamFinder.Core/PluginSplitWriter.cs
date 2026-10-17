using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins.Analysis;
using Mutagen.Bethesda.Plugins.Exceptions;
using Mutagen.Bethesda.Skyrim;

namespace SeamFinder.Core;

// Writes a tool's output plugin. A plugin can reference at most 255 masters; on a big load order the
// overrides a patch carries can come from more plugins than that. Mutagen's auto-split (the mechanism
// Synthesis's "Split Files if Max Masters Exceeded" uses) then writes adjacent siblings - Name.esp,
// Name_2.esp, Name_3.esp - each within the limit. When nothing overflows the output is a single plugin,
// exactly as before. Same logic PatchForeman v0.2.6/0.2.7 shipped.
public static class PluginSplitWriter
{
    // Returns every file written, the base plugin first.
    public static IReadOnlyList<string> Write(SkyrimMod patchMod, string outputPath, string dataFolderForWrite, Action<string> log)
    {
        RetireStaleSplitSiblings(outputPath, log);

        // WithAllParentMasters also lists every master's own masters. Mutagen's split counts the masters the records
        // need, but the parents are added afterwards, so a patch that needs more than ~254 masters (parents included)
        // failed with TooManyMastersException instead of splitting. Try with the parents first; if that overflows,
        // write without them: the game only needs the masters the records point at, and the split keeps each file
        // within the limit.
        try
        {
            WriteOnce(patchMod, outputPath, dataFolderForWrite, withParentMasters: true);
        }
        catch (TooManyMastersException ex)
        {
            log($"Too many masters with every master's own masters listed ({ex.Message.Trim()}). Writing without the parent masters so the patch can split.");
            DeletePartialOutput(outputPath);
            WriteOnce(patchMod, outputPath, dataFolderForWrite, withParentMasters: false);
        }

        var files = EnumerateSplitSiblings(outputPath);
        files.Insert(0, outputPath);
        if (files.Count > 1)
        {
            log($"Master limit exceeded: the output was split into {files.Count} plugins. ALL of them must be enabled, " +
                "and they must sit next to each other in the load order, in this order:");
            foreach (var f in files) log("  " + Path.GetFileName(f));
        }
        return files;
    }

    static void WriteOnce(SkyrimMod patchMod, string outputPath, string dataFolderForWrite, bool withParentMasters)
    {
        var builder = SkyrimMod.WriteBuilder(SkyrimRelease.SkyrimSE)
            .ToPath(outputPath, fileSystem: null)
            .WithNoLoadOrder()
            .WithDataFolder(dataFolderForWrite);
        if (withParentMasters) builder = builder.WithAllParentMasters();
        builder.WithAutoSplit().Write(patchMod);
    }

    // A failed write can leave a half-written plugin (and split siblings) behind; they are this run's own output, so they go.
    static void DeletePartialOutput(string outputPath)
    {
        foreach (var f in EnumerateSplitSiblings(outputPath).Append(outputPath))
            if (File.Exists(f)) File.Delete(f);
    }

    // Existing Name_2.esp, Name_3.esp, ... next to the output, sorted by index.
    public static List<string> EnumerateSplitSiblings(string outputPath)
    {
        var dir = Path.GetDirectoryName(outputPath)!;
        var baseName = Path.GetFileNameWithoutExtension(outputPath);
        var ext = Path.GetExtension(outputPath);
        var found = new List<(int Index, string Path)>();
        if (!Directory.Exists(dir)) return new List<string>();
        foreach (var candidate in Directory.EnumerateFiles(dir, baseName + "_*" + ext))
        {
            if (!Path.GetExtension(candidate).Equals(ext, StringComparison.OrdinalIgnoreCase)) continue;
            if (MultiModFileAnalysis.IsSplitFileName(Path.GetFileNameWithoutExtension(candidate), baseName, out var index))
                found.Add((index, candidate));
        }
        return found.OrderBy(x => x.Index).Select(x => x.Path).ToList();
    }

    // A previous run that split leaves Name_2.esp behind; if this run fits in one plugin that file would linger
    // as an orphan sibling. Move it aside under the toolkit's backup naming (never deleted, never loadable).
    static void RetireStaleSplitSiblings(string outputPath, Action<string> log)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        foreach (var stale in EnumerateSplitSiblings(outputPath))
        {
            var bak = stale + ".bak_" + stamp;
            File.Move(stale, bak);
            log($"Moved leftover split plugin from a previous run aside: {Path.GetFileName(stale)} -> {Path.GetFileName(bak)}");
        }
    }

    // "RoadMaskMerge_2.esp" is a split sibling of "RoadMaskMerge.esp": true when `name` is `baseName` + "_<n>" + same extension.
    public static bool IsSplitSiblingOf(string name, string baseFileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(baseFileName);
        var ext = Path.GetExtension(baseFileName);
        if (!Path.GetExtension(name).Equals(ext, StringComparison.OrdinalIgnoreCase)) return false;
        return MultiModFileAnalysis.IsSplitFileName(Path.GetFileNameWithoutExtension(name), baseName, out _);
    }
}
