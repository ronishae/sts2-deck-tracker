using System.Globalization;
using System.Text;
using System.Text.Json;
using Godot;

namespace DeckTracker;

// Writes the run export artifacts: one JSON document per run (full timeline + map + per-fight breakdown)
// and one master append-only CSV of per-fight card rows accumulated across every run. Both are written
// automatically at combat end and run end. All I/O is guarded so an export failure can never crash a run.
public static class RunExporter
{
    private static readonly string ExportDirectory = ProjectSettings.GlobalizePath("user://deck_tracker_exports/");
    private const string FightCsvName = "card_fights.csv";

    private const string CsvHeader =
        "runSeed,runStartedAt,character,ascension,gameVersion,act,actName,floor,combatIndex,encounterId,combatType,turns,damageTaken," +
        "entityType,name,playerIndex,ownerNetId,floorAdded,copyIndex,upgradeLevel,enchantment,generatedBy,rarity," +
        "floorObtained,floorUsed,floorDiscarded," +
        "timesDrawn,timesPlayed,playRate,damage,generatedDamage,damageContribPct,rawForge,connectedForge,receivedForge";

    // Reads back a run's previously exported JSON document into a RunLog. Used to amend a run that is no
    // longer the active session (e.g. abandoned from the main menu after a save & quit), since the export
    // file persists indefinitely whereas the internal save file is LRU-evicted. Returns null if absent.
    public static RunLog? TryLoadExportedRun(string seed)
    {
        try
        {
            var path = System.IO.Path.Combine(ExportDirectory, $"{CardRegistry.GetRunFileStem(seed)}.json");
            if (!System.IO.File.Exists(path))
            {
                Log.Warn($"TryLoadExportedRun. No export found. Seed: {seed}, Path: {path}");
                return null;
            }
            var json = System.IO.File.ReadAllText(path);
            var log = JsonSerializer.Deserialize(json, RunExportCtx.Default.RunLog);
            Log.Info($"TryLoadExportedRun. Loaded. Seed: {seed}, Outcome: {log?.Outcome}");
            return log;
        }
        catch (Exception e)
        {
            Log.Error($"TryLoadExportedRun Failed: {e.Message}");
            return null;
        }
    }

    // Overwrites the run's JSON document with the current state of its log. Cheap and idempotent, so it is
    // safe to call after every combat and at run end.
    public static void ExportRun(RunLog log)
    {
        try
        {
            System.IO.Directory.CreateDirectory(ExportDirectory);
            var path = System.IO.Path.Combine(ExportDirectory, $"{CardRegistry.GetRunFileStem(log.RunSeed)}.json");
            var json = JsonSerializer.Serialize(log, RunExportCtx.Default.RunLog);
            System.IO.File.WriteAllText(path, json);
            Log.Info($"ExportRun. Seed: {log.RunSeed}, Combats: {log.Combats.Count}, Path: {path}");
        }
        catch (Exception e)
        {
            Log.Error($"ExportRun Failed: {e.Message}");
        }
    }

    // Appends per-fight card rows to the single master CSV for every combat past the log's high-water mark,
    // then advances the mark. The mark is persisted in the save file, so a resume never double-appends.
    public static void AppendFightRows(RunLog log)
    {
        try
        {
            var newCombats = log.Combats
                .Where(c => c.Index > log.LastExportedCombatIndex)
                .OrderBy(c => c.Index)
                .ToList();
            if (newCombats.Count == 0)
            {
                Log.VeryDebug($"AppendFightRows. Nothing new. Mark: {log.LastExportedCombatIndex}");
                return;
            }

            System.IO.Directory.CreateDirectory(ExportDirectory);
            var path = System.IO.Path.Combine(ExportDirectory, FightCsvName);

            var builder = new StringBuilder();
            if (!System.IO.File.Exists(path))
            {
                builder.AppendLine(CsvHeader);
            }

            foreach (var combat in newCombats)
            {
                AppendCombatRows(builder, log, combat);
            }

            System.IO.File.AppendAllText(path, builder.ToString());
            log.LastExportedCombatIndex = newCombats[^1].Index;
            Log.Info($"AppendFightRows. Appended combats: {newCombats.Count}, NewMark: {log.LastExportedCombatIndex}, Path: {path}");
        }
        catch (Exception e)
        {
            Log.Error($"AppendFightRows Failed: {e.Message}");
        }
    }

    private static void AppendCombatRows(StringBuilder builder, RunLog log, CombatRecord combat)
    {
        foreach (var entity in combat.Contributions)
        {
            var fields = new[]
            {
                log.RunSeed, log.StartedAtUtc, log.Character, Num(log.AscensionLevel), log.GameVersion,
                Num(combat.Act), combat.ActName, Num(combat.Floor), Num(combat.Index), combat.EncounterId, combat.CombatType, Num(combat.Turns), Num(combat.DamageTaken),
                entity.EntityType, entity.Name, Num(entity.PlayerIndex), entity.OwnerNetId,
                Num(entity.FloorAdded), Num(entity.CopyIndex), Num(entity.UpgradeLevel), entity.Enchantment, entity.GeneratedBy, entity.Rarity,
                Num(entity.FloorObtained), Num(entity.FloorUsed), Num(entity.FloorDiscarded),
                Num(entity.TimesDrawn), Num(entity.TimesPlayed), Num(entity.PlayRate),
                Num(entity.Damage), Num(entity.GeneratedDamage), Num(entity.DamageContribPct),
                Num(entity.RawForge), Num(entity.ConnectedForge), Num(entity.ReceivedForge)
            };
            builder.AppendLine(string.Join(",", fields.Select(Csv)));
        }
    }

    private static string Num(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Num(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
    private static string Num(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";

    // Quotes a CSV field only when it contains a comma, quote, or newline, doubling any embedded quotes.
    private static string Csv(string? field)
    {
        var value = field ?? "";
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
        {
            return value;
        }
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
