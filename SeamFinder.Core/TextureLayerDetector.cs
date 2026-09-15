// Landscape TEXTURE seam detection - the companion problem to SeamDetector's
// height seams: when two mods paint different ground textures (grass/dirt/
// snow/rock/etc.) for neighboring cells without knowing about each other, the
// shared edge shows a hard, abrupt line instead of a blend.
//
// This is a meaningfully different data shape than heights. A LAND record's
// texture data (see Mutagen's Landscape.Layers) is a flat list of BaseLayer/
// AlphaLayer entries, each tagged with which of the cell's 4 quadrants
// (Header.Quadrant) it belongs to - there's no single continuous value to
// compare like a height. A quadrant either paints a given texture somewhere
// within it or it doesn't.
//
// Two distinct kinds of mismatch get flagged, confirmed against real
// examples found in-game:
//   - "Foreign": the two boundary quadrants share NO texture at all - a
//     completely hard cutoff.
//   - "Bridgeable": each side's DOMINANT texture (highest total opacity)
//     differs, but at least one texture is already present in BOTH
//     quadrants (just not prominently at the boundary). This is actually
//     the more common real-world case - e.g. two cells that both contain
//     grass and rock, just with different ground types winning each side -
//     and it's fixable more naturally by strengthening a texture that's
//     already believably present nearby, rather than introducing a
//     completely foreign one.
//
// Deliberately stays at whole-quadrant granularity rather than exact per-
// vertex edge positions (AlphaLayerData.Position indexes a confirmed-by-
// inspection 17x17-per-quadrant grid, but its exact row/column ordering
// convention isn't nailed down yet).

using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace SeamFinder.Core;

// OpacityByQuadrant carries how strongly each texture is present (BaseLayer
// counts as a large constant since it's full, unconditional coverage) - this
// is what lets us find both the dominant texture and any shared-but-weak
// "bridge" candidate on each side.
public record DecodedCellTextures(Dictionary<Quadrant, Dictionary<FormKey, float>> OpacityByQuadrant, string Plugin);

public record TextureDetectionResult(
    List<string> ReportCsvLines,
    int PluginCount,
    int CellsProcessed,
    int WorldspaceCount,
    int MismatchCount);

public static class TextureLayerDetector
{
    // Only textures with at least this much total opacity summed across a
    // quadrant's alpha-layer positions count as "meaningfully present" -
    // filters out stray single-vertex traces that wouldn't read as a visible
    // texture patch anyway. A plain BaseLayer entry (no alpha data at all,
    // opacity implicitly full coverage) always counts.
    public const float MinMeaningfulOpacity = 3.0f;

    // Sentinel opacity value for a plain BaseLayer (full, unconditional
    // coverage - no per-vertex data to sum). Chosen well above any realistic
    // summed alpha-layer opacity (which maxes out somewhere under 289, one
    // per quadrant vertex, each capped at 1.0) so a base layer always reads
    // as more "dominant" than any partial alpha coverage, matching how it
    // actually renders in-game.
    const float BaseLayerOpacity = 1000f;

    public static TextureDetectionResult RunForResolvedPlugins(
        List<Mo2Resolver.ResolvedPlugin> loadOrder,
        Action<string> log)
    {
        var mergedFolder = Path.Combine(Path.GetTempPath(), "TextureDetectorMerged-" + Guid.NewGuid().ToString("N"));
        log($"Staging {loadOrder.Count} plugin files into {mergedFolder} ...");
        Mo2Resolver.MaterializeMergedFolder(loadOrder, mergedFolder);

        try
        {
            var modKeys = loadOrder.Select(p => ModKey.FromFileName(p.FileName)).ToArray();
            using var env = GameEnvironmentBuilder<ISkyrimMod, ISkyrimModGetter>
                .Create(GameRelease.SkyrimSE)
                .WithLoadOrder(modKeys)
                .WithTargetDataFolder(mergedFolder)
                .Build();

            var priorityIndex = modKeys.Select((k, idx) => (k, idx)).ToDictionary(x => x.k, x => x.idx);
            return RunDetection(env.LinkCache, priorityIndex, modKeys.Length, log);
        }
        finally
        {
            try { Directory.Delete(mergedFolder, recursive: true); }
            catch (Exception ex) { log($"(could not clean up temp folder {mergedFolder}: {ex.Message})"); }
        }
    }

