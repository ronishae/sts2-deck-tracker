using System.Globalization;
using System.Text;
using Godot;

namespace DeckTracker;

// Appends per-fight card rows to the single master CSV. Written automatically at combat end.
// I/O is guarded so an export failure can never crash a run.
public static class RunExporter
{
    private static readonly string ExportDirectory = ProjectSettings.GlobalizePath("user://deck_tracker_exports/");
    private const string FightCsvName = "card_fights.csv";

    private const string CsvHeader =
        "runSeed,runStartedAt,character,ascension,gameVersion,act,actName,floor,combatIndex,encounterId,combatType,turns,damageTaken,combatResult," +
        "entityType,name,playerIndex,ownerNetId,floorAdded,copyIndex,upgradeLevel,enchantment,generatedBy,rarity," +
        "floorObtained,floorUsed,floorDiscarded," +
        "timesDrawn,timesPlayed,playRate,damage,generatedDamage,damageContribPct,rawForge,connectedForge,receivedForge";

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
                Num(combat.Act), combat.ActName, Num(combat.Floor), Num(combat.Index), combat.EncounterId, combat.CombatType, Num(combat.Turns), Num(combat.DamageTaken), combat.Outcome,
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
