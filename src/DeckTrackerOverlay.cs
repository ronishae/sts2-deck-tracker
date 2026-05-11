using Godot;
using System.Collections.Concurrent;

namespace DeckTracker;

public static class DeckTrackerOverlay
{
    private static CanvasLayer? _instance;
    
    // --- Small UI Elements ---
    private static PanelContainer? _smallPanel;
    private static VBoxContainer? _smallRowsContainer;
    private static Label? _titleLabel;
    private static Button? _toggleBtn;
    private static Button? _expandBtn;
    private static Button? _toggleForgeDmgBtnSmall;
    
    // --- Full Screen UI Elements ---
    private static PanelContainer? _fullScreenPanel;
    private static VBoxContainer? _fullScreenRowsContainer;
    private static HBoxContainer? _fullScreenHeadersContainer;
    private static Button? _toggleForgeDmgBtnLarge;
    private static Button? _toggleRawForgeBtnLarge;
    private static CheckBox? _act1Check;
    private static CheckBox? _act2Check;
    private static CheckBox? _act3Check;
    
    // --- State & Data ---
    private static readonly ConcurrentQueue<List<CardStats>> UpdateQueue = new();
    private static bool _isHookedToProcess;

    private static bool _showRunStats; 
    private static bool _includeConnectedForge;
    private static bool _showRawForge;
    
    private static bool _act1Enabled = true;
    private static bool _act2Enabled = true;
    private static bool _act3Enabled = true;

    private static List<CardStats> _latestStats = [];
    
    private static bool _smallUIVisibleInternal = true;
    private static bool _hWasPressed;
    private static bool _tabWasPressed;

    public static void EnsureCreated()
    {
        if (GodotObject.IsInstanceValid(_instance)) return;

        if (Engine.GetMainLoop() is SceneTree tree && tree.Root != null)
        {
            if (!_isHookedToProcess)
            {
                tree.ProcessFrame += OnProcessFrame;
                CardRegistry.Changed += (stats) => UpdateQueue.Enqueue(stats);
                _isHookedToProcess = true;
            }

            _instance = new CanvasLayer { Layer = 100, Name = "DeckTrackerOverlay" };
            
            BuildSmallOverlay(_instance);
            BuildFullScreenOverlay(_instance);

            tree.Root.CallDeferred(Node.MethodName.AddChild, _instance);
        }
    }

