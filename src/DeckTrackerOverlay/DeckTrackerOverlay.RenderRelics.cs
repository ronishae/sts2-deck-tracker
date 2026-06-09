using Godot;
using System.Linq;

namespace DeckTracker;

public static partial class DeckTrackerOverlay
{
    private static void RenderFullScreenRelics()
    {
        _fullScreenHeadersContainer!.AddChild(CreateSortableHeader("RELIC NAME", "NAME", 300));

        string totalColText = (_showRunStats ? "RUN" : "COMBAT") + (_showRawForge ? " FORGE" : " DAMAGE");
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader(totalColText, "TOTAL_DMG", 180));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("AVG (#Fights)", "AVG_DMG", 130));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("HALLWAY (AVG) (#)", "HALLWAY_DMG", 200));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("ELITE (AVG) (#)", "ELITE_DMG", 200));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("BOSS (AVG) (#)", "BOSS_DMG", 200));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("ADDED", "ADDED", 80));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("REMOVED", "REMOVED", 90));

        var unsortedList = CardRegistry.EntityLedger.Values.OfType<RelicStats>()
            .Select(r => new { Stat = r, Agg = AggregateActData(r) })
            .Where(x => {
                decimal effCombat = _showRawForge ? x.Stat.RawForgeCombat : (x.Stat.CombatDamage + (_includeConnectedForge ? x.Stat.ConnectedForgeCombat - x.Stat.ReceivedForgeCombat : 0));
                decimal effRun = _showRawForge ? x.Agg.RawForgeTotal : (x.Agg.TotalDamage + (_includeConnectedForge ? x.Agg.ConnectedForgeTotal - x.Agg.ReceivedForgeTotal : 0));
                return _showRunStats ? effRun > 0 : effCombat > 0;
            })
            .ToList();

        var sortedList = _currentSort.Column switch
        {
            "NAME" => _currentSort.Ascending
                ? unsortedList.OrderBy(x => GetEntityDisplayTitle(x.Stat))
                : unsortedList.OrderByDescending(x => GetEntityDisplayTitle(x.Stat)),

            "TOTAL_DMG" => _currentSort.Ascending
                ? unsortedList.OrderBy(x => {
                    decimal effCombat = _showRawForge ? x.Stat.RawForgeCombat : (x.Stat.CombatDamage + (_includeConnectedForge ? x.Stat.ConnectedForgeCombat - x.Stat.ReceivedForgeCombat : 0));
                    decimal effRun = _showRawForge ? x.Agg.RawForgeTotal : (x.Agg.TotalDamage + (_includeConnectedForge ? x.Agg.ConnectedForgeTotal - x.Agg.ReceivedForgeTotal : 0));
                    return _showRunStats ? effRun : effCombat;
                })
                : unsortedList.OrderByDescending(x => {
                    decimal effCombat = _showRawForge ? x.Stat.RawForgeCombat : (x.Stat.CombatDamage + (_includeConnectedForge ? x.Stat.ConnectedForgeCombat - x.Stat.ReceivedForgeCombat : 0));
                    decimal effRun = _showRawForge ? x.Agg.RawForgeTotal : (x.Agg.TotalDamage + (_includeConnectedForge ? x.Agg.ConnectedForgeTotal - x.Agg.ReceivedForgeTotal : 0));
                    return _showRunStats ? effRun : effCombat;
                }),

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

            _ => unsortedList.OrderByDescending(x => {
                decimal effCombat = _showRawForge ? x.Stat.RawForgeCombat : (x.Stat.CombatDamage + (_includeConnectedForge ? x.Stat.ConnectedForgeCombat - x.Stat.ReceivedForgeCombat : 0));
                decimal effRun = _showRawForge ? x.Agg.RawForgeTotal : (x.Agg.TotalDamage + (_includeConnectedForge ? x.Agg.ConnectedForgeTotal - x.Agg.ReceivedForgeTotal : 0));
                return _showRunStats ? effRun : effCombat;
            })
        };

        var finalSort = sortedList;
        if (_currentSort.Column != "TOTAL_DMG")
        {
            finalSort = finalSort.ThenByDescending(x => {
                decimal effCombat = _showRawForge ? x.Stat.RawForgeCombat : (x.Stat.CombatDamage + (_includeConnectedForge ? x.Stat.ConnectedForgeCombat - x.Stat.ReceivedForgeCombat : 0));
                decimal effRun = _showRawForge ? x.Agg.RawForgeTotal : (x.Agg.TotalDamage + (_includeConnectedForge ? x.Agg.ConnectedForgeTotal - x.Agg.ReceivedForgeTotal : 0));
                return _showRunStats ? effRun : effCombat;
            });
        }

        var relicList = finalSort.ThenBy(x => x.Stat.FloorAdded).ToList();

        foreach (var item in relicList)
        {
            var stat = item.Stat;
            var agg = item.Agg;
            HBoxContainer row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

            Label nameLabel = new Label { Text = GetEntityDisplayTitle(stat), CustomMinimumSize = new Vector2(300, 0) };

            decimal valTotal = _showRawForge ? agg.RawForgeTotal : (agg.TotalDamage + (_includeConnectedForge ? agg.ConnectedForgeTotal - agg.ReceivedForgeTotal : 0));
            decimal avgTotal = agg.EncountersSeenTotal > 0 ? valTotal / agg.EncountersSeenTotal : 0;

            decimal valHallway = _showRawForge ? agg.RawForgeHallway : (agg.DamageHallway + (_includeConnectedForge ? agg.ConnectedForgeHallway - agg.ReceivedForgeHallway : 0));
            decimal avgHallway = agg.EncountersSeenHallway > 0 ? valHallway / agg.EncountersSeenHallway : 0;

            decimal valElite = _showRawForge ? agg.RawForgeElite : (agg.DamageElite + (_includeConnectedForge ? agg.ConnectedForgeElite - agg.ReceivedForgeElite : 0));
            decimal avgElite = agg.EncountersSeenElite > 0 ? valElite / agg.EncountersSeenElite : 0;

            decimal valBoss = _showRawForge ? agg.RawForgeBoss : (agg.DamageBoss + (_includeConnectedForge ? agg.ConnectedForgeBoss - agg.ReceivedForgeBoss : 0));
            decimal avgBoss = agg.EncountersSeenBoss > 0 ? valBoss / agg.EncountersSeenBoss : 0;

            Color statColor = new Color("A0A8B4");

            // In combat mode, the Total column shows combat-specific damage; other columns use run-level averages
            decimal damageToShow = _showRunStats ? valTotal :
                (_showRawForge ? stat.RawForgeCombat : (stat.CombatDamage + (_includeConnectedForge ? stat.ConnectedForgeCombat - stat.ReceivedForgeCombat : 0)));

            Label totalDataLabel = new Label { Text = $"{damageToShow:0.##}", CustomMinimumSize = new Vector2(180, 0) };
            if (!_showRawForge && _includeConnectedForge)
            {
                bool hasForge = _showRunStats ? agg.ConnectedForgeTotal > 0 : stat.ConnectedForgeCombat > 0;
                totalDataLabel.AddThemeColorOverride("font_color", hasForge ? new Color("38BDF8") : new Color("4ADE80"));
            }
            else
            {
                totalDataLabel.AddThemeColorOverride("font_color", statColor);
            }

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

            string removedText = stat.FloorRemoved <= 0 ? "N/A" : stat.FloorRemoved.ToString();
            Label removedLabel = new Label { Text = removedText, CustomMinimumSize = new Vector2(90, 0) };
            removedLabel.AddThemeColorOverride("font_color", new Color("A0A8B4"));

            row.AddChild(nameLabel);
            row.AddChild(totalDataLabel);
            row.AddChild(avgDataLabel);
            row.AddChild(hallwayLabel);
            row.AddChild(eliteLabel);
            row.AddChild(bossLabel);
            row.AddChild(addedLabel);
            row.AddChild(removedLabel);

            _fullScreenRowsContainer!.AddChild(CreateHoverableRow(row));
        }
    }
}
