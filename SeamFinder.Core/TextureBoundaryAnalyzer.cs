// Per-vertex texture-seam ("straight line") detection and repair - the
// precise counterpart to TextureLayerFixer.cs's older whole-quadrant
// "dominant texture differs" comparison. Built 2026-09-11 after the user
// showed real in-game screenshots of a hard, razor-straight boundary
// between two ground textures (road-vs-grass, snow-vs-dirt) running clean
// across open terrain - the unmistakable signature of a texture painted
// with a hard-edged brush right up to a quadrant boundary, with zero
// falloff/blend, rather than CK's normal soft-brush taper.
//
// Key prerequisite this file depends on, confirmed empirically (throwaway
// AlphaProbe diagnostic against real cells (-3,-16) and (2,-20) in the
// user's own live profile, not kept as part of this project):
// AlphaLayerData.Position (0-288) reshapes into a 17x17 grid via
// `row = Position / 17, col = Position % 17`, where COL is the EAST-WEST
// (world X) axis and ROW is the NORTH-SOUTH (world Y) axis - same
// convention HeightmapDecoder already uses (col=x, row=y). Confirmed via a
// shared texture (LRocks01NoRocks) showing near-identical opacity across
// the real BottomLeft(col16)|BottomRight(col0) boundary, row for row
// (1.00/1.00, 1.00/1.00, 0.87/0.87), while the transposed hypothesis showed
// zero correlation (one side flat 0.00 throughout). This also means each
// quadrant's 17x17 alpha grid is literally the same quarter-subset of the
// cell's 33x33 VertexHeightMap grid HeightmapDecoder decodes elsewhere.
//
// Deliberately narrower in scope than "detect any texture disagreement":
// a hard edge is a per-vertex, boundary-LOCAL phenomenon (full strength
// right up to a line, then gone), and that's the only thing distinguished
// here from ordinary, expected textural variety anywhere else in a
// quadrant (a rock outcrop in one corner is NOT a bug - see the reverted
// whole-quadrant intra-cell attempt in TextureLayerFixer.cs's own history
// for why that distinction matters; it crashed a live run by flagging
// thousands of ordinary cells).

using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace SeamFinder.Core;

// One confirmed hard-edge run along a specific intra-cell quadrant
// boundary: `Texture` is strong (>= HardEdgeStrongThreshold, with no real
// taper approaching the edge) on the StrongQuadrant side of the boundary,
// and essentially absent (<= HardEdgeAbsentThreshold) on the WeakQuadrant
// side, for at least HardEdgeMinRunLength consecutive boundary vertices.
// `BoundaryOpacity` carries the STRONG side's own real per-vertex opacity
// at each boundary index in the run (keyed by the 0..16 boundary index,
// i.e. row for an east-west pair or col for a north-south pair) - used to
// scale the taper proportionally instead of a single flat number.
public record HardEdgeSeam(
    Quadrant StrongQuadrant, Quadrant WeakQuadrant, FormKey Texture,
    bool BoundaryIsEastWest, Dictionary<int, float> BoundaryOpacity);

public static class TextureBoundaryAnalyzer
{
    // How strong a texture must be AT the exact boundary vertex to count as
    // a genuine, solid presence there (not just a faint trace) - the "full
    // strength right up to the line" half of the hard-edge signature.
    public const float HardEdgeStrongThreshold = 0.85f;

    // How close to zero the SAME texture must be on the OTHER quadrant's
    // matching boundary vertex to count as genuinely absent - the "then
    // completely gone" half of the signature. Not a hard 0.0 requirement -
    // allows a little organic bleed without disqualifying a real hard edge.
    public const float HardEdgeAbsentThreshold = 0.15f;

    // How much opacity is allowed to drop between the boundary vertex and
    // the vertex one step further INTO the strong side before it counts as
    // "already tapering on its own" rather than a flat cutoff. A genuinely
    // hard-painted edge shows the SAME strength right up to the boundary as
    // in the interior; a normally soft-brushed edit falls off well before
    // it ever reaches the strength threshold above, so in practice this is
    // a light extra safety check, not the primary discriminator.
    public const float HardEdgeNoTaperMaxDrop = 0.15f;

    // Minimum consecutive boundary vertices (of the 17 along any one edge)
    // needed to count as a genuine LINE rather than one or two coincidental
    // spikes. 17 vertices span a full 2048-unit quadrant edge (~128 units
    // apart, same spacing as the height grid); 5 consecutive steps is
    // ~640 units - long enough to actually read as a line in-game, not
    // noise. Starting point, expected to get tuned from real feedback the
    // same way every other threshold in this toolkit has been.
    public const int HardEdgeMinRunLength = 5;

    // How many vertices deep the repair taper extends INTO the weak side,
    // fading from (approximately) the strong side's own boundary opacity
    // down toward 0. 3 vertices is ~384 units - modest, deliberately not
    // trying to repaint a huge swath, just soften the specific line.
    public const int TaperWidthVertices = 3;

