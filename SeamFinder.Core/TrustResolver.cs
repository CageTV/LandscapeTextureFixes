// Shared "which plugins do we trust as a source of truth" logic, used by
// BOTH SeamFixer (heights) and TextureLayerFixer (textures) so the two never
// drift apart on what counts as trusted - the exact bug class that let
// SeamDetector.cs's OWN separate copy go stale relative to SeamFixer.cs
// earlier in this project's history.

namespace SeamFinder.Core;

public static class TrustResolver
{
    public static readonly HashSet<string> BaseGamePlugins = new(StringComparer.OrdinalIgnoreCase)
    {
        "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm",
        "Unofficial Skyrim Special Edition Patch.esp", "Legacy of the Dragonborn.esm",
        // Third-party Nexus mod (spaces in the name - distinct from
        // SeamFixer's own no-spaces output filename "LandscapeSeamFixes.esp"),
        // built specifically as a companion to Landscape and Water Fixes:
        // nexusmods.com/.../59687.
        "Landscape Seam Fixes.esp",
        // Falkreath-area landscape overhaul, built to work WITH Northern
        // Roads (matches its road textures/area rather than fighting it) -
        // trusted unconditionally, and given priority ABOVE Northern Roads
        // itself in SeamFixer.ResolveTrustedOnlyLandscape, since it's the
        // one mod the user has confirmed should win over Northern Roads
        // rather than the other way around.
        "UniqueLocationsRiverwoodForest.esp",
        "Landscape and Water Fixes.esp",
        "Lux Via.esp",

        // This toolkit's OWN sibling apps' default output plugins - trusted
        // unconditionally for the SAME reason everything else in this set
        // is: each one represents a deliberate, already-computed-correctly
        // decision about that exact cell, not something a LATER tool in the
        // pipeline should second-guess. Confirmed as a real, user-reported
        // bug 2026-09-11 ("swiss cheese" terrain): SeamFixer's own
        // restoration logic gives Northern Roads/URF UNCONDITIONAL priority
        // wherever they genuinely edited a cell, and does ZERO blending -
        // it copies the trusted plugin's WHOLE LAND record verbatim. Without
        // recognizing RoadMaskMerge.esp as trusted, SeamFixer saw it as just
        // another untrusted mod "overwriting" Northern Roads' terrain
        // (which, from SeamFixer's narrow view, is exactly true - the
        // merged output legitimately differs from NR's own pure copy almost
        // everywhere outside the actual road strip) and restored the FULL,
        // UNBLENDED Northern Roads cell right back over RoadMaskMerger's
        // careful, road-mask-scoped per-vertex merge - undoing it in a
        // scattered patchwork of "SeamFixer overwrote this one" cells mixed
        // with untouched neighbors, which is exactly what reads as "swiss
        // cheese" in-game. The user's own workaround (run RoadMaskMerger
        // FIRST, install its output, THEN run SeamFixer) avoided the worst
        // of it by accident, but doesn't fix the root cause for every
        // ordering - this entry does, regardless of run order.
        "RoadMaskMerge.esp",
        // The other three sibling tools' own default outputs, added for the
        // same symmetric reasoning even though only RoadMaskMerge.esp was
        // confirmed to cause visible damage: none of SeamFixer/TextureLayerFixer/
        // FloatingObjectFixer should treat another one of THIS toolkit's own
        // finished outputs as an ordinary "untrusted mod" to second-guess.
        "LandscapeTextureFixes.esp",
        "FloatingObjectFixes.esp",
        // LandscapeSeamFixes.esp (SeamFixer's own output) is deliberately
        // NOT listed here - SeamFixer already has no explicit self-
        // exclusion the way RoadMaskMerger does, and adding it would make a
        // RE-RUN's own prior output eligible as a "best" trusted-pool
        // candidate via ordinary load-order priority, which needs its own
        // dedicated review before enabling (unlike the other three, which
        // are read-only inputs from SeamFixer's perspective, never
        // something SeamFixer itself re-derives from).
    };

