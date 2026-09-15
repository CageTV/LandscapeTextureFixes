// Landscape Texture Fixer CLI - detects and fixes landscape TEXTURE seams:
// adjacent exterior cells whose winning quadrant texture layers don't share
// anything at all, caused by two mods painting different ground textures
// without knowing about each other. Companion to SeamFinder (which handles
// HEIGHT seams) - core logic lives in SeamFinder.Core (shared with this
// project's own future UI).
//
// Two ways to run this exe:
//   - "--mo2 <instancePath> <profileName> [gameDataPath]": reads the MO2
//     profile's plugins.txt/loadorder.txt/modlist.txt directly and resolves
//     each active plugin's winning file itself (SeamFinder.Core's
//     Mo2Resolver) - no VFS needed at all.
//   - "--fix <instancePath> <profileName> [gameDataPath]": generates
//     LandscapeTextureFixes.esp for the isolated mod-vs-base-game cases.
//
// See SeamFinder.Core/TextureLayerDetector.cs and TextureLayerFixer.cs for
// the texture-layer format details and the scope/reasoning behind the fix.

using SeamFinder.Core;

if (args.Length > 0 && args[0] == "--mo2")
{
    RunMo2Mode(args);
}
else if (args.Length > 0 && args[0] == "--fix")
{
    RunFixMode(args);
}
else
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  TextureSeamFixer.exe --mo2 <instancePath> <profileName> [gameDataPath]");
    Console.WriteLine("  TextureSeamFixer.exe --fix <instancePath> <profileName> [gameDataPath]");
    Pause();
}

void RunMo2Mode(string[] mo2Args)
{
    if (mo2Args.Length < 3)
    {
        Console.WriteLine("Usage: TextureSeamFixer.exe --mo2 <instancePath> <profileName> [gameDataPath]");
        Console.WriteLine("  gameDataPath is optional - if omitted, reads gamePath from <instancePath>\\ModOrganizer.ini");
        return;
    }
    var instancePath = mo2Args[1];
    var profileName = mo2Args[2];
    var gameDataPath = mo2Args.Length > 3 ? mo2Args[3] : ReadGamePathFromIni(instancePath);

    Console.WriteLine($"MO2 instance: {instancePath}");
    Console.WriteLine($"Profile: {profileName}");
    Console.WriteLine($"Game Data path: {gameDataPath}");
    Console.WriteLine();

    try
    {
        var resolved = Mo2Resolver.Resolve(instancePath, profileName, gameDataPath);
        Console.WriteLine($"Resolved {resolved.LoadOrder.Count} active plugins to real files.");
        if (resolved.MissingPlugins.Count > 0)
        {
            Console.WriteLine($"WARNING: {resolved.MissingPlugins.Count} active plugins could not be found in any enabled mod folder or the game Data folder:");
            foreach (var m in resolved.MissingPlugins) Console.WriteLine("  " + m);
        }

        var result = TextureLayerDetector.RunForResolvedPlugins(resolved.LoadOrder, Console.WriteLine);
        WriteReport(result.ReportCsvLines, Path.Combine(AppContext.BaseDirectory, "TextureSeamReport.csv"));
    }
    catch (Exception ex)
    {
        Console.WriteLine();
        Console.WriteLine("ERROR: " + ex);
    }

    Pause();
}

