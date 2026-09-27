# Landscape Texture Fixer — Changelog

## v1.1.1 — 2026-09-27

- **Fixed: VHGT `Offset` scaling** (reported on the sibling Landscape Seam Fixer:
  [#2](https://github.com/CageTV/landscape-seam-fixer/issues/2)). The heightmap offset uses the same
  8-unit step as the per-vertex deltas; reading it unscaled gave wrong absolute terrain heights.
- The app now uses the tool family's shared icon.
- Command-line and UI versions aligned at 1.1.1.

## v1.1.0 — 2026-09-15

**New: genuine two-way blending when two DIFFERENT trusted road textures meet.** Reported by a user:
when two different Northern Roads patches (or Northern Roads and one of its own patches) paint
different specific road textures right up against each other - e.g. one area uses `COTN_LDirtDry01`,
another uses `COTN_LDirtDry02` - the boundary between them used to get NO correction at all. Both
sides had a legitimate "this is the deliberate edit" signal, and the tool's cross-cell logic could only
ever pick ONE winner - when both sides equally qualified, it fell back to "ambiguous, not our scope"
and silently did nothing, leaving the straight line unfixed.

Fixed: this specific case is now recognized as a genuine BLEND, not a winner-vs-loser situation - both
sides get a hint of the other's texture (or a shared bridge texture, when the two areas already have
something painted in common), softening the transition from both directions instead of leaving a hard
cutoff. Verified against the real live profile: cells patched went from 627 to 1096 - a real, bounded
increase (not the false-positive flood an earlier, broader "any two different textures" attempt caused
and had to be reverted for).

## v1.0.9 — 2026-09-15

**New: automatic ESL flagging.** The generated fix plugin is now checked for ESL eligibility every run
(same logic as SSEEdit's own "Find ESP plugins which could be turned into ESL" script) and automatically
flagged as an ESL if it qualifies — this tool's output almost never adds brand-new records, so it's
eligible essentially every time.

**New: a `log.txt` and `settings-used.json` are now written into the output folder alongside the
generated plugin/report**, for both the detection run and the fix run — a portable record of exactly
what happened and what settings produced it.

## v1.0.8 — 2026-09-14

**Fixed a real gap the boundary/seam scanners structurally could never catch: a wholesale-absent
Northern Roads texture layer, not a seam.** Every other correction in this tool reacts to a detected
disagreement between two adjacent quadrants/cells. But whenever a LATER, non-Northern-Roads plugin
completely re-authors a cell's Landscape record outright (a normal load-order override, nothing to do
with this tool), that plugin's own copy simply never knew Northern Roads' road-texture layer existed
— it's just missing, with no "edge" for a seam scanner to ever find. Confirmed via a real case: a
`COTN_LDirtDry` layer painted by a Northern Roads Grass Mods patch, silently absent from the winning
cell with no boundary to detect at all.

Added a new pass, gated on "Trust Northern Roads" like everything else Northern-Roads-specific here:
for every cell, checks whether Northern Roads (or a genuine patch of it) authored a texture layer the
current winner doesn't carry, and adds it if so — verified by who originally DEFINED the texture
record, not by its name (`COTN` was the first cut; checking real authorship instead is strictly more
correct and needs no naming convention at all). Real per-vertex data copied verbatim, never a
synthesized blend — this is preservation, not correction. Additive only: never touches an existing
layer, respects the same 7-layer-per-quadrant cap the rest of this tool already uses.

**Verified against the real, live 1282-plugin profile**: 627 cells patched (148 layers added, 818
boosted), 60 of the added layers were exactly this kind of wholesale-absence preservation — real
coverage the boundary scanners alone would never have found.

## v1.0.6 — 2026-09-14

**Added: the app now remembers your settings.** Every path, mode, and trust
option you fill in is saved automatically and restored the next time you
open the tool — no more re-typing your MO2 instance path or re-checking
"Trust Northern Roads" every single launch. Matches the same behavior
Floating Object Fixer and Road Mask Merger already had; this tool just
didn't have it yet.

## v1.0.5 — 2026-09-14

**Fixed a second real bug, found via in-game screenshots the user took right
after installing v1.0.4**: even with Northern Roads correctly winning
cross-mod boundaries (see v1.0.4 below), some spots still showed a blotchy,
multi-shade patch where one quadrant of a cell had none of Northern Roads'
road/trail texture at all, while the quadrants around it did. Root cause,
confirmed by decoding the exact reported cell's real per-vertex texture
data: Northern Roads' own road-trail texture is legitimately painted as a
short, low-opacity accent touch in cells away from the main highway — often
just a single full-strength vertex sitting exactly on a quadrant corner,
never spanning the 5 consecutive full-strength vertices the existing
straight-line-seam detector requires. That detector was built (and tuned)
for a different, wider kind of cutoff and structurally could never catch a
single-vertex corner touch, no matter how the strength threshold was
adjusted. Added a second, additional detection pass scoped only to
Northern Roads' own trusted textures, tuned for exactly this shape: any
real, deliberate paint touch on one quadrant that's completely missing from
the immediately neighboring quadrant, even a single vertex, now gets a
smooth 3-vertex taper instead of an abrupt on/off cliff. **Verified against
the user's own reported cell**: the affected quadrant went from zero trace
of the road texture to a proper fade (full strength at the shared corner,
tapering to nothing 3 vertices in) — confirmed by decoding the real,
freshly-generated output plugin, not just a compile check.

## v1.0.4 — 2026-09-14

**Fixed a real bug: Northern Roads' own road texture was not winning/
propagating even with "Trust Northern Roads" enabled.** Two compounding
causes: Northern Roads was being lumped into the same generic "passive"
trust bucket as vanilla/USSEP/Landscape and Water Fixes (which correctly
just *receive* a hint of whatever mod sits next to them, since they're
neutral base content) — backward for Northern Roads, whose road texture
*is* the deliberate edit that should spread outward into its neighbor. On
top of that, the fallback texture-prefix check (e.g. `COTN`) could only
ever fire when plugin-level trust was a tie, so it never actually reached
the most common real case: Northern Roads directly bordering a fully
untrusted mod. Fixed by giving Northern Roads (and any custom trusted
texture prefix) its own "wins/propagates" priority tier, checked first —
the old generic trust pool is unchanged for everything else. Also: enabling
"Trust Northern Roads" now automatically trusts its own `COTN` texture
prefix too, instead of requiring both settings to be turned on separately.
**Verified against the real, live 1282-plugin profile**: of 1769 real
cross-mod texture-boundary decisions, Northern Roads now wins 100% of the
time against a non-Northern-Roads neighbor (previously it could lose), with
zero regressions on the cases where two Northern-Roads-family plugins
border each other (correctly left alone, since there's no real cross-mod
seam there).

**Inherited from the shared trust library** (used by this tool and
Landscape Seam Fixer alike) since v1.0.3: a real-vertex hard-edge scanner
(gated on the same trusted-texture-prefix signal as the rest of this tool,
so it only fires on genuine Northern-Roads-style straight-line cutoffs, not
ordinary terrain variety) that builds a proper tapered blend instead of a
flat correction; and a fix for a tool-ordering bug where running Landscape
Seam Fixer before this tool (or Road Mask Merger) could undo their work in
a scattered "swiss cheese" patchwork — this tool's output, and its sibling
tools' output, is now recognized and respected regardless of run order.

## v1.0.3 — 2026-09-10

**Trust system generalized to real mod-vs-mod correction.** Previously a
trusted mod's texture data could only correct plain vanilla/base-game
texture data underneath it. Now it can correct an *untrusted mod's* texture
too, wherever the two share no texture at all across a quadrant edge — the
same trust rules as Landscape Seam Fixer, applied to textures instead of
terrain height.

**Added: trusted texture prefixes.** A new allowlist lets you trust one
specific texture set by its EditorID prefix (e.g. `COTN` for Northern
Roads' own road/path textures) without trusting that mod's entire texture
output. CLI: `--trust-texture-prefix="COTN"` (repeatable). UI: new "Trusted
texture prefixes" box.

**Internal:** now shares its trust/patch-detection logic with Landscape
Seam Fixer via a common library (previously a separate, smaller
implementation that only recognized base-game plugins — could silently
drift out of sync with the other tool over time).

**Verified** against a real load order with Northern Roads trust + the
`COTN` prefix enabled: cells patched went from 147 to 179, texture layers
boosted from 185 to 218.

---

## Earlier versions

v1.0.0 – v1.0.2 — see prior release notes (no changelog kept before this
version).