    public static TextureDetectionResult RunForDirectDataFolder(string dataFolderPath, Action<string> log)
    {
        using var env = GameEnvironmentBuilder<ISkyrimMod, ISkyrimModGetter>
            .Create(GameRelease.SkyrimSE)
            .WithTargetDataFolder(dataFolderPath)
            .Build();

        var priorityIndex = env.LoadOrder.ListedOrder
            .Select((listing, idx) => (listing.ModKey, idx))
            .ToDictionary(x => x.ModKey, x => x.idx);

        return RunDetection(env.LinkCache, priorityIndex, env.LoadOrder.Count, log);
    }

    public static TextureDetectionResult RunDetection(
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        Dictionary<ModKey, int> priorityIndex,
        int loadOrderCount,
        Action<string> log)
    {
        log($"Load order: {loadOrderCount} plugins.");

        var cellsByWorldspace = new Dictionary<FormKey, Dictionary<(int X, int Y), DecodedCellTextures>>();
        int processed = 0;

        foreach (var context in linkCache.WinningContextOverrides<Cell, ICellGetter>(linkCache))
        {
            var cell = context.Record;
            if (cell.Grid is null) continue;
            if (!context.TryGetParentSimpleContext<IWorldspaceGetter>(out var wsContext)) continue;
            var worldspaceKey = wsContext.Record.FormKey;

            var (landscape, ownerModKey) = ResolveWinningLandscape(cell.FormKey, linkCache, priorityIndex);
            if (landscape?.Layers is null) continue;

            var opacityByQuadrant = DecodeOpacityByQuadrant(landscape.Layers);

            if (!cellsByWorldspace.TryGetValue(worldspaceKey, out var cellDict))
            {
                cellDict = new Dictionary<(int, int), DecodedCellTextures>();
                cellsByWorldspace[worldspaceKey] = cellDict;
            }
            cellDict[(cell.Grid.Point.X, cell.Grid.Point.Y)] = new DecodedCellTextures(opacityByQuadrant, ownerModKey.FileName);
            processed++;
        }

        log($"Processed {processed} exterior cells across {cellsByWorldspace.Count} worldspace(s).");

        var report = new List<string> {
            "Worldspace,Edge,CellA_X,CellA_Y,PluginA,CellB_X,CellB_Y,PluginB,SamePlugin,QuadrantA,QuadrantB,MismatchKind,DominantA,DominantB,BridgeTexture,TexturesOnlyInA,TexturesOnlyInB"
        };

        foreach (var (wsKey, cellDict) in cellsByWorldspace)
        {
            foreach (var ((x, y), cellA) in cellDict)
            {
                if (cellDict.TryGetValue((x + 1, y), out var cellBEast))
                    CompareEdge(report, wsKey, "East", x, y, cellA, x + 1, y, cellBEast);

                if (cellDict.TryGetValue((x, y + 1), out var cellBNorth))
                    CompareEdge(report, wsKey, "North", x, y, cellA, x, y + 1, cellBNorth);
            }
        }

        var mismatchCount = report.Count - 1;
        log($"Found {mismatchCount} texture mismatches at cell boundaries.");

        return new TextureDetectionResult(report, loadOrderCount, processed, cellsByWorldspace.Count, mismatchCount);
    }

    // Same independent-resolution reasoning as SeamDetector.ResolveWinningLandscape:
    // Landscape (and therefore Layers) is embedded per-plugin's own CELL copy,
    // not a separately-linked record, so the winning CELL override isn't
    // necessarily the plugin that owns the winning landscape/texture data.
    static (ILandscapeGetter? Landscape, ModKey OwnerModKey) ResolveWinningLandscape(
        FormKey cellFormKey,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        Dictionary<ModKey, int> priorityIndex)
    {
        ILandscapeGetter? best = null;
        ModKey bestModKey = default;
        int bestIndex = -1;

        foreach (var ctx in linkCache.ResolveAllSimpleContexts<ICellGetter>(cellFormKey, ResolveTarget.Winner))
        {
            if (ctx.Record.Landscape is null) continue;
            var idx = priorityIndex.GetValueOrDefault(ctx.ModKey, -1);
            if (idx > bestIndex)
            {
                bestIndex = idx;
                best = ctx.Record.Landscape;
                bestModKey = ctx.ModKey;
            }
        }

        return (best, bestModKey);
    }

