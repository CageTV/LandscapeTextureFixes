// Generates a patch plugin that softens "isolated mod-vs-base" landscape
// TEXTURE seams - the companion problem to SeamFixer's height seams. Same
// scope restriction and same reasoning: only touches edges where exactly one
// side is base-game and the other is a mod, leaving multi-mod pileups (a much
// murkier "who's right" question) unfixed for now.
//
// Unlike heights, a texture layer is a discrete choice sitting in one of a
// hard-capped 6 slots per quadrant - there's no continuous value to
// interpolate. The fix here is deliberately coarse for a first pass: rather
// than trying to taper a blend across exact per-vertex boundary positions
// (AlphaLayerData.Position indexes a confirmed-by-inspection 17x17-per-
// quadrant grid, but its exact row/column ordering isn't nailed down yet),
// it adds one new AlphaLayer covering the WHOLE base-side quadrant at a
// modest, uniform opacity, carrying the mod side's dominant boundary texture.
// That's a gentle "hint" of the neighbor's texture bleeding in across the
// whole quadrant, not a precise crease-free taper - real in-game testing
// (the same way the height fixer's roughness/magnitude gates got tuned from
// real feedback) is expected to refine this further.
//
// Never touches an existing layer - only ever adds a new one, and only when
// the target quadrant has a free slot under the 6-layer cap. Skips otherwise,
// falling into the same "not auto-fixed yet" bucket as multi-mod pileups.

using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace SeamFinder.Core;

public record TextureFixResult(int CellsPatched, int LayersAdded, int LayersBoosted, int SkippedQuadrantFull, string OutputPath);

public static class TextureLayerFixer
{
    // Deliberately subtle - this is a "hint" bleeding in from the neighbor,
    // not a replacement. A full-strength (near-1.0) uniform layer across the
    // whole quadrant would just look like the neighbor's texture abruptly
    // replacing the existing one over a big area, trading one hard edge for
    // another. Chosen conservatively; expect this to get tuned from real
    // in-game feedback the same way BlendWidth/MaxRoughnessForAutoFix were.
    const float BlendOpacity = 0.35f;

    // Verified empirically against real game data (Skyrim.esm + all 3 DLCs +
    // SLaWF): the true observed maximum is 7 layers total per quadrant - 1
    // mandatory base layer plus up to 6 alpha layers. `currentCount` below
    // counts ALL layers (base + alpha), so the cap here must be 7, not 6 -
    // using 6 would incorrectly treat a quadrant with room for one more
    // alpha layer as already full.
    const int MaxLayersPerQuadrant = 7;

    // Trust now comes from the shared TrustResolver (same pool SeamFixer
    // uses: game masters, USSEP, LWF family, URF, Northern Roads if opted
    // in, custom user-trusted plugins, masters-detected patches, ...) -
    // this is what upgrades the old base-game-only scope to real mod-vs-mod
    // (e.g. Northern Roads vs Archwood), not just mod-vs-vanilla.

    // Texture EditorID prefixes trusted regardless of which plugin's cell
    // they showed up in - e.g. "COTN" for Northern Roads' own road/path
    // textures. Lets a specific, named texture set be trusted even from a
    // plugin that isn't (or shouldn't be) fully trusted overall, without
    // requiring the whole mod to join TrustResolver.BaseGamePlugins. A
    // texture matching one of these is always an acceptable correction
    // source, on top of (not instead of) the ordinary trusted-plugin path.
    static IReadOnlyList<string> _trustedTexturePrefixes = [];

    static bool IsTrustedTexture(FormKey texture, ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache)
    {
        if (_trustedTexturePrefixes.Count == 0) return false;
        if (!linkCache.TryResolve<ILandscapeTextureGetter>(texture, out var ltex) || ltex.EditorID is null) return false;
        foreach (var prefix in _trustedTexturePrefixes)
            if (ltex.EditorID.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    record CellData(
        Dictionary<Quadrant, List<IBaseLayerGetter>> LayersByQuadrant,
        Dictionary<Quadrant, Dictionary<FormKey, float>> OpacityByQuadrant,
        string Plugin,
        ILandscapeGetter SourceLandscape,
        IModContext<ISkyrimMod, ISkyrimModGetter, Cell, ICellGetter> CellContext);

    // Bridge: the chosen texture is already present (as a base or weak alpha
    // layer) in the quadrant being fixed - boost its existing presence.
    // Foreign: nothing shared between the two sides at all - add the mod
    // side's dominant texture as a brand-new layer, same as v1's only mode.
    // Fallback: nothing shared between the two sides AND there's no trust
    // relationship to decide a "source" (the 2026-09-11 intra-cell case,
    // below) - add a hint of VANILLA's own dominant texture at that exact
    // quadrant instead of guessing which mod's texture should win. Same
    // "vanilla as neutral ground truth" pattern SeamFixer/RoadMaskMerger/
    // FloatingObjectFixer already use elsewhere in this toolkit.
    enum FixKind { Bridge, Foreign, Fallback }

    record Correction(Quadrant Quadrant, FormKey Texture, float Severity, FixKind Kind);

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

    // The two "internal" seams running through the MIDDLE of every single
    // cell - BottomLeft|BottomRight and TopLeft|TopRight split the cell down
    // its own vertical center; BottomLeft|TopLeft and BottomRight|TopRight
    // split it down its own horizontal center. Confirmed as a real, entirely
    // unhandled gap on 2026-09-11: the cross-cell EastPairs/NorthPairs above
    // only ever compare a quadrant against its counterpart in a NEIGHBORING
    // cell - two quadrants of the SAME cell disagreeing with each other was
    // never checked at all, even though it's the same kind of hard-texture-
    // cutoff seam, just one level smaller in scale. Since both quadrants of
    // an intra-cell pair come from the exact same plugin's own cell (there's
    // no "which mod is more trustworthy" question to answer the way there is
    // across a cell boundary), the fix here is symmetric bridging/fallback
    // rather than one-sided "source vs fixed" - see RecordIntraCellSeams.
    static readonly (Quadrant A, Quadrant B)[] IntraCellPairs =
    {
        (Quadrant.BottomLeft, Quadrant.BottomRight),
        (Quadrant.TopLeft, Quadrant.TopRight),
        (Quadrant.BottomLeft, Quadrant.TopLeft),
        (Quadrant.BottomRight, Quadrant.TopRight),
    };

    public static TextureFixResult GenerateFixPluginForResolvedPlugins(
        List<Mo2Resolver.ResolvedPlugin> loadOrder,
        string outputPluginName,
        string outputDirectory,
        Action<string> log,
        bool trustNorthernRoads = false,
        IReadOnlyList<string>? customTrustedPlugins = null,
        IReadOnlyList<string>? trustedTexturePrefixes = null)
    {
        var mergedFolder = Path.Combine(Path.GetTempPath(), "TextureFixerMerged-" + Guid.NewGuid().ToString("N"));
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
            var mastersByPlugin = BuildMastersByPlugin(env.LoadOrder.ListedOrder);
            return GenerateFixPluginCore(env.LinkCache, priorityIndex, mergedFolder, outputPluginName, outputDirectory, log,
                trustNorthernRoads, customTrustedPlugins ?? [], trustedTexturePrefixes ?? [], mastersByPlugin);
        }
        finally
        {
            try { Directory.Delete(mergedFolder, recursive: true); }
            catch (Exception ex) { log($"(could not clean up temp folder {mergedFolder}: {ex.Message})"); }
        }
    }

    // Same shape as SeamFixer.BuildMastersByPlugin - kept as its own copy
    // rather than shared, since it's a two-line adapter with no logic to
    // drift; TrustResolver itself is what's actually shared.
    static Dictionary<string, HashSet<string>> BuildMastersByPlugin(IEnumerable<Mutagen.Bethesda.Plugins.Order.IModListingGetter<ISkyrimModGetter>> listedOrder)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var listing in listedOrder)
        {
            if (listing.Mod is null) continue;
            result[listing.ModKey.FileName] = listing.Mod.ModHeader.MasterReferences
                .Select(m => m.Master.FileName.String)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        return result;
    }