    private static void BuildSmallOverlay(CanvasLayer layer)
    {
        _smallPanel = new PanelContainer { Position = new Vector2(20, 100), CustomMinimumSize = new Vector2(280, 50) };
        _smallPanel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.8f), CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4 });

        MarginContainer margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 10); margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_top", 10); margin.AddThemeConstantOverride("margin_bottom", 10);

        VBoxContainer mainCol = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        HBoxContainer header = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        
        _titleLabel = new Label { Text = "Tracker (Combat)", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _titleLabel.AddThemeColorOverride("font_color", new Color("FACC15")); 
        
        _toggleBtn = new Button { Text = "Run", FocusMode = Control.FocusModeEnum.None };
        _toggleBtn.AddThemeFontSizeOverride("font_size", 12);
        _toggleBtn.Pressed += OnTogglePressed; 

        // NEW: Toggle to inject forge damage
        _toggleForgeDmgBtnSmall = new Button { Text = "+Forge: OFF", FocusMode = Control.FocusModeEnum.None };
        _toggleForgeDmgBtnSmall.AddThemeFontSizeOverride("font_size", 12);
        _toggleForgeDmgBtnSmall.Pressed += ToggleForgeDamage; 

        _expandBtn = new Button { Text = "[+]", FocusMode = Control.FocusModeEnum.None };
        _expandBtn.AddThemeFontSizeOverride("font_size", 12);
        _expandBtn.Pressed += OnExpandPressed;

        header.AddChild(_titleLabel);
        header.AddChild(_toggleForgeDmgBtnSmall);
        header.AddChild(_toggleBtn);
        header.AddChild(_expandBtn);

        mainCol.AddChild(header);
        mainCol.AddChild(new ColorRect { CustomMinimumSize = new Vector2(0, 2), Color = new Color(1, 1, 1, 0.3f) });

        ScrollContainer scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 250), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, VerticalScrollMode = ScrollContainer.ScrollMode.Auto };

        _smallRowsContainer = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(_smallRowsContainer);
        mainCol.AddChild(scroll);

        margin.AddChild(mainCol);
        _smallPanel.AddChild(margin);
        layer.AddChild(_smallPanel);
    }

    private static void BuildFullScreenOverlay(CanvasLayer layer)
    {
        _fullScreenPanel = new PanelContainer { Visible = false };
        _fullScreenPanel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _fullScreenPanel.OffsetLeft = 100; _fullScreenPanel.OffsetTop = 100;
        _fullScreenPanel.OffsetRight = -100; _fullScreenPanel.OffsetBottom = -100;

        _fullScreenPanel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.05f, 0.05f, 0.08f, 0.98f), BorderColor = new Color("3A3A5C"), BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2, CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8 });

        MarginContainer margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 20); margin.AddThemeConstantOverride("margin_right", 20);
        margin.AddThemeConstantOverride("margin_top", 20); margin.AddThemeConstantOverride("margin_bottom", 20);

        VBoxContainer mainCol = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        HBoxContainer header = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        
        Label title = new Label { Text = "Detailed Deck Statistics", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        title.AddThemeColorOverride("font_color", new Color("FACC15"));
        title.AddThemeFontSizeOverride("font_size", 24);

        _act1Check = new CheckBox { Text = "Act 1", ButtonPressed = _act1Enabled, FocusMode = Control.FocusModeEnum.None };
        _act2Check = new CheckBox { Text = "Act 2", ButtonPressed = _act2Enabled, FocusMode = Control.FocusModeEnum.None };
        _act3Check = new CheckBox { Text = "Act 3", ButtonPressed = _act3Enabled, FocusMode = Control.FocusModeEnum.None };
        
        _act1Check.Toggled += (val) => { _act1Enabled = val; RedrawUI(_latestStats); };
        _act2Check.Toggled += (val) => { _act2Enabled = val; RedrawUI(_latestStats); };
        _act3Check.Toggled += (val) => { _act3Enabled = val; RedrawUI(_latestStats); };

        _toggleRawForgeBtnLarge = new Button { Text = "Show Raw Forge: OFF", FocusMode = Control.FocusModeEnum.None };
        _toggleRawForgeBtnLarge.Pressed += ToggleRawForge;

        // NEW: Toggle to inject forge damage
        _toggleForgeDmgBtnLarge = new Button { Text = "Include Connected Forge: OFF", FocusMode = Control.FocusModeEnum.None };
        _toggleForgeDmgBtnLarge.Pressed += ToggleForgeDamage;

        Button closeBtn = new Button { Text = "  X  ", FocusMode = Control.FocusModeEnum.None };
        closeBtn.AddThemeColorOverride("font_color", new Color("F87171"));
        closeBtn.Pressed += OnClosePressed;

        header.AddChild(title);
        header.AddChild(_act1Check);
        header.AddChild(_act2Check);
        header.AddChild(_act3Check);
        header.AddChild(_toggleRawForgeBtnLarge);
        header.AddChild(_toggleForgeDmgBtnLarge);
        header.AddChild(closeBtn);
        
        mainCol.AddChild(header);
        mainCol.AddChild(new ColorRect { CustomMinimumSize = new Vector2(0, 2), Color = new Color(1, 1, 1, 0.3f) });
        
        // Headers container is now blank, populated dynamically in RedrawUI
        _fullScreenHeadersContainer = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        mainCol.AddChild(_fullScreenHeadersContainer);
        mainCol.AddChild(new ColorRect { CustomMinimumSize = new Vector2(0, 1), Color = new Color(1, 1, 1, 0.1f) });
        
        ScrollContainer scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, VerticalScrollMode = ScrollContainer.ScrollMode.Auto };

        _fullScreenRowsContainer = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(_fullScreenRowsContainer);
        mainCol.AddChild(scroll);

        margin.AddChild(mainCol);
        _fullScreenPanel.AddChild(margin);
        layer.AddChild(_fullScreenPanel);
    }
    
    private static void ToggleRawForge()
    {
        _showRawForge = !_showRawForge;
        if (_toggleRawForgeBtnLarge != null) _toggleRawForgeBtnLarge.Text = _showRawForge ? "Show Raw Forge: ON" : "Show Raw Forge: OFF";
        RedrawUI(_latestStats);
    }
    
    private static void ToggleForgeDamage()
    {
        _includeConnectedForge = !_includeConnectedForge;
        if (_toggleForgeDmgBtnSmall != null) _toggleForgeDmgBtnSmall.Text = _includeConnectedForge ? "+Forge: ON" : "+Forge: OFF";
        if (_toggleForgeDmgBtnLarge != null) _toggleForgeDmgBtnLarge.Text = _includeConnectedForge ? "Include Connected Forge: ON" : "Include Connected Forge: OFF";
        RedrawUI(_latestStats);
    }
    
    private static void OnTogglePressed()
    {
        _showRunStats = !_showRunStats;
        if (_titleLabel != null) _titleLabel.Text = _showRunStats ? "Tracker (Run)" : "Tracker (Combat)";
        if (_toggleBtn != null) _toggleBtn.Text = _showRunStats ? "Combat" : "Run";
        RedrawUI(_latestStats);
    }

    private static void OnExpandPressed()
    {
        SetFullScreenVisible(true);
    }

    private static void OnClosePressed()
    {
        SetFullScreenVisible(false);
    }

    private static void SetFullScreenVisible(bool visible)
    {
        if (_fullScreenPanel != null)
        {
            _fullScreenPanel.Visible = visible;
            if (visible) RedrawUI(_latestStats);
            SyncVisibility();
        }
    }

    private static void SyncVisibility()
    {
        bool fullVisible = _fullScreenPanel?.Visible ?? false;
        if (GodotObject.IsInstanceValid(_smallPanel))
        {
            _smallPanel.Visible = _smallUIVisibleInternal && !fullVisible;
        }
    }

    private static void HandleInputs()
    {
        bool hPressed = Input.IsKeyPressed(Key.H);
        if (hPressed && !_hWasPressed)
        {
            _smallUIVisibleInternal = !_smallUIVisibleInternal;
            SyncVisibility();
        }
        _hWasPressed = hPressed;

        bool tabPressed = Input.IsKeyPressed(Key.Tab);
        if (tabPressed && !_tabWasPressed)
        {
            SetFullScreenVisible(!(_fullScreenPanel?.Visible ?? false));
        }
        _tabWasPressed = tabPressed;
    }

    private static void OnProcessFrame()
    {
        HandleInputs();
        if (!GodotObject.IsInstanceValid(_smallRowsContainer)) return;
        bool hasUpdate = false;
        while (UpdateQueue.TryDequeue(out var stats))
        {
            _latestStats = stats;
            hasUpdate = true;
        }
        if (hasUpdate) RedrawUI(_latestStats);
    }

    private static string GetCardDisplayTitle(CardStats stat)
    {
        string title = stat.DisplayName;
        if (!string.IsNullOrEmpty(stat.Enchantment) && stat.Enchantment != "None") title += $" [{stat.Enchantment}]";
        if (stat.CopiesInDeck > 1) title += $" x{stat.CopiesInDeck}";
        return title;
    }

    private static ActData AggregateActData(CardStats stat)
    {
        ActData result = new ActData();
        if (_act1Enabled) AddAct(result, stat.Act1);
        if (_act2Enabled) AddAct(result, stat.Act2);
        if (_act3Enabled) AddAct(result, stat.Act3);
        // Act 4 is supported in data but hidden in UI for now
        return result;
    }

    private static void AddAct(ActData target, ActData source)
    {
        target.TimesDrawn += source.TimesDrawn;
        target.TimesPlayed += source.TimesPlayed;
        target.DamageHallway += source.DamageHallway;
        target.DamageElite += source.DamageElite;
        target.DamageBoss += source.DamageBoss;
        target.RawForgeHallway += source.RawForgeHallway;
        target.RawForgeElite += source.RawForgeElite;
        target.RawForgeBoss += source.RawForgeBoss;
        target.ConnectedForgeHallway += source.ConnectedForgeHallway;
        target.ConnectedForgeElite += source.ConnectedForgeElite;
        target.ConnectedForgeBoss += source.ConnectedForgeBoss;
        target.ReceivedForgeHallway += source.ReceivedForgeHallway;
        target.ReceivedForgeElite += source.ReceivedForgeElite;
        target.ReceivedForgeBoss += source.ReceivedForgeBoss;
        target.EncountersSeenHallway += source.EncountersSeenHallway;
        target.EncountersSeenElite += source.EncountersSeenElite;
        target.EncountersSeenBoss += source.EncountersSeenBoss;
    }

    private static void RedrawUI(List<CardStats> stats)
    {
        // --- 1. Update Small UI ---
        if (GodotObject.IsInstanceValid(_smallRowsContainer))
        {
            foreach (Node child in _smallRowsContainer.GetChildren()) { _smallRowsContainer.RemoveChild(child); child.QueueFree(); }
            
            var allCards = stats
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
                Label nameLabel = new Label { Text = GetCardDisplayTitle(stat), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                
                decimal damageToShow = _showRunStats ? 
                    (agg.TotalDamage + (_includeConnectedForge ? agg.ConnectedForgeTotal - agg.ReceivedForgeTotal : 0)) : 
                    (stat.CombatDamage + (_includeConnectedForge ? stat.ConnectedForgeCombat - stat.ReceivedForgeCombat : 0));
                
                Label damageLabel = new Label { Text = damageToShow.ToString("0.##") };
                // Turn text blue if forge is making up a portion of the damage!
                Color dmgColor = (_includeConnectedForge && ((_showRunStats ? agg.ConnectedForgeTotal : stat.ConnectedForgeCombat) > 0)) ? new Color("38BDF8") : new Color("4ADE80");
                damageLabel.AddThemeColorOverride("font_color", dmgColor); 

                row.AddChild(nameLabel); row.AddChild(damageLabel); _smallRowsContainer.AddChild(row);
            }
        }

        // --- 2. Update Full Screen UI ---
        if (GodotObject.IsInstanceValid(_fullScreenPanel) && _fullScreenPanel.Visible && GodotObject.IsInstanceValid(_fullScreenRowsContainer) && GodotObject.IsInstanceValid(_fullScreenHeadersContainer))
        {
            // Clear Rows
            foreach (Node child in _fullScreenRowsContainer.GetChildren()) { _fullScreenRowsContainer.RemoveChild(child); child.QueueFree(); }
            // Clear Headers
            foreach (Node child in _fullScreenHeadersContainer.GetChildren()) { _fullScreenHeadersContainer.RemoveChild(child); child.QueueFree(); }
            
            _fullScreenHeadersContainer.AddChild(new Label { Text = "CARD NAME", CustomMinimumSize = new Vector2(300, 0) });
            _fullScreenHeadersContainer.AddChild(new Label { Text = "% PLAYED", CustomMinimumSize = new Vector2(150, 0) });
            string mainColText = _showRawForge ? "ALL FORGE (AVG) (#)" : "ALL DMG (AVG) (#)";
            _fullScreenHeadersContainer.AddChild(new Label { Text = mainColText, CustomMinimumSize = new Vector2(220, 0) });
            
            _fullScreenHeadersContainer.AddChild(new Label { Text = "HALLWAY (AVG) (#)", CustomMinimumSize = new Vector2(200, 0) });
            _fullScreenHeadersContainer.AddChild(new Label { Text = "ELITE (AVG) (#)", CustomMinimumSize = new Vector2(200, 0) });
            _fullScreenHeadersContainer.AddChild(new Label { Text = "BOSS (AVG) (#)", CustomMinimumSize = new Vector2(200, 0) });
            _fullScreenHeadersContainer.AddChild(new Label { Text = "ADDED", CustomMinimumSize = new Vector2(80, 0) });
            _fullScreenHeadersContainer.AddChild(new Label { Text = "REMOVED", CustomMinimumSize = new Vector2(90, 0) });
            _fullScreenHeadersContainer.AddChild(new Label { Text = "LEFT", CustomMinimumSize = new Vector2(80, 0) });

            var allCards = stats.Where(s => s.CardType != "Status")
                .Select(s => new { Stat = s, Agg = AggregateActData(s) })
                .OrderByDescending(x => _showRawForge ? x.Agg.RawForgeTotal : (x.Agg.TotalDamage + (_includeConnectedForge ? x.Agg.ConnectedForgeTotal - x.Agg.ReceivedForgeTotal : 0)))
                .ThenBy(x => x.Stat.FloorAdded).ToList();
            
            foreach (var item in allCards)
            {
                var stat = item.Stat;
                var agg = item.Agg;
                HBoxContainer row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                
                Label nameLabel = new Label { Text = GetCardDisplayTitle(stat), CustomMinimumSize = new Vector2(300, 0) };
                Label playRateLabel = new Label { Text = $"{agg.TimesPlayed}/{agg.TimesDrawn} ({agg.PlayRate * 100:0.#}%)", CustomMinimumSize = new Vector2(150, 0) };
                playRateLabel.AddThemeColorOverride("font_color", new Color("A0A8B4"));
                
                decimal valTotal = _showRawForge ? agg.RawForgeTotal : (agg.TotalDamage + (_includeConnectedForge ? agg.ConnectedForgeTotal - agg.ReceivedForgeTotal : 0));
                decimal avgTotal = agg.EncountersSeenTotal > 0 ? valTotal / agg.EncountersSeenTotal : 0;
                
                decimal valHallway = _showRawForge ? agg.RawForgeHallway : (agg.DamageHallway + (_includeConnectedForge ? agg.ConnectedForgeHallway - agg.ReceivedForgeHallway : 0));
                decimal avgHallway = agg.EncountersSeenHallway > 0 ? valHallway / agg.EncountersSeenHallway : 0;

                decimal valElite = _showRawForge ? agg.RawForgeElite : (agg.DamageElite + (_includeConnectedForge ? agg.ConnectedForgeElite - agg.ReceivedForgeElite : 0));
                decimal avgElite = agg.EncountersSeenElite > 0 ? valElite / agg.EncountersSeenElite : 0;

                decimal valBoss = _showRawForge ? agg.RawForgeBoss : (agg.DamageBoss + (_includeConnectedForge ? agg.ConnectedForgeBoss - agg.ReceivedForgeBoss : 0));
                decimal avgBoss = agg.EncountersSeenBoss > 0 ? valBoss / agg.EncountersSeenBoss : 0;
                
                Color statColor = new Color("A0A8B4");
                
                Label allDataLabel = new Label { Text = $"{valTotal:0.##} ({avgTotal:0.#}) (#{agg.EncountersSeenTotal})", CustomMinimumSize = new Vector2(220, 0) };
                allDataLabel.AddThemeColorOverride("font_color", statColor);

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

                string leftText = stat.FloorLeftDeck <= 0 ? "N/A" : stat.FloorLeftDeck.ToString();
                Label leftLabel = new Label { Text = leftText, CustomMinimumSize = new Vector2(80, 0) };
                leftLabel.AddThemeColorOverride("font_color", new Color("A0A8B4"));
                
                row.AddChild(nameLabel); row.AddChild(playRateLabel);
                row.AddChild(allDataLabel); row.AddChild(hallwayLabel); row.AddChild(eliteLabel); row.AddChild(bossLabel);
                row.AddChild(addedLabel); row.AddChild(removedLabel); row.AddChild(leftLabel);
                _fullScreenRowsContainer.AddChild(row);
            }
            
        }
    }
}