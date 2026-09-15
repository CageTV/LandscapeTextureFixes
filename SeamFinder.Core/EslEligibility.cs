using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace SeamFinder.Core;

// Shared by all 5 tools in this family - each one writes an output plugin
// that (by design) almost always consists ENTIRELY of overrides of existing
// records (Cell/Landscape/PlacedObject/PlacedNpc via GetOrAddAsOverride),
// never brand-new ones - so every one of them is, in practice, trivially
// ESL-eligible. Confirmed 2026-09-15 against a real generated PatchForeman.esp
// via SSEEdit's own "Find ESP plugins which could be turned into ESL.pas"
// script (E:\Tabula Rasa\tools\SSEEdit\Edit Scripts\) - this class implements
// the EXACT SAME algorithm that script uses, so the answer here always
// matches what that script would report for the same file.
public record EslEligibilityResult(bool Eligible, bool FlagWasSet, int NewRecordCount, uint MaxNewFormId, bool HasNewCell)
{
    public string Summary => Eligible
        ? $"ESL flag set ({NewRecordCount} new record(s) this plugin actually defines, highest FormID 0x{MaxNewFormId:X3})."
            + (HasNewCell ? " WARNING: this plugin also defines a brand-new CELL - per a known engine limitation, a new CELL in an ESL-flagged plugin can misbehave if a later plugin overrides it." : "")
        : NewRecordCount > EslEligibility.MaxNewRecords
            ? $"NOT flagged ESL: this plugin defines {NewRecordCount} new record(s), over the {EslEligibility.MaxNewRecords} ESL limit."
            : $"NOT flagged ESL: highest new-record FormID is 0x{MaxNewFormId:X3}, over the ESL-range limit of 0x{EslEligibility.MaxFormId:X3} - would need FormIDs compacted first.";
}

public static class EslEligibility
{
    // Matches "Find ESP plugins which could be turned into ESL.pas" exactly:
    // iESLMaxRecords = $800, iESLMaxFormID = $fff.
    public const int MaxNewRecords = 0x800;
    public const uint MaxFormId = 0xFFF;

    // Call once, right before Write() (setting the header flag is an
    // in-memory change, so it lands in the same write). A record counts as
    // "new" - the exact same test xEdit's own IsMaster(e) uses - only when
    // this plugin is genuinely the record's OWN origin (FormKey.ModKey equals
    // this plugin's own ModKey); every GetOrAddAsOverride'd record keeps its
    // ORIGINAL defining plugin's FormKey, so it correctly does NOT count here
    // even though this plugin's file contains a copy of it.
    public static EslEligibilityResult CheckAndFlag(SkyrimMod patchMod)
    {
        int newRecordCount = 0;
        uint maxFormId = 0;
        bool hasNewCell = false;

        foreach (var record in patchMod.EnumerateMajorRecords())
        {
            if (!record.FormKey.ModKey.Equals(patchMod.ModKey)) continue;
            newRecordCount++;
            if (record is Cell) hasNewCell = true;
            if (record.FormKey.ID > maxFormId) maxFormId = record.FormKey.ID;
        }

        if (newRecordCount > MaxNewRecords || maxFormId > MaxFormId)
            return new EslEligibilityResult(false, false, newRecordCount, maxFormId, hasNewCell);

        patchMod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Small;
        return new EslEligibilityResult(true, true, newRecordCount, maxFormId, hasNewCell);
    }
}