    public static TextureFixResult GenerateFixPluginForDirectDataFolder(
        string dataFolderPath,
        string outputPluginName,
        string outputDirectory,
        Action<string> log,
        bool trustNorthernRoads = false,
        IReadOnlyList<string>? customTrustedPlugins = null,
        IReadOnlyList<string>? trustedTexturePrefixes = null)
    {
        using var env = GameEnvironmentBuilder<ISkyrimMod, ISkyrimModGetter>
            .Create(GameRelease.SkyrimSE)
            .WithTargetDataFolder(dataFolderPath)
            .Build();

        var priorityIndex = env.LoadOrder.ListedOrder
            .Select((listing, idx) => (listing.ModKey, idx))
            .ToDictionary(x => x.ModKey, x => x.idx);

        var mastersByPlugin = BuildMastersByPlugin(env.LoadOrder.ListedOrder);
        return GenerateFixPluginCore(env.LinkCache, priorityIndex, dataFolderPath, outputPluginName, outputDirectory, log,
            trustNorthernRoads, customTrustedPlugins ?? [], trustedTexturePrefixes ?? [], mastersByPlugin);
    }

    static TextureFixResult GenerateFixPluginCore(
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        Dictionary<ModKey, int> priorityIndex,
        string dataFolderForWrite,
        string outputPluginName,
        string outputDirectory,
        Action<string> log,
        bool trustNorthernRoads,
        IReadOnlyList<string> customTrustedPlugins,
        IReadOnlyList<string> trustedTexturePrefixes,
        Dictionary<string, HashSet<string>> mastersByPlugin)
    {
        TrustResolver.SetMastersContext(mastersByPlugin, outputPluginName);
        // Auto-include Northern Roads' own "COTN" road/path texture prefix
        // whenever Northern Roads trust is on, added 2026-09-14 per direct
        // user request ("make sure the textures from northern roads that
        // start with COTN are... winning") - previously this required BOTH
        // ticking "Trust Northern Roads" AND separately remembering to type
        // "COTN" into the trusted-texture-prefixes box, two settings that
        // needed to agree with each other for the one thing most users
        // actually want when they enable Northern Roads trust at all. Still
        // additive, not exclusive - anything the user already typed in that
        // box is kept alongside this.
        _trustedTexturePrefixes = trustNorthernRoads && !trustedTexturePrefixes.Contains("COTN", StringComparer.OrdinalIgnoreCase)
            ? [.. trustedTexturePrefixes, "COTN"]
            : trustedTexturePrefixes;
        var outputModKey = ModKey.FromNameAndExtension(outputPluginName);
        var patchMod = new SkyrimMod(outputModKey, SkyrimRelease.SkyrimSE);

        // Pass 1: decode every exterior cell's texture layers, keeping the
        // TRUE overall winning CELL's own mod-context (not necessarily the
        // context that owns the winning Landscape below - a later plugin
        // can win the Cell overall without ever redeclaring Landscape) so
        // we can write an override later without losing that cell's other
        // current state.
        var cellsByWorldspace = new Dictionary<FormKey, Dictionary<(int X, int Y), CellData>>();

        // Blanket road-texture preservation (2026-09-14, user request):
        // distinct from every OTHER correction pass in this file, which
        // reacts to a detected SEAM between two disagreeing quadrants/cells.
        // This one isn't boundary-driven - it catches a WHOLESALE ABSENCE:
        // whenever a later, non-Northern-Roads plugin completely re-authors a
        // cell's Landscape record, that plugin's own copy simply never knew
        // Northern Roads' road-texture layer existed, so it's just missing,
        // with no "edge" for the hard-edge/faint-presence scanners to ever
        // find. Same failure class RoadMaskMerger.cs's own 2026-09-14 fix
        // addresses for its own narrower (RoadMaskMerger-touched-cells-only)
        // scope - this closes it for EVERY cell in the worldspace, gated the
        // same way every other Northern-Roads-specific behavior in this file
        // is (trustNorthernRoads).
        //
        // GENERALIZED same day, same user request as RoadMaskMerger's own
        // fix: the first cut matched a hardcoded "COTN" EditorID prefix -
        // Northern Roads' own naming convention. Replaced with a PROVENANCE
        // check instead (see the layer loop below): was this texture record
        // ITSELF originally defined by Northern Roads or a genuine patch of
        // it? Same principle as RoadMaskMerger.cs's identical fix - more
        // correct than a prefix, not just more general, since it can't
        // false-positive on an unrelated texture sharing a naming
        // convention. Still tied to "Northern Roads" specifically (this
        // tool's `trustNorthernRoads` switch, not an arbitrary configurable
        // road-source plugin like RoadMaskMerger's `--road-source=` - this
        // tool has no equivalent parameter, since Northern Roads is one
        // named trust tier among several here, not a single generic
        // concept the way it is in RoadMaskMerger).
        //
        // Keyed the same shape as `corrections` below so it can be unioned
        // into the same per-cell write loop and reuse all its existing
        // layer-count/cap bookkeeping.
        var roadTexturePreservations = new Dictionary<(FormKey Worldspace, int X, int Y), List<(Quadrant Quadrant, IAlphaLayerGetter SourceLayer)>>();

        foreach (var context in linkCache.WinningContextOverrides<Cell, ICellGetter>(linkCache))
        {
            var cell = context.Record;
            if (cell.Grid is null) continue;
            if (!context.TryGetParentSimpleContext<IWorldspaceGetter>(out var wsContext)) continue;
            var worldspaceKey = wsContext.Record.FormKey;

            var (landscape, ownerModKey) = HeightmapDecoder.ResolveWinningLandscape(cell.FormKey, linkCache, priorityIndex);
            if (landscape?.Layers is null) continue;

            var layersByQuadrant = DecodeLayers(landscape.Layers);
            var opacityByQuadrant = TextureLayerDetector.DecodeOpacityByQuadrant(landscape.Layers);

            if (!cellsByWorldspace.TryGetValue(worldspaceKey, out var cellDict))
            {
                cellDict = new Dictionary<(int, int), CellData>();
                cellsByWorldspace[worldspaceKey] = cellDict;
            }
            cellDict[(cell.Grid.Point.X, cell.Grid.Point.Y)] =
                new CellData(layersByQuadrant, opacityByQuadrant, ownerModKey.FileName, landscape, context);

            if (!trustNorthernRoads) continue;

            // Find the highest-priority Northern-Roads-or-its-patch context
            // that ALSO touches this exact cell's Landscape - mirrors
            // RoadMaskMerger.cs's own "prefer the genuine patch over the
            // plain base plugin" reasoning (a patch's road texture work is
            // the more authoritative one where both exist).
            ILandscapeGetter? nrLandscape = null;
            var nrIndex = -1;
            foreach (var ctx in linkCache.ResolveAllContexts<Cell, ICellGetter>(cell.FormKey, ResolveTarget.Winner))
            {
                if (ctx.Record.Landscape is null) continue;
                if (!TrustResolver.IsNorthernRoadsOrItsPatch(ctx.ModKey.FileName, trustNorthernRoads)) continue;
                var idx = priorityIndex.GetValueOrDefault(ctx.ModKey, -1);
                if (idx > nrIndex) { nrIndex = idx; nrLandscape = ctx.Record.Landscape; }
            }
            if (nrLandscape?.Layers is null) continue;

            foreach (var nrLayer in nrLandscape.Layers)
            {
                if (nrLayer is not IAlphaLayerGetter nrAlpha) continue;
                string textureOwner = nrAlpha.Header.Texture.FormKey.ModKey.FileName;
                if (!TrustResolver.IsNorthernRoadsOrItsPatch(textureOwner, trustNorthernRoads)) continue;

                var alreadyPresent = layersByQuadrant.GetValueOrDefault(nrAlpha.Header.Quadrant)?
                    .Any(l => l.Header.Texture.FormKey == nrAlpha.Header.Texture.FormKey) ?? false;
                if (alreadyPresent) continue;

                var key = (worldspaceKey, cell.Grid.Point.X, cell.Grid.Point.Y);
                if (!roadTexturePreservations.TryGetValue(key, out var list))
                {
                    list = new();
                    roadTexturePreservations[key] = list;
                }
                list.Add((nrAlpha.Header.Quadrant, nrAlpha));
            }
        }

        log($"Decoded {cellsByWorldspace.Sum(kv => kv.Value.Count)} cells across {cellsByWorldspace.Count} worldspace(s). " +
            $"{roadTexturePreservations.Count} cell(s) are missing a Northern Roads texture layer entirely (no boundary - a wholesale absence).");

        // Pass 2: find ModVsBase texture-set mismatches, keyed by the base-
        // game cell + quadrant that needs a blend hint added.
        var corrections = new Dictionary<(FormKey Worldspace, int X, int Y), List<Correction>>();

        // Pass 2b: real per-vertex hard-edge ("straight line") seams along
        // each cell's own internal quadrant boundaries - see
        // TextureBoundaryAnalyzer.cs. Kept in its own dictionary rather than
        // folded into `corrections` above: a HardEdgeSeam carries a full
        // per-vertex taper, not a single flat opacity, so it needs its own
        // application logic in the write loop below (see hardEdgeTapers'
        // use there).
        var hardEdgeTapers = new Dictionary<(FormKey Worldspace, int X, int Y), List<(Quadrant Quadrant, FormKey Texture, Dictionary<int, float> Positions)>>();

        foreach (var (wsKey, cellDict) in cellsByWorldspace)
        {
            foreach (var ((x, y), cellA) in cellDict)
            {
                if (cellDict.TryGetValue((x + 1, y), out var cellBEast))
                    RecordIfTrustedVsUntrusted(corrections, wsKey, x, y, cellA, x + 1, y, cellBEast, EastPairs, linkCache, trustNorthernRoads, customTrustedPlugins);
                if (cellDict.TryGetValue((x, y + 1), out var cellBNorth))
                    RecordIfTrustedVsUntrusted(corrections, wsKey, x, y, cellA, x, y + 1, cellBNorth, NorthPairs, linkCache, trustNorthernRoads, customTrustedPlugins);

                // RecordIntraCellSeams call DISABLED 2026-09-11 - see its own
                // header comment. The whole-quadrant "dominant texture
                // differs" trigger it used is too broad: normal, deliberate
                // terrain variety within a single cell (a rock outcrop in
                // one corner, say) ALWAYS differs by that measure, so it
                // fired on huge numbers of cells that were never actually
                // broken - including custom worldspaces like Wyrmstooth that
                // have nothing to do with the reported bug - and a live test
                // run crashed on a FormKey collision while writing the
                // resulting flood of corrections. Keeping the method and its
                // supporting pieces (AddCorrection, FixKind.Fallback,
                // TryGetVanillaOpacityByQuadrant) - they're the right
                // building blocks, just need a real per-vertex hard-edge
                // check driving them instead of this whole-quadrant
                // heuristic. Re-enable only once that per-vertex trigger
                // replaces the dominant-texture-mismatch check inside it.

                foreach (var (quadA, quadB, isEastWest) in TextureBoundaryAnalyzer.IntraCellBoundaries)
                {
                    // Strict pass (long, full-strength cutoffs) + the looser
                    // faint-presence pass (short/low-opacity trusted accent
                    // touches, e.g. a Northern Roads trail vertex at a
                    // quadrant corner - see TrustedPresenceThreshold's own
                    // comment, added 2026-09-14 after real profile data
                    // showed a single full-strength COTN vertex on one side
                    // of a corner with the SAME texture entirely absent on
                    // the other, which the strict 5-vertex-run pass can
                    // never catch). Both funnel through the identical trust
                    // filter and taper-building below; a texture flagged by
                    // both passes safely merges (max per vertex) at write
                    // time - see the write loop's own comment on this.
                    var candidateSeams = TextureBoundaryAnalyzer.FindHardEdgeSeams(quadA, quadB, isEastWest, cellA.LayersByQuadrant)
                        .Concat(TextureBoundaryAnalyzer.FindFaintPresenceGaps(quadA, quadB, isEastWest, cellA.LayersByQuadrant));
                    foreach (var seam in candidateSeams)
                    {
                        // Gate on the SAME texture-level trust signal
                        // RecordIfTrustedVsUntrusted's case 2 already uses
                        // (_trustedTexturePrefixes, e.g. "COTN") - required
                        // here, confirmed necessary on the very first live
                        // test (2026-09-11, H:\TB_test): without it, this
                        // fired on 5027 of a SMALL 6-plugin test list's
                        // cells, virtually all plain Skyrim.esm vanilla
                        // textures meeting at an ordinary quadrant boundary -
                        // real, but completely normal terrain painting, not
                        // the cross-mod seam the user actually reported.
                        // Only the texture doing the actual cutting-off (the
                        // STRONG side, the one about to get tapered into its
                        // neighbor) needs to be trusted - that's specifically
                        // "a road/path mod's own texture painted with no
                        // regard for what's next to it," which is the real,
                        // reported failure mode; an untrusted, ordinary
                        // texture cutting off at a quadrant boundary is just
                        // how terrain is usually painted and isn't this
                        // tool's business to touch.
                        if (!IsTrustedTexture(seam.Texture, linkCache)) continue;

                        // Which physical edge of the WEAK quadrant faces the
                        // strong one - see FindHardEdgeSeams' strongIsA
                        // convention: quadA at strongIsA=true used boundary
                        // index 16, so quadB (weak) faces it at index 0, and
                        // vice versa.
                        int weakBoundaryIndex = seam.StrongQuadrant == quadA ? 0 : 16;
                        var taper = TextureBoundaryAnalyzer.BuildTaper(seam, weakBoundaryIndex);
                        if (taper.Count == 0) continue;

                        var key = (wsKey, x, y);
                        if (!hardEdgeTapers.TryGetValue(key, out var list))
                        {
                            list = new();
                            hardEdgeTapers[key] = list;
                        }
                        list.Add((seam.WeakQuadrant, seam.Texture, taper));
                        log($"  Hard-edge seam at ({x},{y}) {seam.StrongQuadrant}|{seam.WeakQuadrant}: {seam.Texture} cuts straight across {seam.BoundaryOpacity.Count} boundary vertices with no taper - blending {taper.Count} vertices into {seam.WeakQuadrant}.");
                    }
                }
            }
        }

        log($"Found {corrections.Count} cells with a cross-cell texture seam and {hardEdgeTapers.Count} cells with an intra-cell hard-edge seam that need a blend hint.");

        var allCellKeys = corrections.Keys.Union(hardEdgeTapers.Keys).Union(roadTexturePreservations.Keys).ToList();
        int cellsPatched = 0, layersAdded = 0, layersBoosted = 0, skippedFull = 0, hardEdgeSkippedOverlap = 0, roadTextureLayersPreserved = 0;
        foreach (var (wsKey, x, y) in allCellKeys)
        {
            var correctionList = corrections.GetValueOrDefault((wsKey, x, y)) ?? new List<Correction>();
            var taperList = hardEdgeTapers.GetValueOrDefault((wsKey, x, y)) ?? new();
            var cellData = cellsByWorldspace[wsKey][(x, y)];
            var newLayers = new List<AlphaLayer>();
            var boostedLayers = new List<AlphaLayer>(); // replacements for existing layers, keyed by (Quadrant, Texture) at write time

            // Track per-quadrant layer counts as we go, since a cell can
            // need corrections on more than one quadrant (or, in principle,
            // more than one texture queued for the same quadrant from two
            // different neighbors) and each addition eats into the same cap.
            // Boosting an existing layer doesn't change the count, only
            // adding a brand-new one does.
            var currentCount = new Dictionary<Quadrant, int>();
            foreach (var q in new[] { Quadrant.BottomLeft, Quadrant.BottomRight, Quadrant.TopLeft, Quadrant.TopRight })
                currentCount[q] = cellData.LayersByQuadrant.GetValueOrDefault(q)?.Count ?? 0;

            // Track the next alpha-layer number and which textures already
            // occupy each quadrant as a running total, since two DIFFERENT
            // neighbor comparisons can legitimately target the same physical
            // quadrant (e.g. BottomRight is both the East-facing and South-
            // facing corner) - without this, a second correction landing on
            // the same quadrant would recompute both from the ORIGINAL
            // pre-correction data, risking a duplicate layer with a colliding
            // LayerNumber. A plain BaseLayer's LayerNumber is a 65535
            // sentinel (it's conceptually "underneath everything," not part
            // of the alpha stack's sequential numbering) - excluded from the
            // running max so it doesn't get treated as a real high number.
            var nextLayerNumber = new Dictionary<Quadrant, int>();
            var texturesAlreadyQueued = new Dictionary<Quadrant, HashSet<FormKey>>();
            foreach (var q in new[] { Quadrant.BottomLeft, Quadrant.BottomRight, Quadrant.TopLeft, Quadrant.TopRight })
            {
                var existing = cellData.LayersByQuadrant.GetValueOrDefault(q);
                var alphaNumbers = existing?.OfType<IAlphaLayerGetter>().Select(l => (int)l.Header.LayerNumber).ToList();
                nextLayerNumber[q] = (alphaNumbers?.Count > 0 ? alphaNumbers.Max() : -1) + 1;
                texturesAlreadyQueued[q] = new HashSet<FormKey>();
            }

            foreach (var correction in correctionList.OrderByDescending(c => c.Severity))
            {
                if (!texturesAlreadyQueued[correction.Quadrant].Add(correction.Texture))
                    continue; // already queued this exact texture for this quadrant this pass - dedupe

                var existingLayers = cellData.LayersByQuadrant.GetValueOrDefault(correction.Quadrant);
                var existingLayer = existingLayers?.FirstOrDefault(l => l.Header.Texture.FormKey == correction.Texture);

                // Regardless of how this correction was classified (Bridge vs
                // Foreign), a layer for this exact texture may already sit in
                // this quadrant - e.g. it was too weak to count as "present"
                // during opacity-weighted classification (DecodeOpacityByQuadrant
                // filters sub-MinMeaningfulOpacity layers), or it arrived here
                // from a different neighbor comparison touching the same
                // physical quadrant (a corner quadrant faces both an East and a
                // North pairing). Always boost a genuinely pre-existing layer
                // instead of adding a duplicate.
                if (existingLayer is not null)
                {
                    if (existingLayer is IAlphaLayerGetter existingAlpha)
                    {
                        boostedLayers.Add(BuildBoostedLayer(existingAlpha));
                        layersBoosted++;
                    }
                    // else it's the BaseLayer - already full coverage, nothing to boost
                    continue;
                }

                if (correction.Kind == FixKind.Bridge)
                {
                    // RecordIfModVsBase's Bridge detection guarantees the
                    // texture is present in some form, so existingLayer should
                    // never be null here - but if it somehow is, there's
                    // nothing safe to boost, so skip rather than guess.
                    continue;
                }

                // Foreign or Fallback: texture isn't present here at all -
                // add as new either way, respecting the cap (boosts never
                // need a free slot, only brand-new additions do). The two
                // kinds only differ in WHICH texture ended up in
                // correction.Texture (the other mod's dominant texture for
                // Foreign, vanilla's own dominant texture for Fallback) -
                // see RecordIfTrustedVsUntrusted / RecordIntraCellSeams.
                if (currentCount[correction.Quadrant] >= MaxLayersPerQuadrant)
                {
                    skippedFull++;
                    continue;
                }

                var alphaData = new ExtendedList<AlphaLayerData>();
                for (ushort pos = 0; pos <= 288; pos++)
                    alphaData.Add(new AlphaLayerData { Position = pos, Opacity = BlendOpacity });

                newLayers.Add(new AlphaLayer
                {
                    Header = new LayerHeader
                    {
                        Texture = new FormLink<ILandscapeTextureGetter>(correction.Texture),
                        Quadrant = correction.Quadrant,
                        LayerNumber = (ushort)nextLayerNumber[correction.Quadrant],
                    },
                    AlphaLayerData = alphaData,
                });

                nextLayerNumber[correction.Quadrant]++;
                currentCount[correction.Quadrant]++;
                layersAdded++;
            }

            // Apply hard-edge tapers (Pass 2b) - separate loop from the flat
            // corrections above since each taper carries real per-vertex
            // opacity, not one flat number. Two different taper runs CAN
            // legitimately target the same (quadrant, texture) - e.g. a
            // corner quadrant bordering hard edges on two different sides -
            // so these are merged together (max per vertex) BEFORE building
            // any AlphaLayer, never added as two separate entries; adding
            // two separate boosted/new layers for the same (quadrant,
            // texture) would silently clobber each other when
            // WriteLayerChanges' RemoveAll-then-Add runs for the second one.
            // Deliberately does NOT touch a (quadrant, texture) pair the
            // correctionList loop above already touched this cell - merging
            // across the two DIFFERENT correction shapes (flat vs per-
            // vertex) safely would need real position-aware merging of an
            // already-built AlphaLayer, more risk than this pass is worth
            // taking for what should be a rare coincidence; skipped and
            // logged instead; the flat correction's own boost still stands.
            var mergedTapers = new Dictionary<(Quadrant, FormKey), Dictionary<int, float>>();
            foreach (var (quadrant, texture, positions) in taperList)
            {
                if (texturesAlreadyQueued[quadrant].Contains(texture))
                {
                    hardEdgeSkippedOverlap++;
                    log($"  ({x},{y}) {quadrant}: hard-edge taper for {texture} overlaps a cross-cell correction already handled this cell - skipped (the existing boost still applies).");
                    continue;
                }
                var mergeKey = (quadrant, texture);
                if (!mergedTapers.TryGetValue(mergeKey, out var merged))
                {
                    merged = new Dictionary<int, float>();
                    mergedTapers[mergeKey] = merged;
                }
                foreach (var (pos, opacity) in positions)
                    merged[pos] = Math.Max(merged.GetValueOrDefault(pos), opacity);
            }

            foreach (var ((quadrant, texture), positions) in mergedTapers)
            {
                var existingLayers = cellData.LayersByQuadrant.GetValueOrDefault(quadrant);
                var existingLayer = existingLayers?.FirstOrDefault(l => l.Header.Texture.FormKey == texture);

                if (existingLayer is not null)
                {
                    if (existingLayer is IAlphaLayerGetter existingAlpha)
                    {
                        boostedLayers.Add(BuildTaperedBoost(existingAlpha, positions));
                        layersBoosted++;
                    }
                    // else it's the BaseLayer - already full coverage, nothing to boost
                    continue;
                }

                if (currentCount[quadrant] >= MaxLayersPerQuadrant)
                {
                    skippedFull++;
                    continue;
                }

                var alphaData = new ExtendedList<AlphaLayerData>();
                foreach (var (pos, opacity) in positions.OrderBy(kv => kv.Key))
                    alphaData.Add(new AlphaLayerData { Position = (ushort)pos, Opacity = opacity });

                newLayers.Add(new AlphaLayer
                {
                    Header = new LayerHeader
                    {
                        Texture = new FormLink<ILandscapeTextureGetter>(texture),
                        Quadrant = quadrant,
                        LayerNumber = (ushort)nextLayerNumber[quadrant],
                    },
                    AlphaLayerData = alphaData,
                });

                nextLayerNumber[quadrant]++;
                currentCount[quadrant]++;
                layersAdded++;
            }

            // Road-texture preservation (2026-09-14): unlike everything
            // above, this isn't reacting to a detected seam - it's ensuring
            // Northern Roads' own road-texture layers for THIS cell survive
            // at all, even where nothing about them looks like a boundary
            // mismatch. Uses the REAL per-vertex AlphaLayerData Northern
            // Roads (or its patch) authored, copied verbatim - never a
            // synthesized blend/taper like the corrections above, since this
            // is preservation, not correction.
            foreach (var (quadrant, nrAlpha) in roadTexturePreservations.GetValueOrDefault((wsKey, x, y)) ?? [])
            {
                var texture = nrAlpha.Header.Texture.FormKey;
                if (!texturesAlreadyQueued[quadrant].Add(texture)) continue;

                var existingLayers = cellData.LayersByQuadrant.GetValueOrDefault(quadrant);
                if (existingLayers?.Any(l => l.Header.Texture.FormKey == texture) == true) continue;

                if (currentCount[quadrant] >= MaxLayersPerQuadrant)
                {
                    skippedFull++;
                    log($"  ({x},{y}) {quadrant}: could not preserve Northern Roads' own texture layer - quadrant already at the {MaxLayersPerQuadrant}-layer cap.");
                    continue;
                }

                var preservedData = new ExtendedList<AlphaLayerData>();
                foreach (var d in nrAlpha.AlphaLayerData)
                    preservedData.Add(new AlphaLayerData { Position = d.Position, Opacity = d.Opacity });

                newLayers.Add(new AlphaLayer
                {
                    Header = new LayerHeader
                    {
                        Texture = new FormLink<ILandscapeTextureGetter>(texture),
                        Quadrant = quadrant,
                        LayerNumber = (ushort)nextLayerNumber[quadrant],
                    },
                    AlphaLayerData = preservedData,
                });

                nextLayerNumber[quadrant]++;
                currentCount[quadrant]++;
                layersAdded++;
                roadTextureLayersPreserved++;
            }

            if (newLayers.Count == 0 && boostedLayers.Count == 0) continue;

            WriteLayerChanges(cellData.CellContext, patchMod, cellData.SourceLandscape, newLayers, boostedLayers);
            cellsPatched++;
        }

        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, outputPluginName);

