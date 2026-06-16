using Godot;

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

        // Effective combat = summed damage buckets (direct + generated-card damage + connected-forge adj).
        decimal SmallEffectiveCombat(CardStats s) =>
            _showRawForge ? s.RawForgeCombat : (s.CombatDamage + s.GeneratedCombatDamage + (_includeConnectedForge ? s.ConnectedForgeCombat - s.ReceivedForgeCombat : 0));

        var stacked = BuildStackedCardList(ApplyCombatOnlyFilter(stats)).Where(s => s.CardType != "Status").ToList();
        // Generated cards roll into their creator's total, so drop them when the creator is also shown to
        // avoid counting the same damage twice (matches the full-screen nesting behaviour).
        var smallPresentIds = stacked.Select(s => s.Id).ToHashSet();
        var allCards = stacked
            .Where(s => string.IsNullOrEmpty(s.GeneratedById) || !smallPresentIds.Contains(s.GeneratedById))
            .Select(s => new { Stat = s, Agg = AggregateActData(s) })
            .Where(x => SmallEffectiveCombat(x.Stat) > 0)
            .OrderByDescending(x => SmallEffectiveCombat(x.Stat))
            .ThenBy(x => x.Stat.FloorAdded)
            .ToList();

        foreach (var item in allCards)
        {
            var stat = item.Stat;
            var agg = item.Agg;
            HBoxContainer row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

            var generatorName = ResolveGeneratorDisplayName(stat.GeneratedById);
            var nameText = generatorName != null ? $"{GetEntityDisplayTitle(stat)} ({generatorName})" : GetEntityDisplayTitle(stat);
            Label nameLabel = new Label { Text = nameText, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

            decimal combatDamage = SmallEffectiveCombat(stat);
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
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("AVG (#)", "AVG_DMG", 130));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("HALLWAY (AVG) (#)", "HALLWAY_DMG", 185));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("ELITE (AVG) (#)", "ELITE_DMG", 185));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("BOSS (AVG) (#)", "BOSS_DMG", 185));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("ADDED", "ADDED", 80));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("REMOVED", "REMOVED", 90));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("EVOLVED", "EVOLVED", 80));

        // Effective = the displayed damage value. In normal mode it SUMS every damage bucket (direct +
        // generated-card damage + connected-forge adjustment); future buckets only need adding here.
        decimal EffectiveCombat(CardStats s) =>
            _showRawForge ? s.RawForgeCombat : (s.CombatDamage + s.GeneratedCombatDamage + (_includeConnectedForge ? s.ConnectedForgeCombat - s.ReceivedForgeCombat : 0));

        decimal EffectiveRun(ActData a) =>
            _showRawForge ? a.RawForgeTotal : (a.TotalDamage + a.GeneratedDamageTotal + (_includeConnectedForge ? a.ConnectedForgeTotal - a.ReceivedForgeTotal : 0));

        decimal EffectiveHallway(ActData a) =>
            _showRawForge ? a.RawForgeHallway : (a.DamageHallway + a.GeneratedDamageHallway + (_includeConnectedForge ? a.ConnectedForgeHallway - a.ReceivedForgeHallway : 0));

        decimal EffectiveElite(ActData a) =>
            _showRawForge ? a.RawForgeElite : (a.DamageElite + a.GeneratedDamageElite + (_includeConnectedForge ? a.ConnectedForgeElite - a.ReceivedForgeElite : 0));

        decimal EffectiveBoss(ActData a) =>
            _showRawForge ? a.RawForgeBoss : (a.DamageBoss + a.GeneratedDamageBoss + (_includeConnectedForge ? a.ConnectedForgeBoss - a.ReceivedForgeBoss : 0));

        // --- Build the generation tree (multi-level, keyed by immediate parent) ---------------------------
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
        // secondary sorts as the flat list used (run damage, then floor added).
        List<CardStats> SortNodes(IEnumerable<CardStats> nodes)
        {
            IOrderedEnumerable<CardStats> ordered = _currentSort.Column switch
            {
                "NAME" => _currentSort.Ascending
                    ? nodes.OrderBy(GetEntityDisplayTitle)
                    : nodes.OrderByDescending(GetEntityDisplayTitle),
                "PLAY_RATE" => _currentSort.Ascending
                    ? nodes.OrderBy(s => Display(s).Agg.PlayRate)
                    : nodes.OrderByDescending(s => Display(s).Agg.PlayRate),
                "COMBAT_DMG" => _currentSort.Ascending
                    ? nodes.OrderBy(s => EffectiveCombat(Display(s).Stat))
                    : nodes.OrderByDescending(s => EffectiveCombat(Display(s).Stat)),
                "RUN_DMG" => _currentSort.Ascending
                    ? nodes.OrderBy(s => EffectiveRun(Display(s).Agg))
                    : nodes.OrderByDescending(s => EffectiveRun(Display(s).Agg)),
                "AVG_DMG" => _currentSort.Ascending
                    ? nodes.OrderBy(s => Display(s).Agg.EncountersSeenTotal > 0 ? EffectiveRun(Display(s).Agg) / Display(s).Agg.EncountersSeenTotal : 0)
                    : nodes.OrderByDescending(s => Display(s).Agg.EncountersSeenTotal > 0 ? EffectiveRun(Display(s).Agg) / Display(s).Agg.EncountersSeenTotal : 0),
                "HALLWAY_DMG" => _currentSort.Ascending
                    ? nodes.OrderBy(s => EffectiveHallway(Display(s).Agg))
                    : nodes.OrderByDescending(s => EffectiveHallway(Display(s).Agg)),
                "ELITE_DMG" => _currentSort.Ascending
                    ? nodes.OrderBy(s => EffectiveElite(Display(s).Agg))
                    : nodes.OrderByDescending(s => EffectiveElite(Display(s).Agg)),
                "BOSS_DMG" => _currentSort.Ascending
                    ? nodes.OrderBy(s => EffectiveBoss(Display(s).Agg))
                    : nodes.OrderByDescending(s => EffectiveBoss(Display(s).Agg)),
                "ADDED" => _currentSort.Ascending
                    ? nodes.OrderBy(s => s.FloorAdded)
                    : nodes.OrderByDescending(s => s.FloorAdded),
                "REMOVED" => _currentSort.Ascending
                    ? nodes.OrderBy(s => s.FloorRemoved)
                    : nodes.OrderByDescending(s => s.FloorRemoved),
                "EVOLVED" => _currentSort.Ascending
                    ? nodes.OrderBy(s => s.FloorLeftDeck)
                    : nodes.OrderByDescending(s => s.FloorLeftDeck),
                _ => nodes.OrderByDescending(s => EffectiveRun(Display(s).Agg))
            };

            return ordered
                .ThenByDescending(s => EffectiveRun(Display(s).Agg))
                .ThenBy(s => s.FloorAdded)
                .ToList();
        }

        var topLevel = SortNodes(working.Where(s => !IsNested(s) && Kept(s)));

        // Builds one card row's content. namePrefix lets callers prepend an expand arrow (generators) or
        // an indent marker (nested generated children); every damage column shows the summed Effective value.
        Control BuildCardRow(CardStats stat, ActData agg, string namePrefix, string nameSuffix = "")
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
            runDataLabel.AddThemeColorOverride("font_color", _includeConnectedForge && agg.ConnectedForgeTotal > 0 ? new Color("38BDF8") : new Color("4ADE80"));

            Label combatDataLabel = new Label { Text = $"{valCombat:0.##}", CustomMinimumSize = new Vector2(150, 0) };
            combatDataLabel.AddThemeColorOverride("font_color", _includeConnectedForge && stat.ConnectedForgeCombat > 0 ? new Color("38BDF8") : new Color("4ADE80"));

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
                var generatorId = node.Id;
                rowPanel.GuiInput += (InputEvent ev) =>
                {
                    if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
                    {
                        if (!_expandedGenerators.Remove(generatorId)) _expandedGenerators.Add(generatorId);
                        RedrawUI(_latestStats);
                    }
                };
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

    // Merge Versions folds a card's own prior-version history (its pre-upgrade/enchant "ghost" rows) into
    // the surviving upgraded copy, but never merges two separate live copies. With multiple upgraded
    // copies, each ghost is distributed to a distinct surviving copy so they stay distinct rows.
    private static List<CardStats> BuildMergedCardList(List<CardStats> stats)
    {
        // BaseCardKey already encodes the owner, but include PlayerIndex explicitly so versions never merge across players.
        // GeneratedById is included so generated cards (which share a BaseCardKey) never merge across different creators.
        var groups = stats.GroupBy(s => (string.IsNullOrEmpty(s.BaseCardKey) ? s.Id : s.BaseCardKey, s.PlayerIndex, s.GeneratedById));
        var result = new List<CardStats>();

        // Maps each folded-away version id to the surviving merged row's id. Used after the loop to
        // re-point cards generated by an old creator version (e.g. pre-upgrade) at the merged creator,
        // so they nest in its dropdown instead of orphaning to top-level rows.
        var idRemap = new Dictionary<string, string>();
        void RecordMerge(List<CardStats> versions, string representativeId)
        {
            foreach (var v in versions)
            {
                if (v.Id != representativeId)
                {
                    idRemap[v.Id] = representativeId;
                }
            }
        }

        foreach (var group in groups)
        {
            var entries = group.ToList();
            var evolvedHistory = entries.Where(IsEvolvedRetired).ToList();
            var standalone = entries.Where(s => !IsEvolvedRetired(s)).ToList();

            // The whole lineage left the deck via evolution (no surviving copy) — collapse its history.
            if (standalone.Count == 0)
            {
                var mergedLineage = MergeVersions(evolvedHistory);
                RecordMerge(evolvedHistory, mergedLineage.Id);
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
                    unpaired.Add(ghost); // its upgraded form is gone — keep its own row
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
                RecordMerge(ghosts, merged.Id);
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