    // Looser companion thresholds for FindFaintPresenceGaps below - see its
    // own header comment for why the strict thresholds above (85% strong,
    // 5-vertex run) structurally can never catch a real Northern Roads
    // defect confirmed 2026-09-14 against the user's own live profile: a
    // real per-vertex dump of cell (4,-12) (a reported "4 different shades,
    // one quadrant got no road texture at all" cell) showed
    // COTN_LRoadDirt01Trail113 sitting at a single boundary vertex - the
    // exact CENTER POINT where all 4 quadrants meet - at full strength
    // (1.00) in BottomLeft, while BottomRight had NO layer for that texture
    // at all (i.e. 0.00 everywhere). That is a one-vertex-wide trail touch,
    // not a long straight cutoff - HardEdgeMinRunLength=5 can never fire on
    // it no matter how low HardEdgeStrongThreshold goes. 0.25 (not "any
    // nonzero value") still requires a real, deliberate paint stroke rather
    // than a stray rounding trace.
    public const float TrustedPresenceThreshold = 0.25f;
    public const int TrustedMinRunLength = 1;

    // The 4 seams running through the middle of every single cell, each
    // tagged with which axis its 17-vertex boundary runs along. Matches
    // TextureLayerFixer.IntraCellPairs' quadrant pairing exactly, but adds
    // the axis information this file's per-vertex analysis actually needs.
    public static readonly (Quadrant A, Quadrant B, bool IsEastWest)[] IntraCellBoundaries =
    {
        (Quadrant.BottomLeft, Quadrant.BottomRight, true),
        (Quadrant.TopLeft, Quadrant.TopRight, true),
        (Quadrant.BottomLeft, Quadrant.TopLeft, false),
        (Quadrant.BottomRight, Quadrant.TopRight, false),
    };

    // Reshapes one AlphaLayer's sparse (Position,Opacity) list into the
    // confirmed 17x17 grid (row=Position/17=Y, col=Position%17=X). Missing
    // positions default to 0 (the layer's own paint never touched that
    // vertex) - the standard sparse-storage convention this whole format
    // uses.
    public static float[,] DecodeAlphaGrid(IAlphaLayerGetter layer)
    {
        var grid = new float[17, 17];
        if (layer.AlphaLayerData is null) return grid;
        foreach (var d in layer.AlphaLayerData)
        {
            int row = d.Position / 17, col = d.Position % 17;
            if (row is < 0 or > 16 || col is < 0 or > 16) continue;
            grid[row, col] = d.Opacity;
        }
        return grid;
    }

    // Scans one intra-cell quadrant boundary for hard-edge seams in EITHER
    // direction (a texture strong on quadA-absent-on-quadB, or vice versa).
    // `layersByQuadrant` only needs entries for quadA and quadB.
    public static List<HardEdgeSeam> FindHardEdgeSeams(
        Quadrant quadA, Quadrant quadB, bool boundaryIsEastWest,
        Dictionary<Quadrant, List<IBaseLayerGetter>> layersByQuadrant)
    {
        var seams = new List<HardEdgeSeam>();
        var alphaA = layersByQuadrant.GetValueOrDefault(quadA)?.OfType<IAlphaLayerGetter>().ToList() ?? new();
        var alphaB = layersByQuadrant.GetValueOrDefault(quadB)?.OfType<IAlphaLayerGetter>().ToList() ?? new();

        Scan(alphaA, quadA, alphaB, quadB, strongIsA: true, boundaryIsEastWest, seams, HardEdgeStrongThreshold, HardEdgeMinRunLength);
        Scan(alphaB, quadB, alphaA, quadA, strongIsA: false, boundaryIsEastWest, seams, HardEdgeStrongThreshold, HardEdgeMinRunLength);
        return seams;
    }

    // Looser companion to FindHardEdgeSeams above - see TrustedPresenceThreshold's
    // own comment for why this exists. The caller MUST restrict this to
    // already-vetted trusted textures (e.g. filter results by
    // TextureLayerFixer.IsTrustedTexture, exactly as it already does for
    // FindHardEdgeSeams' output) - run against an untrusted texture this
    // would reintroduce the original false-positive flood, since a short,
    // low-opacity presence near a corner is completely ordinary, ubiquitous
    // terrain painting for most textures. Trust is what distinguishes "this
    // was meant to continue" from "coincidental normal variety."
    public static List<HardEdgeSeam> FindFaintPresenceGaps(
        Quadrant quadA, Quadrant quadB, bool boundaryIsEastWest,
        Dictionary<Quadrant, List<IBaseLayerGetter>> layersByQuadrant)
    {
        var seams = new List<HardEdgeSeam>();
        var alphaA = layersByQuadrant.GetValueOrDefault(quadA)?.OfType<IAlphaLayerGetter>().ToList() ?? new();
        var alphaB = layersByQuadrant.GetValueOrDefault(quadB)?.OfType<IAlphaLayerGetter>().ToList() ?? new();

        Scan(alphaA, quadA, alphaB, quadB, strongIsA: true, boundaryIsEastWest, seams, TrustedPresenceThreshold, TrustedMinRunLength);
        Scan(alphaB, quadB, alphaA, quadA, strongIsA: false, boundaryIsEastWest, seams, TrustedPresenceThreshold, TrustedMinRunLength);
        return seams;
    }