        var eslResult = EslEligibility.CheckAndFlag(patchMod);
        log(eslResult.Summary);

        log($"Writing patch plugin to {outputPath} ...");
        SkyrimMod.WriteBuilder(SkyrimRelease.SkyrimSE)
            .ToPath(outputPath, fileSystem: null)
            .WithNoLoadOrder()
            .WithDataFolder(dataFolderForWrite)
            .WithAllParentMasters()
            .Write(patchMod);

        log($"Patched {cellsPatched} cells ({layersAdded} layers added, {layersBoosted} existing layers boosted, {skippedFull} skipped - quadrant already at the {MaxLayersPerQuadrant}-layer cap" +
            (hardEdgeSkippedOverlap > 0 ? $", {hardEdgeSkippedOverlap} hard-edge taper(s) skipped for overlapping a cross-cell correction" : "") +
            (roadTextureLayersPreserved > 0 ? $", {roadTextureLayersPreserved} of which were Northern Roads texture layer(s) preserved (wholesale absence, not a seam)" : "") + ").");
        return new TextureFixResult(cellsPatched, layersAdded, layersBoosted, skippedFull, outputPath);
    }

    // ResolveWinningLandscape now lives in HeightmapDecoder.cs, shared with
    // SeamFixer.cs (write path below still targets the outer, TRUE winning
    // Cell context, and only uses this function's result as source data to
    // deep-copy and correct - never as the context to write into, since the
    // Landscape owner can differ from whoever wins the Cell overall).

    static Dictionary<Quadrant, List<IBaseLayerGetter>> DecodeLayers(IReadOnlyList<IBaseLayerGetter> layers)
    {
        var byQuadrant = new Dictionary<Quadrant, List<IBaseLayerGetter>>
        {
            [Quadrant.BottomLeft] = new(), [Quadrant.BottomRight] = new(),
            [Quadrant.TopLeft] = new(), [Quadrant.TopRight] = new(),
        };

        foreach (var layer in layers)
        {
            if (layer?.Header is null) continue;
            byQuadrant[layer.Header.Quadrant].Add(layer);
        }

        return byQuadrant;
    }

    // Two DIFFERENT trust semantics decide a corrections, checked in this
    // priority order (see the full rationale in the function body below,
    // 2026-09-14 rework):
    //
    // 1. WINS/propagates (highest confidence - a deliberate, specific, named
    //    edit): Northern Roads/its patches (if opted in), or a trusted-
    //    texture-prefix match (e.g. "COTN") on either side. This side's
    //    texture is what spreads INTO its neighbor.
    //
    // 2. PASSIVE default (generalized from the original mod-vs-BASE-GAME-
    //    only scope, otherwise unchanged): exactly one side is generically
    //    trusted per TrustResolver (game masters, USSEP, LWF, URF, custom-
    //    trusted mods, masters-detected patches - Northern Roads excluded,
    //    see #1) - this side gently receives a hint of whatever specific
    //    mod edit sits next to it, not the other way around.
    //
    // Two kinds of fix either way, see FixKind: prefer a texture already
    // shared between both quadrants (just not dominant at the boundary)
    // over introducing a completely foreign one, since boosting something
    // already believably present nearby reads far more natural than
    // grafting in something new.
    static void RecordIfTrustedVsUntrusted(
        Dictionary<(FormKey, int, int), List<Correction>> corrections,
        FormKey ws, int ax, int ay, CellData a, int bx, int by, CellData b,
        (Quadrant A, Quadrant B)[] pairs,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        bool trustNorthernRoads, IReadOnlyList<string> customTrustedPlugins)
    {
        // REWORKED 2026-09-14 after a direct user report that Northern Roads'
        // own road texture was NOT winning/propagating even with "Trust
        // Northern Roads" enabled - confirmed via code tracing as two
        // compounding real bugs, not a misunderstanding:
        //
        // 1. WRONG DIRECTION: TrustResolver.IsBaseGamePlugin lumps Northern
        //    Roads into the SAME "trusted" bucket as vanilla/USSEP/LWF/URF,
        //    and the old logic below treated ANY trusted plugin as the
        //    PASSIVE side (receives a hint of whatever sits next to it) -
        //    correct for genuinely-neutral vanilla ground, but backward for
        //    Northern Roads, whose road texture IS the deliberate edit that
        //    should WIN and propagate outward, exactly like a trusted-
        //    texture-prefix (COTN) match already does correctly below.
        // 2. UNREACHABLE CODE: the old logic checked plugin-level trust
        //    FIRST and only fell through to the texture-prefix check when
        //    plugin trust was a TIE (both sides equally trusted or equally
        //    untrusted) - so the COTN-prefix path, which already had the
        //    right direction, could never fire on the single most common
        //    real case: Northern Roads (now plugin-trusted) directly
        //    bordering a fully-untrusted mod.
        //
        // Fix: split into two DIFFERENT trust semantics instead of one bucket.
        // "Wins/propagates" (checked FIRST, highest confidence - a deliberate,
        // specific, named edit): Northern Roads/its patches, OR a trusted-
        // texture-prefix match on either side. "Passive default" (checked
        // SECOND, UNCHANGED from v1): the remaining generic trust pool -
        // vanilla/USSEP/LWF/URF/custom-trusted plugins - still gently
        // receives a hint of whatever specific mod edit sits next to it,
        // exactly as before. Northern Roads is deliberately EXCLUDED from
        // the generic bucket now so it can never fall back into the old,
        // backward "passive" behavior.
        var aIsNorthernRoads = TrustResolver.IsNorthernRoadsOrItsPatch(a.Plugin, trustNorthernRoads);
        var bIsNorthernRoads = TrustResolver.IsNorthernRoadsOrItsPatch(b.Plugin, trustNorthernRoads);

        bool IsGenericTrust(string plugin) =>
            TrustResolver.IsTrustedBaseOrPatch(plugin) || TrustResolver.MatchesCustomTrust(plugin, customTrustedPlugins);
        var aGenericTrusted = IsGenericTrust(a.Plugin);
        var bGenericTrusted = IsGenericTrust(b.Plugin);
        var genericTrustDecides = aGenericTrusted != bGenericTrusted;

        foreach (var (quadA, quadB) in pairs)
        {
            var opacityA = a.OpacityByQuadrant[quadA];
            var opacityB = b.OpacityByQuadrant[quadB];
            if (opacityA.Count == 0 || opacityB.Count == 0) continue;

            var dominantA = opacityA.OrderByDescending(kv => kv.Value).First().Key;
            var dominantB = opacityB.OrderByDescending(kv => kv.Value).First().Key;
            if (dominantA == dominantB) continue; // already matches - no seam here

            var aWinsSignal = aIsNorthernRoads || IsTrustedTexture(dominantA, linkCache);
            var bWinsSignal = bIsNorthernRoads || IsTrustedTexture(dominantB, linkCache);

            // FIXED 2026-09-15 (real user report): BOTH sides having a wins
            // signal used to fall all the way through to "ambiguous - not
            // our scope" and do nothing at all - the exact real case a user
            // reported: two different Northern Roads patches (or Northern
            // Roads vs one of its own patches) painting DIFFERENT specific
            // road textures (e.g. COTN_LDirtDry01 vs COTN_LDirtDry02) right
            // up against each other. Neither texture is "wrong" and neither
            // side should just overwrite the other the way the single-
            // winner path below does - this is a genuine two-way BLEND
            // case, not a wins-vs-passive one. Handled separately, before
            // the single-winner logic, so that logic's own "not our scope"
            // fallback only ever means "neither side had any signal at all"
            // (its original, narrower meaning).
            if (aWinsSignal && bWinsSignal)
            {
                var sharedBoth = opacityA.Keys.Where(opacityB.ContainsKey).ToList();
                var bridgeBoth = sharedBoth.Count > 0
                    ? sharedBoth.OrderByDescending(t => opacityA[t] + opacityB[t]).First()
                    : (FormKey?)null;

                if (bridgeBoth is FormKey bridgeBothTexture)
                {
                    var severityA = Math.Abs(opacityA.GetValueOrDefault(dominantA) - opacityA.GetValueOrDefault(bridgeBothTexture));
                    var severityB = Math.Abs(opacityB.GetValueOrDefault(dominantB) - opacityB.GetValueOrDefault(bridgeBothTexture));
                    AddCorrection(corrections, ws, ax, ay, quadA, bridgeBothTexture, severityA, FixKind.Bridge);
                    AddCorrection(corrections, ws, bx, by, quadB, bridgeBothTexture, severityB, FixKind.Bridge);
                }
                else
                {
                    // Nothing shared at all - introduce a hint of EACH
                    // side's own dominant texture into the OTHER side, so
                    // the transition softens from both directions instead
                    // of only one.
                    var severityBothForeign = Math.Abs(opacityA.GetValueOrDefault(dominantA) - opacityB.GetValueOrDefault(dominantB));
                    AddCorrection(corrections, ws, bx, by, quadB, dominantA, severityBothForeign, FixKind.Foreign);
                    AddCorrection(corrections, ws, ax, ay, quadA, dominantB, severityBothForeign, FixKind.Foreign);
                }
                continue;
            }

            bool? aIsSource =
                aWinsSignal && !bWinsSignal ? true
                : bWinsSignal && !aWinsSignal ? false
                // Neither side has any wins signal at all - fall back to
                // the original, UNCHANGED generic-trust-as-passive-default
                // behavior.
                : genericTrustDecides ? !aGenericTrusted
                : null; // no signal at all for this edge - not our scope
            if (aIsSource is not bool aSource) continue;

            var shared = opacityA.Keys.Where(opacityB.ContainsKey).ToList();
            // Best bridge candidate: highest combined presence across both
            // sides, so the boost lands on something already reasonably
            // established in the area rather than a barely-there trace.
            var bridge = shared.Count > 0
                ? shared.OrderByDescending(t => opacityA[t] + opacityB[t]).First()
                : (FormKey?)null;

            var severity = Math.Abs(opacityA.GetValueOrDefault(dominantA) - (bridge is FormKey br ? opacityA.GetValueOrDefault(br) : 0));

            if (bridge is FormKey bridgeTexture)
            {
                // Bridge is present on BOTH sides by construction (it's in
                // opacityA ∩ opacityB) - the fixed side always gets boosted.
                if (aSource)
                    AddCorrection(corrections, ws, bx, by, quadB, bridgeTexture, severity, FixKind.Bridge);
                else
                    AddCorrection(corrections, ws, ax, ay, quadA, bridgeTexture, severity, FixKind.Bridge);
            }
            else
            {
                // No shared texture at all - introduce the source side's
                // dominant texture on the fixed side.
                if (aSource)
                    AddCorrection(corrections, ws, bx, by, quadB, dominantA, severity, FixKind.Foreign);
                else
                    AddCorrection(corrections, ws, ax, ay, quadA, dominantB, severity, FixKind.Foreign);
            }
        }
    }

    // NOT CURRENTLY CALLED - see the disabled call site in GenerateFixPluginCore
    // above for why (too broad a trigger, caused a real write crash on a live
    // profile 2026-09-11). Kept as the scaffolding for the real fix (bridge/
    // fallback application, vanilla lookup) - needs its dominant-texture-
    // mismatch trigger replaced with a genuine per-vertex hard-edge check
    // before re-enabling.
    //
    // The intra-cell counterpart to RecordIfTrustedVsUntrusted - see
    // IntraCellPairs above for why this exists. Both quadrants in every pair
    // here belong to the SAME cell (therefore the same owning plugin), so
    // there's no "which mod is more trustworthy" signal to pick a one-sided
    // "source" the way the cross-cell version does - the fix is symmetric:
    //   1. A texture already present on BOTH quadrants (just not dominant at
    //      their shared internal edge) gets boosted on BOTH sides - the same
    //      "bridge" concept as cross-cell, just applied without a winner.
    //   2. Nothing shared at all: rather than guess which quadrant's texture
    //      should graft onto the other (there's no principled way to choose
    //      between two pieces of the SAME author's own work), fall back to
    //      VANILLA's own dominant texture at each quadrant - the same
    //      "vanilla as neutral ground truth" pattern used throughout this
    //      toolkit (SeamFixer's vanilla baseline, FloatingObjectFixer's
    //      TryGetVanillaHeights, RoadMaskMerger's genuine-edit gate). Each
    //      quadrant gets nudged toward ITS OWN vanilla texture - which can
    //      differ between the two quadrants, and that's fine: the point
    //      isn't that vanilla itself is seamless here, only that it's a
    //      safe, non-guessed source neither quadrant's own mod-added data
    //      can dispute.
    //   3. If vanilla has no landscape data for this cell at all (a custom
    //      worldspace, or the cell doesn't originate in Skyrim.esm), there's
    //      no safe fallback source - left unfixed and logged, matching this
    //      toolkit's standing rule of never guessing when there's nothing
    //      trustworthy to pull from (see SeamFixer's own header comment).
    static void RecordIntraCellSeams(
        Dictionary<(FormKey, int, int), List<Correction>> corrections,
        FormKey ws, int x, int y, CellData cell,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        Action<string> log)
    {
        Dictionary<Quadrant, Dictionary<FormKey, float>>? vanillaOpacity = null;
        bool vanillaLookedUp = false;

        foreach (var (quadA, quadB) in IntraCellPairs)
        {
            var opacityA = cell.OpacityByQuadrant[quadA];
            var opacityB = cell.OpacityByQuadrant[quadB];
            if (opacityA.Count == 0 || opacityB.Count == 0) continue;

            var dominantA = opacityA.OrderByDescending(kv => kv.Value).First().Key;
            var dominantB = opacityB.OrderByDescending(kv => kv.Value).First().Key;
            if (dominantA == dominantB) continue; // already matches - no internal seam here

            var shared = opacityA.Keys.Where(opacityB.ContainsKey).ToList();
            if (shared.Count > 0)
            {
                var bridge = shared.OrderByDescending(t => opacityA[t] + opacityB[t]).First();
                var severity = Math.Abs(opacityA.GetValueOrDefault(dominantA) - opacityA.GetValueOrDefault(bridge));
                AddCorrection(corrections, ws, x, y, quadA, bridge, severity, FixKind.Bridge);
                AddCorrection(corrections, ws, x, y, quadB, bridge, severity, FixKind.Bridge);
                continue;
            }

            // No bridge - look up vanilla's own texture per quadrant, once
            // per cell (not once per pair) since it's the same lookup every
            // time.
            if (!vanillaLookedUp)
            {
                vanillaOpacity = TryGetVanillaOpacityByQuadrant(cell, linkCache);
                vanillaLookedUp = true;
            }
            if (vanillaOpacity is null)
            {
                log($"  Intra-cell seam at ({x},{y}) {quadA}|{quadB}: no shared texture and no vanilla baseline for this cell (custom worldspace?) - left unfixed, needs manual review.");
                continue;
            }

            var severityFallback = Math.Abs(opacityA.GetValueOrDefault(dominantA) - opacityB.GetValueOrDefault(dominantB));
            if (vanillaOpacity[quadA].Count > 0)
            {
                var vanillaA = vanillaOpacity[quadA].OrderByDescending(kv => kv.Value).First().Key;
                AddCorrection(corrections, ws, x, y, quadA, vanillaA, severityFallback, FixKind.Fallback);
            }
            if (vanillaOpacity[quadB].Count > 0)
            {
                var vanillaB = vanillaOpacity[quadB].OrderByDescending(kv => kv.Value).First().Key;
                AddCorrection(corrections, ws, x, y, quadB, vanillaB, severityFallback, FixKind.Fallback);
            }
        }
    }

    // Decodes Skyrim.esm's OWN copy of this cell's texture layers, per
    // quadrant - the "default landscape texture" fallback source for
    // RecordIntraCellSeams. Null if this cell doesn't originate in
    // Skyrim.esm at all (mirrors FloatingObjectFixer.TryGetVanillaHeights'
    // exact same reasoning/limitation for the equivalent height lookup).
    static Dictionary<Quadrant, Dictionary<FormKey, float>>? TryGetVanillaOpacityByQuadrant(
        CellData cell, ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache)
    {
        foreach (var ctx in linkCache.ResolveAllContexts<Cell, ICellGetter>(cell.CellContext.Record.FormKey, ResolveTarget.Winner))
        {
            if (ctx.Record.Landscape?.Layers is null) continue;
            string fileName = ctx.ModKey.FileName;
            if (!fileName.Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase)) continue;
            return TextureLayerDetector.DecodeOpacityByQuadrant(ctx.Record.Landscape.Layers);
        }
        return null;
    }

    static void AddCorrection(
        Dictionary<(FormKey, int, int), List<Correction>> corrections,
        FormKey ws, int x, int y, Quadrant quadrant, FormKey texture, float severity, FixKind kind)
    {
        var key = (ws, x, y);
        if (!corrections.TryGetValue(key, out var list))
        {
            list = new List<Correction>();
            corrections[key] = list;
        }
        list.Add(new Correction(quadrant, texture, severity, kind));
    }

    // Builds a replacement for an existing alpha layer with every position's
    // opacity raised by BlendOpacity (clamped to 1.0), adding a fresh entry
    // at exactly BlendOpacity for positions the layer didn't already cover.
    // Header (texture/quadrant/layer number) is carried over unchanged -
    // this boosts presence, it doesn't change what or where the layer is.
    static AlphaLayer BuildBoostedLayer(IAlphaLayerGetter existing)
    {
        var byPosition = new Dictionary<ushort, float>();
        if (existing.AlphaLayerData is not null)
            foreach (var d in existing.AlphaLayerData)
                byPosition[d.Position] = d.Opacity;

        for (ushort pos = 0; pos <= 288; pos++)
        {
            var current = byPosition.GetValueOrDefault(pos);
            byPosition[pos] = Math.Min(1.0f, current + BlendOpacity);
        }

        var newData = new ExtendedList<AlphaLayerData>();
        foreach (var (pos, opacity) in byPosition.OrderBy(kv => kv.Key))
            newData.Add(new AlphaLayerData { Position = pos, Opacity = opacity });

        return new AlphaLayer
        {
            Header = new LayerHeader
            {
                Texture = new FormLink<ILandscapeTextureGetter>(existing.Header.Texture.FormKey),
                Quadrant = existing.Header.Quadrant,
                LayerNumber = existing.Header.LayerNumber,
            },
            AlphaLayerData = newData,
        };
    }

    // The hard-edge-taper counterpart to BuildBoostedLayer: merges specific
    // PER-VERTEX opacity values into an existing layer (max with whatever
    // was already there) rather than adding one flat BlendOpacity to every
    // position - this is the whole point of the per-vertex taper (see
    // TextureBoundaryAnalyzer.BuildTaper): only the vertices actually near
    // the detected hard edge get touched, everything else in the layer is
    // carried forward completely unchanged.
    static AlphaLayer BuildTaperedBoost(IAlphaLayerGetter existing, Dictionary<int, float> taperPositions)
    {
        var byPosition = new Dictionary<int, float>();
        if (existing.AlphaLayerData is not null)
            foreach (var d in existing.AlphaLayerData)
                byPosition[d.Position] = d.Opacity;

        foreach (var (pos, opacity) in taperPositions)
            byPosition[pos] = Math.Max(byPosition.GetValueOrDefault(pos), opacity);

        var newData = new ExtendedList<AlphaLayerData>();
        foreach (var (pos, opacity) in byPosition.OrderBy(kv => kv.Key))
            newData.Add(new AlphaLayerData { Position = (ushort)pos, Opacity = opacity });

        return new AlphaLayer
        {
            Header = new LayerHeader
            {
                Texture = new FormLink<ILandscapeTextureGetter>(existing.Header.Texture.FormKey),
                Quadrant = existing.Header.Quadrant,
                LayerNumber = existing.Header.LayerNumber,
            },
            AlphaLayerData = newData,
        };
    }

    static void WriteLayerChanges(
        IModContext<ISkyrimMod, ISkyrimModGetter, Cell, ICellGetter> cellContext,
        SkyrimMod patchMod,
        ILandscapeGetter sourceLandscape,
        List<AlphaLayer> newLayers,
        List<AlphaLayer> boostedLayers)
    {
        // GetOrAddAsOverride on a Cell deep-copies only its plain fields -
        // Persistent/Temporary (placed-object children: rocks, flora,
        // containers, etc.) come back empty and, since our patch loads last
        // for cells it touches, would win and silently delete every
        // reference in the cell. Preserve them explicitly. Confirmed
        // empirically against the height-seam fixer's identical pattern:
        // it wiped references in 410 of 550 patched cells in a real load
        // order before this preservation step was added.
        var writableCell = cellContext.GetOrAddAsOverride(patchMod);

        foreach (var p in cellContext.Record.Persistent)
            writableCell.Persistent.Add((IPlaced)p.DeepCopy());
        foreach (var t in cellContext.Record.Temporary)
            writableCell.Temporary.Add((IPlaced)t.DeepCopy());

        // Landscape is its own distinct major record (its own FormKey)
        // embedded in Cell, not a plain data field - it doesn't cascade
        // through GetOrAddAsOverride either. Deep-copy it explicitly, from
        // the actual winning Landscape (sourceLandscape), which may belong
        // to a different plugin than the one winning this Cell overall (see
        // ResolveWinningLandscape).
        writableCell.Landscape = sourceLandscape.DeepCopy();
        var writableLandscape = writableCell.Landscape;

        // Boosted layers replace their existing counterpart (same quadrant +
        // texture) rather than stacking a duplicate on top of it.
        foreach (var boosted in boostedLayers)
        {
            writableLandscape.Layers.RemoveAll(l =>
                l.Header.Quadrant == boosted.Header.Quadrant &&
                l.Header.Texture.FormKey == boosted.Header.Texture.FormKey);
            writableLandscape.Layers.Add(boosted);
        }

        foreach (var layer in newLayers)
            writableLandscape.Layers.Add(layer);
    }
}
