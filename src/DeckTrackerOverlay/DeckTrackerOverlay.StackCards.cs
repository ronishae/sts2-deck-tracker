namespace DeckTracker;

public static partial class DeckTrackerOverlay
{
    // Cards that are always stacked into one row regardless of count (generated in large quantities during combat)
    private static readonly HashSet<string> AutoStackCardIds = new()
    {
        "SHIV",
        "SOUL",
        "GIANT_ROCK",
        "SWEEPING_GAZE",
        "MINION_DIVE_BOMB",
        "MINION_STRIKE",
        "FUEL",
        "MINION_SACRIFICE",
        "INFECTION",
        "WITHER",
        "SOOT",
        "BURN",
        "WOUND",
        "BECKON",
        "DEBRIS",
        "TOXIC",
        "SLIMED",
        "DAZED",
    };

    private static string ExtractBaseId(CardStats stat)
    {
        var key = string.IsNullOrEmpty(stat.BaseCardKey) ? stat.Id : stat.BaseCardKey;
        var idx = key.LastIndexOf("_F");
        return idx >= 0 ? key[..idx] : key;
    }

    private static List<CardStats> BuildStackedCardList(List<CardStats> stats)
    {
        const int StackThreshold = 7;
        var result = new List<CardStats>();

        // Include PlayerIndex so identical cards from different players never stack into one row, and
        // GeneratedById so generated cards stack per-creator (e.g. Shivs from Fan of Knives stay separate
        // from Shivs from Blade Dance, ready to nest under their respective generators).
        var groups = stats.GroupBy(s => (ExtractBaseId(s), s.UpgradeLevel, s.Enchantment, s.PlayerIndex, s.GeneratedById, s.GeneratedByImmediateId));

        foreach (var group in groups)
        {
            var versions = group.ToList();
            var baseId = group.Key.Item1;
            var shouldStack = AutoStackCardIds.Contains(baseId) || versions.Count > StackThreshold;

            if (!shouldStack || versions.Count == 1)
            {
                result.AddRange(versions);
                continue;
            }

            var representative = versions.First();
            var stacked = new CardStats
            {
                Id = representative.Id,
                DisplayName = representative.DisplayName,
                CardType = representative.CardType,
                Enchantment = representative.Enchantment,
                UpgradeLevel = representative.UpgradeLevel,
                BaseCardKey = representative.BaseCardKey,
                PlayerIndex = representative.PlayerIndex,
                GeneratedById = representative.GeneratedById,
                GeneratedByImmediateId = representative.GeneratedByImmediateId,
                FloorAdded = versions.Min(s => s.FloorAdded),
                FloorRemoved = -1,
                FloorLeftDeck = -1,
                IsActive = versions.Any(s => s.IsActive),
                CopiesInDeck = versions.Count,
                CombatDamage = versions.Sum(s => s.CombatDamage),
                RunDamage = versions.Sum(s => s.RunDamage),
                GeneratedCombatDamage = versions.Sum(s => s.GeneratedCombatDamage),
                GeneratedRunDamage = versions.Sum(s => s.GeneratedRunDamage),
                CombatTimesDrawn = versions.Sum(s => s.CombatTimesDrawn),
                CombatTimesPlayed = versions.Sum(s => s.CombatTimesPlayed),
                RawForgeCombat = versions.Sum(s => s.RawForgeCombat),
                ConnectedForgeCombat = versions.Sum(s => s.ConnectedForgeCombat),
                ReceivedForgeCombat = versions.Sum(s => s.ReceivedForgeCombat),
            };

            foreach (var v in versions)
            {
                AddAct(stacked.Act1, v.Act1);
                AddAct(stacked.Act2, v.Act2);
                AddAct(stacked.Act3, v.Act3);
                AddAct(stacked.Act4, v.Act4);
            }

            result.Add(stacked);
        }

        return result;
    }
}