    static void Scan(
        List<IAlphaLayerGetter> strongLayers, Quadrant strongQuadrant,
        List<IAlphaLayerGetter> weakLayers, Quadrant weakQuadrant,
        bool strongIsA, bool boundaryIsEastWest, List<HardEdgeSeam> seams,
        float strongThreshold, int minRunLength)
    {
        foreach (var layer in strongLayers)
        {
            var texture = layer.Header.Texture.FormKey;
            if (texture.IsNull) continue;

            var strongGrid = DecodeAlphaGrid(layer);
            var weakLayer = weakLayers.FirstOrDefault(l => l.Header.Texture.FormKey == texture);
            var weakGrid = weakLayer is not null ? DecodeAlphaGrid(weakLayer) : new float[17, 17];

            // Boundary/interior/opposite indices depend on which side is
            // "strong" and which axis the boundary runs along - see the
            // class header for the confirmed col=X/row=Y convention.
            int strongBoundaryIdx = strongIsA ? 16 : 0; // strong quadrant's own edge facing the weak quadrant
            int strongInteriorIdx = strongIsA ? 15 : 1; // one vertex further into the strong quadrant's interior
            int weakBoundaryIdx = strongIsA ? 0 : 16;   // weak quadrant's own edge facing the strong quadrant (same physical line)

            var run = new Dictionary<int, float>();
            void FlushRun()
            {
                if (run.Count >= minRunLength)
                    seams.Add(new HardEdgeSeam(strongQuadrant, weakQuadrant, texture, boundaryIsEastWest, new Dictionary<int, float>(run)));
                run.Clear();
            }

            for (int i = 0; i <= 16; i++)
            {
                float boundaryStrong = boundaryIsEastWest ? strongGrid[i, strongBoundaryIdx] : strongGrid[strongBoundaryIdx, i];
                float interiorStrong = boundaryIsEastWest ? strongGrid[i, strongInteriorIdx] : strongGrid[strongInteriorIdx, i];
                float boundaryWeak = boundaryIsEastWest ? weakGrid[i, weakBoundaryIdx] : weakGrid[weakBoundaryIdx, i];

                bool qualifies = boundaryStrong >= strongThreshold
                    && boundaryWeak <= HardEdgeAbsentThreshold
                    && (interiorStrong - boundaryStrong) <= HardEdgeNoTaperMaxDrop;

                if (qualifies) run[i] = boundaryStrong;
                else FlushRun();
            }
            FlushRun();
        }
    }

    // Builds the sparse (Position,Opacity) taper that repairs one confirmed
    // seam: on the WEAK quadrant, for each boundary index in the run, fades
    // the seam's texture from (close to) the strong side's own real
    // boundary opacity at that vertex down toward 0 over TaperWidthVertices
    // steps moving further into the weak quadrant's interior. This is what
    // actually closes the hard line with a real gradient, instead of the
    // older whole-quadrant fixer's flat, uniform "hint" across the entire
    // quadrant. Returns full (Position -> Opacity) pairs ready to merge
    // into an AlphaLayerData list. `weakBoundaryIndex` is 0 if the strong
    // quadrant sat at index 16 (taper grows inward from 0 to
    // TaperWidthVertices-1), or 16 if the strong quadrant sat at index 0
    // (taper grows inward from 16 down to 17-TaperWidthVertices).
    public static Dictionary<int, float> BuildTaper(HardEdgeSeam seam, int weakBoundaryIndex)
    {
        var result = new Dictionary<int, float>();
        int direction = weakBoundaryIndex == 0 ? 1 : -1;

        foreach (var (boundaryPos, strongOpacity) in seam.BoundaryOpacity)
        {
            for (int depth = 0; depth < TaperWidthVertices; depth++)
            {
                int idx = weakBoundaryIndex + direction * depth;
                if (idx is < 0 or > 16) break;
                float opacity = strongOpacity * (1f - (float)depth / TaperWidthVertices);
                if (opacity <= 0.02f) continue; // not worth encoding a near-zero entry

                int row = seam.BoundaryIsEastWest ? boundaryPos : idx;
                int col = seam.BoundaryIsEastWest ? idx : boundaryPos;
                int position = row * 17 + col;

                // Two different boundary vertices' tapers can overlap at the
                // same target position only if TaperWidthVertices exceeded
                // the run's own spacing, which it never does at 3 - kept as
                // a max() merge anyway for safety rather than assuming.
                result[position] = Math.Max(result.GetValueOrDefault(position), opacity);
            }
        }

        return result;
    }
}