    public static Dictionary<Quadrant, Dictionary<FormKey, float>> DecodeOpacityByQuadrant(IReadOnlyList<IBaseLayerGetter> layers)
    {
        var result = new Dictionary<Quadrant, Dictionary<FormKey, float>>
        {
            [Quadrant.BottomLeft] = new(),
            [Quadrant.BottomRight] = new(),
            [Quadrant.TopLeft] = new(),
            [Quadrant.TopRight] = new(),
        };

        foreach (var layer in layers)
        {
            if (layer?.Header is null) continue;
            var texKey = layer.Header.Texture.FormKey;
            if (texKey.IsNull) continue;

            float opacity = BaseLayerOpacity;
            if (layer is IAlphaLayerGetter alpha)
            {
                opacity = 0f;
                if (alpha.AlphaLayerData is not null)
                    foreach (var data in alpha.AlphaLayerData)
                        opacity += data.Opacity;
                if (opacity < MinMeaningfulOpacity) continue;
            }

            var dict = result[layer.Header.Quadrant];
            dict[texKey] = dict.GetValueOrDefault(texKey) + opacity;
        }

        return result;
    }

    // Boundary-adjacent quadrant pairs for each comparison direction. A cell's
    // East side is covered by its two "Right" quadrants; the neighbor's West
    // side (facing back) is its two "Left" quadrants. Same logic rotated for
    // North/"Top" vs "Bottom".
    static readonly (Quadrant A, Quadrant B)[] EastPairs =
    {
        (Quadrant.BottomRight, Quadrant.BottomLeft),
        (Quadrant.TopRight, Quadrant.TopLeft),
    };
    static readonly (Quadrant A, Quadrant B)[] NorthPairs =
    {
        (Quadrant.TopLeft, Quadrant.BottomLeft),
        (Quadrant.TopRight, Quadrant.BottomRight),
    };

    static void CompareEdge(List<string> report, FormKey worldspace, string edgeName,
        int ax, int ay, DecodedCellTextures a, int bx, int by, DecodedCellTextures b)
    {
        var pairs = edgeName == "East" ? EastPairs : NorthPairs;

        foreach (var (quadA, quadB) in pairs)
        {
            var opacityA = a.OpacityByQuadrant[quadA];
            var opacityB = b.OpacityByQuadrant[quadB];
            if (opacityA.Count == 0 || opacityB.Count == 0) continue;

            var dominantA = opacityA.OrderByDescending(kv => kv.Value).First().Key;
            var dominantB = opacityB.OrderByDescending(kv => kv.Value).First().Key;
            if (dominantA == dominantB) continue; // already matches - no seam here

            var onlyInA = opacityA.Keys.Except(opacityB.Keys).ToList();
            var onlyInB = opacityB.Keys.Except(opacityA.Keys).ToList();
            var shared = opacityA.Keys.Where(opacityB.ContainsKey).ToList();

            var kind = shared.Count > 0 ? "Bridgeable" : "Foreign";
            // Best bridge candidate: highest combined presence across both
            // sides, so we pick something already reasonably established in
            // the area rather than a barely-there trace.
            var bridge = shared.Count > 0
                ? shared.OrderByDescending(t => opacityA[t] + opacityB[t]).First().ToString()
                : "";

            report.Add($"{worldspace},{edgeName},{ax},{ay},{a.Plugin},{bx},{by},{b.Plugin}," +
                $"{a.Plugin == b.Plugin},{quadA},{quadB},{kind},{dominantA},{dominantB},{bridge}," +
                $"\"{string.Join(";", onlyInA)}\",\"{string.Join(";", onlyInB)}\"");
        }
    }
}
