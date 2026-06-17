namespace DeckTracker;

public static partial class DeckTrackerOverlay
{
    // Merge Versions folds a card's own prior-version history (its pre-upgrade/enchant "ghost" rows) into
    // the surviving upgraded copy, but never merges two separate live copies. With multiple upgraded
    // copies, each ghost is distributed to a distinct surviving copy so they stay distinct rows.
    private static List<CardStats> BuildMergedCardList(List<CardStats> stats)
    {
        // BaseCardKey already encodes the owner, but include PlayerIndex explicitly so versions never merge across players.
        // GeneratedById is included so generated cards (which share a BaseCardKey) never merge across different creators.
        var groups = stats.GroupBy(s => (string.IsNullOrEmpty(s.BaseCardKey) ? s.Id : s.BaseCardKey, s.PlayerIndex, s.GeneratedById));
        var result = new List<CardStats>();
        var idRemap = new Dictionary<string, string>();

        foreach (var group in groups)
        {
            var entries = group.ToList();
            var evolvedHistory = entries.Where(IsEvolvedRetired).ToList();
            var standalone = entries.Where(s => !IsEvolvedRetired(s)).ToList();

            // The whole lineage left the deck via evolution (no surviving copy) — collapse its history.
            if (standalone.Count == 0)
            {
                var mergedLineage = MergeVersions(evolvedHistory);
                RecordMerge(idRemap, evolvedHistory, mergedLineage.Id);
                result.Add(mergedLineage);
                continue;
            }

            // Each surviving copy keeps its own row. Distribute each ghost to a strictly-more-evolved
            // survivor, spread one-per-copy, so multiple upgraded copies remain multiple rows.
            var absorbed = standalone.ToDictionary(s => s, _ => new List<CardStats>());
            var unpaired = new List<CardStats>();
            foreach (var ghost in evolvedHistory)
            {
                var target = standalone
                    .Where(s => EvolutionRank(s) > EvolutionRank(ghost))
                    .OrderBy(s => absorbed[s].Count)
                    .ThenByDescending(EvolutionRank)
                    .FirstOrDefault();
                if (target == null)
                {
                    unpaired.Add(ghost);
                }
                else
                {
                    absorbed[target].Add(ghost);
                }
            }

            foreach (var survivor in standalone)
            {
                var ghosts = absorbed[survivor];
                if (ghosts.Count == 0)
                {
                    result.Add(survivor);
                    continue;
                }
                ghosts.Add(survivor);
                var merged = MergeVersions(ghosts);
                RecordMerge(idRemap, ghosts, merged.Id);
                result.Add(merged);
            }
            result.AddRange(unpaired);
        }

        // Re-point generated cards whose creator version was folded into a merged row, so they nest under
        // the surviving creator instead of orphaning. Remap values are always survivors, so one pass suffices.
        // Clone before rewriting: these CardStats are the shared _latestStats objects reused across redraws,
        // so mutating them in place would make the re-parenting persist after Merge Versions is toggled off.
        if (idRemap.Count > 0)
        {
            for (var i = 0; i < result.Count; i++)
            {
                var card = result[i];
                var rootRemapped = !string.IsNullOrEmpty(card.GeneratedById) && idRemap.TryGetValue(card.GeneratedById, out var newRootId);
                var immediateRemapped = !string.IsNullOrEmpty(card.GeneratedByImmediateId) && idRemap.TryGetValue(card.GeneratedByImmediateId, out var newImmediateId);
                if (rootRemapped || immediateRemapped)
                {
                    var clone = (CardStats)card.Clone();
                    if (rootRemapped) clone.GeneratedById = idRemap[card.GeneratedById];
                    if (immediateRemapped) clone.GeneratedByImmediateId = idRemap[card.GeneratedByImmediateId];
                    result[i] = clone;
                }
            }
        }

        return result;
    }

    private static void RecordMerge(Dictionary<string, string> idRemap, List<CardStats> versions, string representativeId)
    {
        foreach (var v in versions)
        {
            if (v.Id != representativeId)
            {
                idRemap[v.Id] = representativeId;
            }
        }
    }

    // A card's pre-upgrade/enchant history: a retired version that left the deck via evolution (not removal).
    private static bool IsEvolvedRetired(CardStats s) =>
        !s.IsActive && s.FloorRemoved == -1 && s.FloorLeftDeck > 0;

    // How evolved a version is: upgrade dominates, enchant breaks ties. Used to fold a ghost only into a
    // strictly-more-evolved survivor (so a never-upgraded copy never absorbs history).
    private static int EvolutionRank(CardStats s) =>
        s.UpgradeLevel * 2 + (!string.IsNullOrEmpty(s.Enchantment) && s.Enchantment != "None" ? 1 : 0);

    // Combines a set of versions (a surviving copy plus its prior-version history, or a fully-departed
    // lineage) into one row: identity from the most-evolved/active version, summed stats, and the EVOLVED
    // floor taken from any retired-evolved version.
    private static CardStats MergeVersions(List<CardStats> versions)
    {
        var representative = versions
            .OrderByDescending(EvolutionRank)
            .ThenByDescending(s => s.IsActive ? 1 : 0)
            .First();

        var evolvedFloor = versions
            .Where(IsEvolvedRetired)
            .Select(s => s.FloorLeftDeck)
            .DefaultIfEmpty(-1)
            .Max();

        var merged = new CardStats
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
            FloorRemoved = representative.FloorRemoved,
            FloorLeftDeck = evolvedFloor,
            IsActive = versions.Any(s => s.IsActive),
            CopiesInDeck = versions.Sum(s => s.CopiesInDeck),
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
            AddAct(merged.Act1, v.Act1);
            AddAct(merged.Act2, v.Act2);
            AddAct(merged.Act3, v.Act3);
            AddAct(merged.Act4, v.Act4);
        }

        return merged;
    }
}