    // Every entry above is trusted alongside its own compatibility-patch
    // family too, not just standalone - most Nexus mods name patches
    // "<Mod Name> - <what it patches>.esp" (LWF's LFfGM/GotT/Myrwatch/...
    // patches, Lux Via's dozens of same-prefix patches, ...), so that
    // convention is auto-derived from each trusted base name below rather
    // than hardcoded per mod. A few mods use a totally unrelated prefix
    // scheme instead and need an explicit entry here - confirmed by the
    // user for Legacy of the Dragonborn specifically (its patches use
    // "DBM_"/"DBM_CC_"/"LOTD_"/"LOTD_TCC_", nothing derivable from its own
    // filename). Add future non-standard cases here as they turn up.
    public static readonly Dictionary<string, string[]> NonStandardPatchPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Legacy of the Dragonborn.esm"] = ["DBM_", "DBM_CC_", "LOTD_", "LOTD_TCC_"],
        // The mod's own file is "UniqueLocationsRiverwoodForest.esp" (with
        // "Forest"), but its official compatibility-patch addon ("Unique
        // Locations Riverwood - Some Patches" on Nexus) ships patches
        // prefixed "UniqueLocationsRiverwood -" (WITHOUT "Forest") - e.g.
        // "UniqueLocationsRiverwood - Northern Roads.esp". The derived
        // "<stem> -" convention never matches this, so every one of these
        // patches was previously invisible to IsPatchOfTrustedBase entirely
        // (not just mis-ranked - not trusted AT ALL). Now largely redundant
        // with masters-based detection below, but kept as a cheap first
        // check and a safety net if masters data is ever unavailable.
        ["UniqueLocationsRiverwoodForest.esp"] = ["UniqueLocationsRiverwood -"],
    };

    // Per-run ESP master lists, keyed by filename - set once per run via
    // SetMastersContext. A plain static rather than threading a new
    // parameter through every call site: both fixers only ever process one
    // run at a time (CLI/UI both call in, wait, done), so there's no
    // concurrent-run hazard to guard against.
    static Dictionary<string, HashSet<string>>? _mastersByPlugin;

    // This run's own output filename (e.g. "LandscapeSeamFixes.esp" or
    // "LandscapeTextureFixes.esp") - never eligible as a masters-detected
    // patch of anything, see HasMasterRelationship.
    static string? _outputPluginNameForRun;

    // A hand-authored compatibility patch masters a handful of things: base
    // game + DLCs + maybe a couple CC entries + the 1-2 mods it reconciles -
    // confirmed empirically at 13 for the real URF/Northern Roads patch. An
    // aggregator/merge output (a Synthesis patch, a bashed patch, or - the
    // bug this constant fixes - either fixer's OWN prior run) accumulates
    // masters from everything it ever copied data from and can reach dozens
    // (confirmed at 45 for a real prior LandscapeSeamFixes.esp). Generous
    // headroom above the 13 baseline, not a tight fit to it.
    const int MaxMastersForPatchDetection = 25;

    // Call once per run, before any trust check, from each fixer's own
    // GenerateFixPluginCore - see SeamFixer/TextureLayerFixer for how the
    // masters dictionary gets built from the resolved load order.
    public static void SetMastersContext(Dictionary<string, HashSet<string>> mastersByPlugin, string outputPluginName)
    {
        _mastersByPlugin = mastersByPlugin;
        _outputPluginNameForRun = outputPluginName;
    }

    // Masters-based patch detection: `plugin` counts as a patch of `baseName`
    // if `baseName` is literally one of its ESP masters - regardless of what
    // either is named. Confirmed empirically before building this: the real
    // "UniqueLocationsRiverwood - Northern Roads.esp" patch carries BOTH
    // UniqueLocationsRiverwoodForest.esp and Northern Roads.esp as masters,
    // while "Northern Roads - Skyrim Wayshrines patch.esp" (a same-day false
    // positive under the old name-only logic) carries Northern Roads.esp
    // but NOT UniqueLocationsRiverwoodForest.esp - masters correctly tell
    // the two apart where filenames alone can't. This is the general
    // replacement for guessing prefixes per mod: a patch's masters ARE its
    // author's own declaration of which mods it reconciles.
    //
    // Bug fixed here (caught by an immediate live-profile regression test,
    // not by reasoning alone): a fixer's own PRIOR output is itself one of
    // the plugins in the merged load order on any re-run, written with
    // WithAllParentMasters() - so it ends up mastering URF/Northern Roads/
    // whatever else it ever copied data from, and without a guard it looked
    // exactly like a genuine reconciliation patch of them. Two independent
    // guards: never let this run's own output name qualify at all, and cap
    // master count generously to exclude aggregator-shaped files in general
    // (any OTHER merge tool's output in the load order, not just this one).
    static bool HasMasterRelationship(string plugin, string baseName)
    {
        if (_outputPluginNameForRun is not null && plugin.Equals(_outputPluginNameForRun, StringComparison.OrdinalIgnoreCase))
            return false;
        if (_mastersByPlugin is null || !_mastersByPlugin.TryGetValue(plugin, out var masters))
            return false;
        if (masters.Count > MaxMastersForPatchDetection)
            return false;
        return masters.Contains(baseName);
    }

    // True if `plugin` is a compatibility patch belonging to `baseName`'s
    // family (NOT `baseName` itself) - the common "<base> - ..." naming
    // convention, one of the non-standard prefix sets above, OR an actual
    // ESP-master relationship - see HasMasterRelationship.
    public static bool IsPatchOfTrustedBase(string plugin, string baseName)
    {
        if (plugin.Equals(baseName, StringComparison.OrdinalIgnoreCase)) return false;
        var stem = Path.GetFileNameWithoutExtension(baseName);
        if (plugin.StartsWith(stem + " -", StringComparison.OrdinalIgnoreCase)) return true;
        if (NonStandardPatchPrefixes.TryGetValue(baseName, out var prefixes))
            foreach (var prefix in prefixes)
                if (plugin.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        if (HasMasterRelationship(plugin, baseName)) return true;
        return false;
    }

    // True if `plugin` is `baseName` itself or a patch of it, for any
    // trusted base name - the actual membership test IsBaseGamePlugin uses
    // for the "ordinary" trusted pool (everything except Northern Roads,
    // which is opt-in/ambiguous and handled separately below).
    public static bool IsTrustedBaseOrPatch(string plugin) =>
        BaseGamePlugins.Any(b => plugin.Equals(b, StringComparison.OrdinalIgnoreCase) || IsPatchOfTrustedBase(plugin, b));

    // User-supplied additions to the trusted list - each entry is either an
    // exact plugin name, or a prefix ending in "*" to trust a whole patch
    // family the same way the families above are matched. Kept as a plain
    // ordered list rather than a HashSet since prefix entries need
    // StartsWith, not just exact lookup; checked linearly, which is fine at
    // the handful-of-entries scale a text box realistically holds.
    //
    // Reused against TWO different lists in SeamFixer.cs's UI/CLI (this
    // method itself doesn't care which): the original "Additional trusted
    // plugins" box (ordinary trust - still ranked below Northern Roads/
    // UniqueLocationsRiverwoodForest.esp by ResolveTrustedOnlyLandscape's
    // tiers, same as always) and the newer "plugins that override Northern
    // Roads / URF" box (ranked ABOVE both instead - see
    // ResolveTrustedOnlyLandscape's priorityOverride tier). Same matching
    // rules either way; only what the CALLER does with a match differs.
    public static bool MatchesCustomTrust(string plugin, IReadOnlyList<string> customTrustedPlugins)
    {
        foreach (var pattern in customTrustedPlugins)
        {
            if (pattern.EndsWith('*'))
            {
                if (plugin.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (plugin.Equals(pattern, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // Northern Roads is opt-in, not in the always-on BaseGamePlugins set:
    // its Nexus page ships two downloads that both install as a plugin
    // literally named "Northern Roads.esp" - the full terrain-reshaping
    // version and a clutter-only version - so the filename alone can't
    // distinguish them. Trusting the wrong one would blend other mods
    // toward a version that never actually reshaped this terrain. The
    // caller states which one is actually installed. Its own compatibility-
    // patch hub is trusted alongside it the same auto-derived way as
    // everything else.
    public static bool IsNorthernRoadsOrItsPatch(string plugin, bool trustNorthernRoads) =>
        trustNorthernRoads && (plugin.Equals("Northern Roads.esp", StringComparison.OrdinalIgnoreCase) || IsPatchOfTrustedBase(plugin, "Northern Roads.esp"));

    public static bool IsBaseGamePlugin(string plugin, bool trustNorthernRoads, IReadOnlyList<string> customTrustedPlugins) =>
        IsTrustedBaseOrPatch(plugin)
        || MatchesCustomTrust(plugin, customTrustedPlugins)
        || IsNorthernRoadsOrItsPatch(plugin, trustNorthernRoads);
}
