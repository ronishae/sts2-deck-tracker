using Godot;

namespace DeckTracker;

public static partial class DeckTrackerOverlay
{
    private static void UpdateSmallUI(List<CardStats> stats)
    {
        if (!GodotObject.IsInstanceValid(_smallRowsContainer)) return;

        foreach (Node child in _smallRowsContainer.GetChildren()) { _smallRowsContainer.RemoveChild(child); child.QueueFree(); }

        // Cards (stacked), plus relics and potions read straight from the ledger like the full-screen tabs.
        var entities = new List<EntityStats>();
        entities.AddRange(BuildStackedCardList(ApplyCombatOnlyFilter(stats)).Where(s => s.CardType != "Status"));
        entities.AddRange(CardRegistry.EntityLedger.Values.OfType<RelicStats>());
        entities.AddRange(CardRegistry.EntityLedger.Values.OfType<PotionStats>());

        var present = entities.Where(s => EffectiveCombat(s) > 0).ToList();

        // A generated card's damage already lives in its creator's generated bucket, so drop the card row
        // when its creator (card/relic/potion) is itself shown — avoids counting the same damage twice. The
        // creator id on a card's GeneratedById is the ledger-key form ("RELIC_" + entry for relics).
        string CreatorId(EntityStats e) => e is RelicStats ? "RELIC_" + e.Id : e.Id;
        var presentCreatorIds = present.Select(CreatorId).ToHashSet();

        var rows = present
            .Where(s => s is not CardStats c || string.IsNullOrEmpty(c.GeneratedById) || !presentCreatorIds.Contains(c.GeneratedById))
            .OrderByDescending(EffectiveCombat)
            .ThenBy(s => s.FloorAdded)
            .ToList();

        foreach (var stat in rows)
        {
            HBoxContainer row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

            // Only cards roll up under a generator; relics/potions have no generator suffix.
            var generatorName = stat is CardStats card ? ResolveGeneratorDisplayName(card.GeneratedById) : null;
            var nameText = generatorName != null ? $"{GetEntityDisplayTitle(stat)} ({generatorName})" : GetEntityDisplayTitle(stat);
            Label nameLabel = new Label { Text = nameText, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

            decimal combatDamage = EffectiveCombat(stat);
            Label damageLabel = new Label { Text = combatDamage.ToString("0.##") };
            Color dmgColor = (_includeConnectedForge && stat.ConnectedForgeCombat > 0) ? new Color("38BDF8") : new Color("4ADE80");
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
            ApplyPlayerFilterTextColor(check, idx);
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
        var combatScopedStats = ApplyCombatOnlyFilter(stats);
        var mergedStats = _mergeCardVersions ? BuildMergedCardList(combatScopedStats) : combatScopedStats;
        var effectiveStats = BuildStackedCardList(mergedStats);

        _fullScreenHeadersContainer!.AddChild(CreateSortableHeader("CARD NAME", "NAME", 300));
        string combatColText = "COMBAT" + (_showRawForge ? " FORGE" : " DMG");
        string runColText = "RUN" + (_showRawForge ? " FORGE" : " DMG");
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader(runColText, "RUN_DMG", 150));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader(combatColText, "COMBAT_DMG", 150));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("% PLAYED", "PLAY_RATE", 130));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("RUN AVG (#)", "AVG_DMG", 130));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("HALLWAY (AVG) (#)", "HALLWAY_DMG", 185));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("ELITE (AVG) (#)", "ELITE_DMG", 185));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("BOSS (AVG) (#)", "BOSS_DMG", 185));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("ADDED", "ADDED", 80));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("REMOVED", "REMOVED", 90));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("EVOLVED", "EVOLVED", 80));

        // Every row shows its SUBTREE total of direct damage: a card's own damage counted once at its node,
        // summed over all descendants. The grand total (sum of top-level rows) therefore equals the true
        // total by construction — no reliance on the routed generated bucket and no double counting.
        var working = effectiveStats
            .Where(s => s.CardType != "Status" && _enabledPlayers.Contains(s.PlayerIndex))
            .ToList();

        var presentIds = working.Select(s => s.Id).ToHashSet();
        bool IsNested(CardStats s) =>
            !string.IsNullOrEmpty(s.GeneratedByImmediateId) && presentIds.Contains(s.GeneratedByImmediateId);

        var childrenByImmediate = working
            .Where(IsNested)
            .GroupBy(s => s.GeneratedByImmediateId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Subtree sum of DIRECT (bucket-excluded) damage for a node and all its descendants. Memoized; the
        // placeholder write before recursing guards against any accidental cycle in the immediate links.
        var subtreeCache = new Dictionary<string, (decimal Combat, decimal Hallway, decimal Elite, decimal Boss)>();
        (decimal Combat, decimal Hallway, decimal Elite, decimal Boss) Subtree(CardStats node)
        {
            if (subtreeCache.TryGetValue(node.Id, out var cached)) return cached;
            subtreeCache[node.Id] = default;

            var own = AggregateActData(node);
            var combat = node.CombatDamage;
            var hallway = own.DamageHallway;
            var elite = own.DamageElite;
            var boss = own.DamageBoss;

            if (childrenByImmediate.TryGetValue(node.Id, out var kids))
            {
                foreach (var sub in kids.Select(Subtree))
                {
                    combat += sub.Combat;
                    hallway += sub.Hallway;
                    elite += sub.Elite;
                    boss += sub.Boss;
                }
            }

            var result = (combat, hallway, elite, boss);
            subtreeCache[node.Id] = result;
            return result;
        }

        // Synthetic display stat/agg: subtree damage in the damage columns, the node's own
        // encounters/play-rate/forge/identity elsewhere, generated bucket zeroed (the subtree already folds in
        // descendant damage, so the existing Effective* helpers yield the subtree totals unchanged).
        var displayCache = new Dictionary<string, (CardStats Stat, ActData Agg)>();
        (CardStats Stat, ActData Agg) Display(CardStats node)
        {
            if (displayCache.TryGetValue(node.Id, out var cached)) return cached;
            var (combat, hallway, elite, boss) = Subtree(node);
            var stat = (CardStats)node.Clone();
            stat.CombatDamage = combat;
            stat.GeneratedCombatDamage = 0;
            var agg = AggregateActData(node);
            agg.DamageHallway = hallway;
            agg.DamageElite = elite;
            agg.DamageBoss = boss;
            agg.GeneratedDamageHallway = 0;
            agg.GeneratedDamageElite = 0;
            agg.GeneratedDamageBoss = 0;
            var pair = (stat, agg);
            displayCache[node.Id] = pair;
            return pair;
        }

        // Hide 0 Damage keeps a node when its SUBTREE has damage, so an intermediate generator (e.g. Discovery)
        // stays whenever a descendant dealt damage. Kept nodes imply kept ancestors, so the tree never orphans.
        bool Kept(CardStats s)
        {
            if (!_hideZeroDamageCards)
            {
                return true;
            }
            var (stat, agg) = Display(s);
            return EffectiveCombat(stat) > 0 || EffectiveRun(agg) > 0;
        }

        // Orders sibling nodes by the active sort column using their subtree (Display) values, with the same
        // secondary sorts as the flat list (run damage, then floor added).
        List<CardStats> SortNodes(IEnumerable<CardStats> nodes)
        {
            IOrderedEnumerable<CardStats> ordered = _currentSort.Column switch
            {
                "NAME"       => SortBy(nodes, GetEntityDisplayTitle),
                "PLAY_RATE"  => SortBy(nodes, s => Display(s).Agg.PlayRate),
                "COMBAT_DMG" => SortBy(nodes, s => EffectiveCombat(Display(s).Stat)),
                "RUN_DMG"    => SortBy(nodes, s => EffectiveRun(Display(s).Agg)),
                "AVG_DMG"    => SortBy(nodes, s => Display(s).Agg.EncountersSeenTotal > 0 ? EffectiveRun(Display(s).Agg) / Display(s).Agg.EncountersSeenTotal : 0),
                "HALLWAY_DMG" => SortBy(nodes, s => EffectiveHallway(Display(s).Agg)),
                "ELITE_DMG"  => SortBy(nodes, s => EffectiveElite(Display(s).Agg)),
                "BOSS_DMG"   => SortBy(nodes, s => EffectiveBoss(Display(s).Agg)),
                "ADDED"      => SortBy(nodes, s => s.FloorAdded),
                "REMOVED"    => SortBy(nodes, s => s.FloorRemoved),
                "EVOLVED"    => SortBy(nodes, s => s.FloorLeftDeck),
                _            => nodes.OrderByDescending(s => EffectiveRun(Display(s).Agg))
            };

            return ordered
                .ThenByDescending(s => EffectiveRun(Display(s).Agg))
                .ThenBy(s => s.FloorAdded)
                .ToList();
        }

        var topLevel = SortNodes(working.Where(s => !IsNested(s) && Kept(s)));

        // Recursively emits a node and, when it is an expanded generator, its immediate children indented one
        // level deeper — so a generation chain (Spectrum Shift -> Discovery -> Noxious Fumes) reads as a tree.
        void EmitNode(CardStats node, int depth)
        {
            childrenByImmediate.TryGetValue(node.Id, out var rawKids);
            var kids = rawKids?.Where(Kept).ToList() ?? new List<CardStats>();
            var isGenerator = kids.Count > 0;
            var expanded = isGenerator && _expandedGenerators.Contains(node.Id);

            var branch = depth > 0 ? new string(' ', depth * 4) + "└ " : "";
            var arrow = isGenerator ? (expanded ? "▼ " : "▶ ") : "";
            var prefix = branch + arrow;

            // A top-level generated card whose immediate creator isn't a present row (a relic/potion, or a
            // filtered-out card) shows its source inline so the row makes sense on its own.
            var suffix = "";
            if (depth == 0)
            {
                var generatorName = ResolveGeneratorDisplayName(node.GeneratedByImmediateId)
                    ?? ResolveGeneratorDisplayName(node.GeneratedById);
                if (generatorName != null) suffix = $" ({generatorName})";
            }

            var (displayStat, displayAgg) = Display(node);
            var content = BuildCardRow(displayStat, displayAgg, prefix, suffix);
            var rowPanel = CreateHoverableRow(content, GetPlayerRowBgColor(node.PlayerIndex));

            if (isGenerator)
            {
                // Let clicks anywhere on the generator row reach the panel so it toggles expansion.
                DisableMouseBlocking(content);
                rowPanel.GuiInput += (InputEvent ev) => HandleGeneratorRowInput(ev, node.Id);
            }

            _fullScreenRowsContainer!.AddChild(rowPanel);

            if (!expanded)
            {
                return;
            }

            foreach (var kid in SortNodes(kids))
            {
                EmitNode(kid, depth + 1);
            }
        }

        foreach (var node in topLevel)
        {
            EmitNode(node, 0);
        }
    }

    private static void HandleGeneratorRowInput(InputEvent ev, string generatorId)
    {
        if (ev is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
        {
            return;
        }
        if (!_expandedGenerators.Remove(generatorId))
        {
            _expandedGenerators.Add(generatorId);
        }
        RedrawUI(_latestStats);
    }

    // Makes a control and all its descendants ignore mouse input so a clickable parent (a generator row)
    // receives the click instead of being blocked by child labels.
    private static void DisableMouseBlocking(Control control)
    {
        control.MouseFilter = Control.MouseFilterEnum.Ignore;
        foreach (var child in control.GetChildren())
        {
            if (child is Control childControl)
            {
                DisableMouseBlocking(childControl);
            }
        }
    }

    // Builds one card row's content. namePrefix lets callers prepend an expand arrow (generators) or
    // an indent marker (nested generated children); every damage column shows the summed Effective value.
    private static Control BuildCardRow(CardStats stat, ActData agg, string namePrefix, string nameSuffix = "")
    {
        HBoxContainer row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

        // Fixed-width, ellipsis-clipped name so a deeply-indented generation chain never widens the column
        // and pushes the other columns out of alignment; the full name is available on hover.
        string fullName = namePrefix + GetEntityDisplayTitle(stat) + nameSuffix;
        Label nameLabel = new Label
        {
            Text = fullName,
            CustomMinimumSize = new Vector2(300, 0),
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            TooltipText = fullName
        };
        Label playRateLabel = new Label { Text = $"{agg.TimesPlayed}/{agg.TimesDrawn} ({agg.PlayRate * 100:0.#}%)", CustomMinimumSize = new Vector2(130, 0) };
        playRateLabel.AddThemeColorOverride("font_color", new Color("A0A8B4"));

        decimal valCombat = EffectiveCombat(stat);
        decimal valRunTotal = EffectiveRun(agg);
        decimal avgTotal = agg.EncountersSeenTotal > 0 ? valRunTotal / agg.EncountersSeenTotal : 0;

        decimal valHallway = EffectiveHallway(agg);
        decimal avgHallway = agg.EncountersSeenHallway > 0 ? valHallway / agg.EncountersSeenHallway : 0;

        decimal valElite = EffectiveElite(agg);
        decimal avgElite = agg.EncountersSeenElite > 0 ? valElite / agg.EncountersSeenElite : 0;

        decimal valBoss = EffectiveBoss(agg);
        decimal avgBoss = agg.EncountersSeenBoss > 0 ? valBoss / agg.EncountersSeenBoss : 0;

        Color statColor = new Color("A0A8B4");

        Label runDataLabel = new Label { Text = $"{valRunTotal:0.##}", CustomMinimumSize = new Vector2(150, 0) };
        runDataLabel.AddThemeColorOverride("font_color", !_showRawForge && _includeConnectedForge && agg.ConnectedForgeTotal > 0 ? new Color("38BDF8") : (_showRawForge ? statColor : new Color("4ADE80")));

        Label combatDataLabel = new Label { Text = $"{valCombat:0.##}", CustomMinimumSize = new Vector2(150, 0) };
        combatDataLabel.AddThemeColorOverride("font_color", !_showRawForge && _includeConnectedForge && stat.ConnectedForgeCombat > 0 ? new Color("38BDF8") : (_showRawForge ? statColor : new Color("4ADE80")));

        Label avgDataLabel = new Label { Text = $"({avgTotal:0.#}) (#{agg.EncountersSeenTotal})", CustomMinimumSize = new Vector2(130, 0) };
        avgDataLabel.AddThemeColorOverride("font_color", statColor);

        Label hallwayLabel = new Label { Text = $"{valHallway:0.##} ({avgHallway:0.#}) (#{agg.EncountersSeenHallway})", CustomMinimumSize = new Vector2(185, 0) };
        hallwayLabel.AddThemeColorOverride("font_color", statColor);

        Label eliteLabel = new Label { Text = $"{valElite:0.##} ({avgElite:0.#}) (#{agg.EncountersSeenElite})", CustomMinimumSize = new Vector2(185, 0) };
        eliteLabel.AddThemeColorOverride("font_color", statColor);

        Label bossLabel = new Label { Text = $"{valBoss:0.##} ({avgBoss:0.#}) (#{agg.EncountersSeenBoss})", CustomMinimumSize = new Vector2(185, 0) };
        bossLabel.AddThemeColorOverride("font_color", statColor);

        string addedText = stat.FloorAdded == 0 ? "GEN" : stat.FloorAdded.ToString();
        Label addedLabel = new Label { Text = addedText, CustomMinimumSize = new Vector2(80, 0) };
        addedLabel.AddThemeColorOverride("font_color", new Color("A0A8B4"));

        row.AddChild(nameLabel); row.AddChild(runDataLabel); row.AddChild(combatDataLabel); row.AddChild(playRateLabel);
        row.AddChild(avgDataLabel);
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

        return row;
    }
}