void RunFixMode(string[] fixArgs)
{
    if (fixArgs.Length < 3)
    {
        Console.WriteLine("Usage: TextureSeamFixer.exe --fix <instancePath> <profileName> [gameDataPath] [--trust-northern-roads] [--trust-plugin=\"X.esp\"] [--trust-texture-prefix=\"COTN\"]");
        Console.WriteLine("  Generates LandscapeTextureFixes.esp fixing trusted-vs-untrusted texture seams (mod-vs-base AND mod-vs-mod).");
        Console.WriteLine("  gameDataPath is optional - if omitted, reads gamePath from <instancePath>\\ModOrganizer.ini");
        return;
    }
    var instancePath = fixArgs[1];
    var profileName = fixArgs[2];
    var gameDataPath = fixArgs.Length > 3 && !fixArgs[3].StartsWith("--") ? fixArgs[3] : ReadGamePathFromIni(instancePath);

    Console.WriteLine($"MO2 instance: {instancePath}");
    Console.WriteLine($"Profile: {profileName}");
    Console.WriteLine($"Game Data path: {gameDataPath}");
    Console.WriteLine();

    try
    {
        var resolved = Mo2Resolver.Resolve(instancePath, profileName, gameDataPath);
        Console.WriteLine($"Resolved {resolved.LoadOrder.Count} active plugins to real files.");
        if (resolved.MissingPlugins.Count > 0)
        {
            Console.WriteLine($"WARNING: {resolved.MissingPlugins.Count} active plugins could not be found:");
            foreach (var m in resolved.MissingPlugins) Console.WriteLine("  " + m);
        }

        var fixResult = TextureLayerFixer.GenerateFixPluginForResolvedPlugins(
            resolved.LoadOrder, "LandscapeTextureFixes.esp", AppContext.BaseDirectory, Console.WriteLine,
            TrustNorthernRoads(fixArgs), CustomTrustedPlugins(fixArgs), TrustedTexturePrefixes(fixArgs));
        Console.WriteLine();
        Console.WriteLine($"Patched {fixResult.CellsPatched} cells ({fixResult.LayersAdded} layers added, {fixResult.LayersBoosted} boosted, {fixResult.SkippedQuadrantFull} skipped - quadrant at the 7-layer cap).");
        Console.WriteLine($"Output: {fixResult.OutputPath}");
    }
    catch (Exception ex)
    {
        Console.WriteLine();
        Console.WriteLine("ERROR: " + ex);
    }

    Pause();
}

bool TrustNorthernRoads(string[] a) => a.Contains("--trust-northern-roads", StringComparer.OrdinalIgnoreCase);

// Repeatable: --trust-plugin="Some Mod.esp" --trust-plugin="Some Family -*"
List<string> CustomTrustedPlugins(string[] a) => a
    .Where(arg => arg.StartsWith("--trust-plugin=", StringComparison.OrdinalIgnoreCase))
    .Select(arg => arg["--trust-plugin=".Length..].Trim('"'))
    .Where(p => p.Length > 0)
    .ToList();

// Repeatable: --trust-texture-prefix="COTN" - texture EditorID prefixes
// trusted regardless of which plugin's cell they show up in (e.g. Northern
// Roads' own road/path textures), independent of whole-mod trust.
List<string> TrustedTexturePrefixes(string[] a) => a
    .Where(arg => arg.StartsWith("--trust-texture-prefix=", StringComparison.OrdinalIgnoreCase))
    .Select(arg => arg["--trust-texture-prefix=".Length..].Trim('"'))
    .Where(p => p.Length > 0)
    .ToList();

void WriteReport(List<string> lines, string outPath)
{
    File.WriteAllLines(outPath, lines);
    Console.WriteLine();
    Console.WriteLine($"Report written to: {outPath}");
}

void Pause()
{
    Console.WriteLine();
    Console.WriteLine("Press any key to exit...");
    try { Console.ReadKey(); } catch (InvalidOperationException) { /* no console input (e.g. redirected/piped) - just exit */ }
}

static string ReadGamePathFromIni(string instancePath)
{
    var iniPath = Path.Combine(instancePath, "ModOrganizer.ini");
    if (!File.Exists(iniPath))
        throw new FileNotFoundException("No gameDataPath given and ModOrganizer.ini not found to read it from.", iniPath);

    foreach (var line in File.ReadAllLines(iniPath))
    {
        if (!line.StartsWith("gamePath=")) continue;
        var value = line["gamePath=".Length..].Trim();
        var start = value.IndexOf('(');
        var end = value.LastIndexOf(')');
        if (start >= 0 && end > start)
            value = value[(start + 1)..end];
        value = value.Replace("\\\\", "\\");
        return Path.Combine(value, "Data");
    }

    throw new InvalidOperationException("Could not find gamePath= in ModOrganizer.ini - pass gameDataPath explicitly instead.");
}
