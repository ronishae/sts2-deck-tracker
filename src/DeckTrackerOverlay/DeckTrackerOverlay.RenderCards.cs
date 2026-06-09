using Godot;
using System.Collections.Generic;
using System.Linq;

namespace DeckTracker;

public static partial class DeckTrackerOverlay
{
    // Cards that are always stacked regardless of count (generated in large quantities during combat)
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

    private static void UpdateSmallUI(List<CardStats> stats)
    {
        if (!GodotObject.IsInstanceValid(_smallRowsContainer)) return;

        foreach (Node child in _smallRowsContainer.GetChildren()) { _smallRowsContainer.RemoveChild(child); child.QueueFree(); }

        var allCards = BuildStackedCardList(stats)
            .Where(s => s.CardType != "Status")
            .Select(s => new { Stat = s, Agg = AggregateActData(s) })
            .Where(x => {
                decimal effCombat = _showRawForge ? x.Stat.RawForgeCombat : x.Stat.CombatDamage + (_includeConnectedForge ? x.Stat.ConnectedForgeCombat - x.Stat.ReceivedForgeCombat : 0);
                decimal effRun = _showRawForge ? x.Agg.RawForgeTotal : x.Agg.TotalDamage + (_includeConnectedForge ? x.Agg.ConnectedForgeTotal - x.Agg.ReceivedForgeTotal : 0);
                return _showRunStats ? effRun > 0 : effCombat > 0;
            })
            .OrderByDescending(x => _showRunStats ?
                (_showRawForge ? x.Agg.RawForgeTotal : (x.Agg.TotalDamage + (_includeConnectedForge ? x.Agg.ConnectedForgeTotal - x.Agg.ReceivedForgeTotal : 0))) :
                (_showRawForge ? x.Stat.RawForgeCombat : (x.Stat.CombatDamage + (_includeConnectedForge ? x.Stat.ConnectedForgeCombat - x.Stat.ReceivedForgeCombat : 0))))
            .ThenBy(x => x.Stat.FloorAdded)
            .ToList();

        foreach (var item in allCards)
        {
            var stat = item.Stat;
            var agg = item.Agg;
            HBoxContainer row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

            Label nameLabel = new Label { Text = GetEntityDisplayTitle(stat), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

            decimal damageToShow = _showRunStats ?
                (agg.TotalDamage + (_includeConnectedForge ? agg.ConnectedForgeTotal - agg.ReceivedForgeTotal : 0)) :
                (stat.CombatDamage + (_includeConnectedForge ? stat.ConnectedForgeCombat - stat.ReceivedForgeCombat : 0));

            Label damageLabel = new Label { Text = damageToShow.ToString("0.##") };
            Color dmgColor = (_includeConnectedForge && ((_showRunStats ? agg.ConnectedForgeTotal : stat.ConnectedForgeCombat) > 0)) ? new Color("38BDF8") : new Color("4ADE80");
            damageLabel.AddThemeColorOverride("font_color", dmgColor);

            row.AddChild(nameLabel);
            row.AddChild(damageLabel);
            _smallRowsContainer.AddChild(CreateHoverableRow(row, GetPlayerRowBgColor(stat.PlayerIndex)));
        }
    }

    private static void RebuildPlayerFilters()
    {
        if (!GodotObject.IsInstanceValid(_playerFiltersContainer)) return;

        foreach (Node child in _playerFiltersContainer.GetChildren()) { _playerFiltersContainer.RemoveChild(child); child.QueueFree(); }

        _playerFiltersContainer.Visible = CardRegistry.PlayerLabels.Count > 1;
        if (!_playerFiltersContainer.Visible) return;

        var label = new Label { Text = "Players: " };
        label.AddThemeColorOverride("font_color", new Color("FACC15"));
        _playerFiltersContainer.AddChild(label);

        foreach (var kvp in CardRegistry.PlayerLabels.OrderBy(x => x.Key))
        {
            var idx = kvp.Key;
            var check = new CheckBox { Text = kvp.Value, ButtonPressed = _enabledPlayers.Contains(idx), FocusMode = Control.FocusModeEnum.None };
            check.AddThemeColorOverride("font_color", GetPlayerTextColor(idx));
            check.Toggled += (val) =>
            {
                if (val) _enabledPlayers.Add(idx);
                else _enabledPlayers.Remove(idx);
                RedrawUI(_latestStats);
            };
            _playerFiltersContainer.AddChild(check);
        }
    }

    private static void RenderFullScreenCards(List<CardStats> stats)
    {
        var mergedStats = _mergeCardVersions ? BuildMergedCardList(stats) : stats;
        var effectiveStats = BuildStackedCardList(mergedStats);

        _fullScreenHeadersContainer!.AddChild(CreateSortableHeader("CARD NAME", "NAME", 300));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("% PLAYED", "PLAY_RATE", 150));
        string totalColText = (_showRunStats ? "RUN" : "COMBAT") + (_showRawForge ? " FORGE" : " DAMAGE");
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader(totalColText, "TOTAL_DMG", 180));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("AVG (#)", "AVG_DMG", 130));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("HALLWAY (AVG) (#)", "HALLWAY_DMG", 200));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("ELITE (AVG) (#)", "ELITE_DMG", 200));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("BOSS (AVG) (#)", "BOSS_DMG", 200));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("ADDED", "ADDED", 80));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("REMOVED", "REMOVED", 90));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("EVOLVED", "EVOLVED", 80));

        decimal EffectiveValue(CardStats s, ActData a) =>
            _showRunStats
                ? (_showRawForge ? a.RawForgeTotal : (a.TotalDamage + (_includeConnectedForge ? a.ConnectedForgeTotal - a.ReceivedForgeTotal : 0)))
                : (_showRawForge ? s.RawForgeCombat : (s.CombatDamage + (_includeConnectedForge ? s.ConnectedForgeCombat - s.ReceivedForgeCombat : 0)));

        var unsortedList = effectiveStats
            .Where(s => s.CardType != "Status")
            .Where(s => _enabledPlayers.Contains(s.PlayerIndex))
            .Select(s => new { Stat = s, Agg = AggregateActData(s) })
            .Where(x => !_hideZeroDamageCards || EffectiveValue(x.Stat, x.Agg) > 0)
            .ToList();

        var sortedList = _currentSort.Column switch
        {
            "NAME" => _currentSort.Ascending
                ? unsortedList.OrderBy(x => GetEntityDisplayTitle(x.Stat))
                : unsortedList.OrderByDescending(x => GetEntityDisplayTitle(x.Stat)),

            "PLAY_RATE" => _currentSort.Ascending
                ? unsortedList.OrderBy(x => _showRunStats ? x.Agg.PlayRate : (x.Stat.CombatTimesDrawn > 0 ? (decimal)x.Stat.CombatTimesPlayed / x.Stat.CombatTimesDrawn : 0))
                : unsortedList.OrderByDescending(x => _showRunStats ? x.Agg.PlayRate : (x.Stat.CombatTimesDrawn > 0 ? (decimal)x.Stat.CombatTimesPlayed / x.Stat.CombatTimesDrawn : 0)),

            "TOTAL_DMG" => _currentSort.Ascending
                ? unsortedList.OrderBy(x => EffectiveValue(x.Stat, x.Agg))
                : unsortedList.OrderByDescending(x => EffectiveValue(x.Stat, x.Agg)),

            "AVG_DMG" => _currentSort.Ascending
                ? unsortedList.OrderBy(x => x.Agg.EncountersSeenTotal > 0 ? (_showRawForge ? x.Agg.RawForgeTotal : (x.Agg.TotalDamage + (_includeConnectedForge ? x.Agg.ConnectedForgeTotal - x.Agg.ReceivedForgeTotal : 0))) / x.Agg.EncountersSeenTotal : 0)
                : unsortedList.OrderByDescending(x => x.Agg.EncountersSeenTotal > 0 ? (_showRawForge ? x.Agg.RawForgeTotal : (x.Agg.TotalDamage + (_includeConnectedForge ? x.Agg.ConnectedForgeTotal - x.Agg.ReceivedForgeTotal : 0))) / x.Agg.EncountersSeenTotal : 0),

            "HALLWAY_DMG" => _currentSort.Ascending
                ? unsortedList.OrderBy(x => _showRawForge ? x.Agg.RawForgeHallway : (x.Agg.DamageHallway + (_includeConnectedForge ? x.Agg.ConnectedForgeHallway - x.Agg.ReceivedForgeHallway : 0)))
                : unsortedList.OrderByDescending(x => _showRawForge ? x.Agg.RawForgeHallway : (x.Agg.DamageHallway + (_includeConnectedForge ? x.Agg.ConnectedForgeHallway - x.Agg.ReceivedForgeHallway : 0))),

            "ELITE_DMG" => _currentSort.Ascending
                ? unsortedList.OrderBy(x => _showRawForge ? x.Agg.RawForgeElite : (x.Agg.DamageElite + (_includeConnectedForge ? x.Agg.ConnectedForgeElite - x.Agg.ReceivedForgeElite : 0)))
                : unsortedList.OrderByDescending(x => _showRawForge ? x.Agg.RawForgeElite : (x.Agg.DamageElite + (_includeConnectedForge ? x.Agg.ConnectedForgeElite - x.Agg.ReceivedForgeElite : 0))),

            "BOSS_DMG" => _currentSort.Ascending
                ? unsortedList.OrderBy(x => _showRawForge ? x.Agg.RawForgeBoss : (x.Agg.DamageBoss + (_includeConnectedForge ? x.Agg.ConnectedForgeBoss - x.Agg.ReceivedForgeBoss : 0)))
                : unsortedList.OrderByDescending(x => _showRawForge ? x.Agg.RawForgeBoss : (x.Agg.DamageBoss + (_includeConnectedForge ? x.Agg.ConnectedForgeBoss - x.Agg.ReceivedForgeBoss : 0))),

            "ADDED" => _currentSort.Ascending
                ? unsortedList.OrderBy(x => x.Stat.FloorAdded)
                : unsortedList.OrderByDescending(x => x.Stat.FloorAdded),

            "REMOVED" => _currentSort.Ascending
                ? unsortedList.OrderBy(x => x.Stat.FloorRemoved)
                : unsortedList.OrderByDescending(x => x.Stat.FloorRemoved),

            "EVOLVED" => _currentSort.Ascending
                ? unsortedList.OrderBy(x => x.Stat.FloorLeftDeck)
                : unsortedList.OrderByDescending(x => x.Stat.FloorLeftDeck),

            _ => unsortedList.OrderByDescending(x => EffectiveValue(x.Stat, x.Agg))
        };

        var finalSort = sortedList;
        if (_currentSort.Column != "TOTAL_DMG")
        {
            finalSort = finalSort.ThenByDescending(x => EffectiveValue(x.Stat, x.Agg));
        }

        var allCards = finalSort.ThenBy(x => x.Stat.FloorAdded).ToList();

        foreach (var item in allCards)
        {
            var stat = item.Stat;
            var agg = item.Agg;
            HBoxContainer row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

            Label nameLabel = new Label { Text = GetEntityDisplayTitle(stat), CustomMinimumSize = new Vector2(300, 0) };
            var playTimesPlayed = _showRunStats ? agg.TimesPlayed : stat.CombatTimesPlayed;
            var playTimesDrawn = _showRunStats ? agg.TimesDrawn : stat.CombatTimesDrawn;
            var playRate = playTimesDrawn > 0 ? (decimal)playTimesPlayed / playTimesDrawn : 0;
            Label playRateLabel = new Label { Text = $"{playTimesPlayed}/{playTimesDrawn} ({playRate * 100:0.#}%)", CustomMinimumSize = new Vector2(150, 0) };
            playRateLabel.AddThemeColorOverride("font_color", new Color("A0A8B4"));

            decimal valTotal = EffectiveValue(stat, agg);
            decimal valRunTotal = _showRawForge ? agg.RawForgeTotal : (agg.TotalDamage + (_includeConnectedForge ? agg.ConnectedForgeTotal - agg.ReceivedForgeTotal : 0));
            decimal avgTotal = agg.EncountersSeenTotal > 0 ? valRunTotal / agg.EncountersSeenTotal : 0;

            decimal valHallway = _showRawForge ? agg.RawForgeHallway : (agg.DamageHallway + (_includeConnectedForge ? agg.ConnectedForgeHallway - agg.ReceivedForgeHallway : 0));
            decimal avgHallway = agg.EncountersSeenHallway > 0 ? valHallway / agg.EncountersSeenHallway : 0;

            decimal valElite = _showRawForge ? agg.RawForgeElite : (agg.DamageElite + (_includeConnectedForge ? agg.ConnectedForgeElite - agg.ReceivedForgeElite : 0));
            decimal avgElite = agg.EncountersSeenElite > 0 ? valElite / agg.EncountersSeenElite : 0;

            decimal valBoss = _showRawForge ? agg.RawForgeBoss : (agg.DamageBoss + (_includeConnectedForge ? agg.ConnectedForgeBoss - agg.ReceivedForgeBoss : 0));
            decimal avgBoss = agg.EncountersSeenBoss > 0 ? valBoss / agg.EncountersSeenBoss : 0;

            Color statColor = new Color("A0A8B4");

            Label totalDataLabel = new Label { Text = $"{valTotal:0.##}", CustomMinimumSize = new Vector2(180, 0) };
            totalDataLabel.AddThemeColorOverride("font_color", statColor);

            Label avgDataLabel = new Label { Text = $"({avgTotal:0.#}) (#{agg.EncountersSeenTotal})", CustomMinimumSize = new Vector2(130, 0) };
            avgDataLabel.AddThemeColorOverride("font_color", statColor);

            Label hallwayLabel = new Label { Text = $"{valHallway:0.##} ({avgHallway:0.#}) (#{agg.EncountersSeenHallway})", CustomMinimumSize = new Vector2(200, 0) };
            hallwayLabel.AddThemeColorOverride("font_color", statColor);

            Label eliteLabel = new Label { Text = $"{valElite:0.##} ({avgElite:0.#}) (#{agg.EncountersSeenElite})", CustomMinimumSize = new Vector2(200, 0) };
            eliteLabel.AddThemeColorOverride("font_color", statColor);

            Label bossLabel = new Label { Text = $"{valBoss:0.##} ({avgBoss:0.#}) (#{agg.EncountersSeenBoss})", CustomMinimumSize = new Vector2(200, 0) };
            bossLabel.AddThemeColorOverride("font_color", statColor);

            string addedText = stat.FloorAdded == 0 ? "GEN" : stat.FloorAdded.ToString();
            Label addedLabel = new Label { Text = addedText, CustomMinimumSize = new Vector2(80, 0) };
            addedLabel.AddThemeColorOverride("font_color", new Color("A0A8B4"));

            row.AddChild(nameLabel); row.AddChild(playRateLabel);
            row.AddChild(totalDataLabel); row.AddChild(avgDataLabel);
            row.AddChild(hallwayLabel); row.AddChild(eliteLabel); row.AddChild(bossLabel);
            row.AddChild(addedLabel);

            string removedText = stat.FloorRemoved <= 0 ? "N/A" : stat.FloorRemoved.ToString();
            Label removedLabel = new Label { Text = removedText, CustomMinimumSize = new Vector2(90, 0) };
            removedLabel.AddThemeColorOverride("font_color", new Color("A0A8B4"));

            // Show EVOLVED when the card left via upgrade/enchant (FloorLeftDeck set to a different floor than the removal floor).
            // For non-merged rows this is: FloorRemoved==-1 && FloorLeftDeck>0.
            // For merged rows FloorRemoved may be set independently, so we check FloorLeftDeck!=FloorRemoved to exclude pure removals.
            string evolvedText = (stat.FloorLeftDeck > 0 && stat.FloorLeftDeck != stat.FloorRemoved)
                ? stat.FloorLeftDeck.ToString()
                : "N/A";
            Label evolvedLabel = new Label { Text = evolvedText, CustomMinimumSize = new Vector2(80, 0) };
            evolvedLabel.AddThemeColorOverride("font_color", new Color("A0A8B4"));

            row.AddChild(removedLabel);
            row.AddChild(evolvedLabel);

            _fullScreenRowsContainer!.AddChild(CreateHoverableRow(row, GetPlayerRowBgColor(stat.PlayerIndex)));
        }
    }

    private static List<CardStats> BuildMergedCardList(List<CardStats> stats)
    {
        var groups = stats.GroupBy(s => string.IsNullOrEmpty(s.BaseCardKey) ? s.Id : s.BaseCardKey);
        var result = new List<CardStats>();

        foreach (var group in groups)
        {
            var versions = group.ToList();
            var representative = versions.OrderByDescending(s => s.UpgradeLevel).ThenByDescending(s => s.IsActive ? 1 : 0).First();

            // Evolution floor: most recent upgrade/enchant event across all retired versions in this group
            var evolvedFloor = versions
                .Where(s => !s.IsActive && s.FloorRemoved == -1 && s.FloorLeftDeck > 0)
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
                FloorAdded = versions.Min(s => s.FloorAdded),
                FloorRemoved = representative.FloorRemoved,
                FloorLeftDeck = evolvedFloor,
                IsActive = versions.Any(s => s.IsActive),
                CopiesInDeck = versions.Sum(s => s.CopiesInDeck),
                CombatDamage = versions.Sum(s => s.CombatDamage),
                RunDamage = versions.Sum(s => s.RunDamage),
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

            result.Add(merged);
        }

        return result;
    }

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

        var groups = stats.GroupBy(s => (ExtractBaseId(s), s.UpgradeLevel, s.Enchantment));

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
                FloorAdded = versions.Min(s => s.FloorAdded),
                FloorRemoved = -1,
                FloorLeftDeck = -1,
                IsActive = versions.Any(s => s.IsActive),
                CopiesInDeck = versions.Count,
                CombatDamage = versions.Sum(s => s.CombatDamage),
                RunDamage = versions.Sum(s => s.RunDamage),
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
