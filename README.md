# Landscape Texture Fixer

**Current version: 1.0.3** — see [CHANGELOG.md](CHANGELOG.md) for what's new.

A standalone tool for Skyrim Special Edition / Anniversary Edition that fixes
**landscape texture seams** — adjacent terrain quadrants that share no
texture at all, because two mods painted ground textures without ever
knowing about each other. Companion to [Landscape Seam Fixer](../Landscape%20Seam%20Fixer)
(which handles terrain *height* seams instead of textures) — same trust
model, same MO2-aware resolution, no Creation Kit work required.

It works entirely offline against your mod manager's own config files — it
does **not** need MO2 or Vortex running, and does not rely on either tool's
virtual file system.

## What it does

**Corrects a texture-quadrant edge wherever a trusted mod's texture data has
been silently buried or never reconciled with a neighbor.** Every exterior
cell's landscape is divided into four quadrants, each carrying up to 7
texture layers. When two adjacent quadrants (from different plugins, or the
same plugin against untouched vanilla) share *no* texture at all, that's a
visible seam in-game — a hard line where one ground texture stops and
another starts with nothing blending between them.

**Trust system shared with Landscape Seam Fixer:** the same trusted-plugin
list and masters-based patch detection (see that tool's README for the
default list). A trusted mod's texture data can correct an **untrusted**
mod's texture too, not just plain vanilla — wherever the two share nothing
across a quadrant edge.

**Trusted texture prefixes** (this tool's own addition, beyond the shared
trust list): trust one specific texture set by its EditorID prefix,
independent of which plugin's cell it shows up in. The motivating case is
Northern Roads' own road/path textures (EditorIDs starting `COTN`) — you can
trust just those without trusting Northern Roads' entire texture output.

## Requirements

- Windows
- To just **run** the pre-built release: nothing extra — it's published
  self-contained (bundles its own .NET runtime).
- To **build from source**: the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

## Building from source

```
git clone <this repo's URL>
cd "Landscape Texture Fixer"
dotnet build
```

Publish the desktop UI as a standalone folder:

```
dotnet publish TextureSeamFixer.UI -c Release -r win-x64 --self-contained true -o TextureSeamFixer.UI/publish
```

The console CLI (`TextureSeamFixer/`) runs straight out of
`bin/Release/net10.0/TextureSeamFixer.exe` — no publish step needed for
local use.

## Usage — desktop app (recommended)

1. Launch `TextureSeamFixer.UI.exe`.
2. Pick how your mods are managed (Mod Organizer 2 / Vortex / Direct game
   path) — same as Landscape Seam Fixer.
3. The **Output folder** auto-fills with a sensible default.
4. Tick **Trust Northern Roads.esp**, fill in **Additional trusted plugins**,
   and/or **Trusted texture prefixes** (e.g. `COTN`) as needed — all
   optional, all off by default.
5. Click **Run Detection** to generate `TextureSeamReport.csv` — every
   mismatched quadrant edge, which plugins own each side, and whether a
   shared/bridgeable texture exists at all.
6. Click **Generate Fix Plugin** to produce `LandscapeTextureFixes.esp`.
   Independent of step 5 — you don't need to run detection first.
7. Install/activate the generated plugin like any other mod, loaded **after**
   the mods it's correcting.
8. **Test in-game** before trusting it broadly.

## Usage — command line

```
TextureSeamFixer.exe --mo2 <instancePath> <profileName> [gameDataPath]
```
Detection only — writes `TextureSeamReport.csv` next to the exe.

```
TextureSeamFixer.exe --fix <instancePath> <profileName> [gameDataPath] [flags]
```
Generates `LandscapeTextureFixes.esp`. Optional flags:

- `--trust-northern-roads`
- `--trust-plugin="Some Mod.esp"` (repeatable; a trailing `*` matches a
  patch-family prefix)
- `--trust-texture-prefix="COTN"` (repeatable)

`gameDataPath` is optional in both modes — if omitted, it's read from
`<instancePath>\ModOrganizer.ini`.

## How it works

**Detection:** for every pair of adjacent exterior cells, resolves each
side's winning quadrant texture layers (the same "landscape isn't always
owned by the plugin that wins the Cell overall" resolution
`SeamFinder.Core` uses for terrain height), then checks whether the two
share a texture. If not, the edge is flagged `Bridgeable` (a shared/related
texture family exists to blend toward) or `Foreign` (nothing in common at
all).

**Fixing:** for each flagged edge involving at least one trusted-vs-
untrusted or trusted-texture-prefix pairing, adds the missing texture as a
new, low-opacity layer on the losing side — never replaces an existing
layer, never exceeds the engine's 7-layers-per-quadrant cap (logged and
skipped if it would). This is a gentle blend hint toward the trusted
texture, not a wholesale texture swap.

**Preservation (with "Trust Northern Roads" on, v1.0.8+):** separately from
the boundary/seam detection above, also checks every cell for a Northern
Roads (or its patch family's) own texture layer that the current winner is
missing ENTIRELY — not a boundary disagreement, a wholesale absence, which
happens whenever a later, unrelated mod completely re-authors that cell's
Landscape record with no idea Northern Roads ever painted anything there.
Confirmed by checking who actually DEFINED the texture record, not by its
name. Adds it with its real, original per-vertex data (never a synthesized
blend) if there's room under the same 7-layer cap — additive only, same as
the fixing pass above.

## Project layout

- `SeamFinder.Core/` — the subset of the shared library this tool needs
  (`Mo2Resolver`, `TrustResolver`, `HeightmapDecoder`, `TextureLayerFixer`,
  `TextureLayerDetector`, `TextureBoundaryAnalyzer`, `EslEligibility`)
- `TextureSeamFixer/` — console CLI
- `TextureSeamFixer.UI/` — WPF desktop app

`SeamFinder.Core` here is a trimmed copy shared with two sibling tools
(Landscape Seam Fixer, Floating Object Fixer) that live in their own
separate repos.

## Contributing

Issues and PRs welcome, especially reports of a specific texture seam this
tool didn't resolve (a `TextureSeamReport.csv` row plus an in-game
screenshot is the most useful bug report).

## License

MIT — see [LICENSE](LICENSE).
