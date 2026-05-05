using Godot;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;

namespace DeckTracker;

public static class DeckTrackerOverlay
{
    private static CanvasLayer? _instance;
    
    // --- Small UI Elements ---
    private static VBoxContainer? _smallRowsContainer;
    private static Label? _titleLabel;
    private static Button? _toggleBtn;
    private static Button? _expandBtn;
    private static Button? _toggleForgeDmgBtnSmall; // NEW
    
    // --- Full Screen UI Elements ---
    private static PanelContainer? _fullScreenPanel;
    private static VBoxContainer? _fullScreenRowsContainer;
    private static HBoxContainer? _fullScreenHeadersContainer; // NEW
    private static Button? _viewModeBtn; // NEW
    private static Button? _toggleForgeDmgBtnLarge; // NEW
    
    // --- State & Data ---
    private static readonly ConcurrentQueue<List<CardStats>> _updateQueue = new();
    private static bool _isHookedToProcess = false;

    private static bool _showRunStats = false; 
    private static bool _includeConnectedForge = false; // NEW: Controls the effective damage calculation
    private static List<CardStats> _latestStats = new();

    public static void EnsureCreated()
    {
        if (GodotObject.IsInstanceValid(_instance)) return;

        if (Engine.GetMainLoop() is SceneTree tree && tree.Root != null)
        {
            if (!_isHookedToProcess)
            {
                tree.ProcessFrame += OnProcessFrame;
                CardRegistry.Changed += (stats) => _updateQueue.Enqueue(stats);
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
        PanelContainer bg = new PanelContainer { Position = new Vector2(20, 100), CustomMinimumSize = new Vector2(280, 50) };
        bg.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.8f), CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4 });

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
        bg.AddChild(margin);
        layer.AddChild(bg);
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

        // NEW: View Switcher
        _viewModeBtn = new Button { Text = "View Forge Stats", FocusMode = Control.FocusModeEnum.None };
        _viewModeBtn.AddThemeColorOverride("font_color", new Color("38BDF8"));

        // NEW: Toggle to inject forge damage
        _toggleForgeDmgBtnLarge = new Button { Text = "Include Connected Forge: OFF", FocusMode = Control.FocusModeEnum.None };
        _toggleForgeDmgBtnLarge.Pressed += ToggleForgeDamage;

        Button closeBtn = new Button { Text = "  X  ", FocusMode = Control.FocusModeEnum.None };
        closeBtn.AddThemeColorOverride("font_color", new Color("F87171"));
        closeBtn.Pressed += OnClosePressed;

        header.AddChild(title);
        header.AddChild(_toggleForgeDmgBtnLarge);
        header.AddChild(_viewModeBtn);
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
        if (_fullScreenPanel != null) _fullScreenPanel.Visible = true;
        RedrawUI(_latestStats); 
    }

    private static void OnClosePressed() { if (_fullScreenPanel != null) _fullScreenPanel.Visible = false; }

    private static void OnProcessFrame()
    {
        if (!GodotObject.IsInstanceValid(_smallRowsContainer)) return;
        bool hasUpdate = false;
        while (_updateQueue.TryDequeue(out var stats))
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

    private static void RedrawUI(List<CardStats> stats)
    {
        // --- 1. Update Small UI ---
        if (GodotObject.IsInstanceValid(_smallRowsContainer))
        {
            foreach (Node child in _smallRowsContainer.GetChildren()) { _smallRowsContainer.RemoveChild(child); child.QueueFree(); }
            
            var allCards = stats
                .Where(s => s.CardType != "Status") 
                .Where(s => {
                    decimal effCombat = s.CombatDamage + (_includeConnectedForge ? s.ConnectedForgeCombat - s.ReceivedForgeCombat : 0);
                    decimal effRun = s.RunDamage + (_includeConnectedForge ? s.ConnectedForgeTotal : 0);
                    return _showRunStats ? effRun > 0 : effCombat > 0;
                })
                .OrderByDescending(s => _showRunStats ? (s.RunDamage + (_includeConnectedForge ? s.ConnectedForgeTotal : 0)) : (s.CombatDamage + (_includeConnectedForge ? s.ConnectedForgeCombat : 0)))
                .ThenBy(s => s.FloorAdded)
                .ToList();
            
            foreach (var stat in allCards)
            {
                HBoxContainer row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                Label nameLabel = new Label { Text = GetCardDisplayTitle(stat), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                
                decimal damageToShow = _showRunStats ? 
                    (stat.RunDamage + (_includeConnectedForge ? stat.ConnectedForgeTotal - stat.ReceivedForgeTotal : 0)) : 
                    (stat.CombatDamage + (_includeConnectedForge ? stat.ConnectedForgeCombat - stat.ReceivedForgeCombat : 0));
                
                Label damageLabel = new Label { Text = damageToShow.ToString("0.##") };
                // Turn text blue if forge is making up a portion of the damage!
                Color dmgColor = (_includeConnectedForge && ((_showRunStats ? stat.ConnectedForgeTotal : stat.ConnectedForgeCombat) > 0)) ? new Color("38BDF8") : new Color("4ADE80");
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
            _fullScreenHeadersContainer.AddChild(new Label { Text = "ALL DMG (AVG) (#)", CustomMinimumSize = new Vector2(220, 0) });
            _fullScreenHeadersContainer.AddChild(new Label { Text = "HALLWAY (AVG) (#)", CustomMinimumSize = new Vector2(200, 0) });
            _fullScreenHeadersContainer.AddChild(new Label { Text = "ELITE (AVG) (#)", CustomMinimumSize = new Vector2(200, 0) });
            _fullScreenHeadersContainer.AddChild(new Label { Text = "BOSS (AVG) (#)", CustomMinimumSize = new Vector2(200, 0) });
            _fullScreenHeadersContainer.AddChild(new Label { Text = "ADDED", CustomMinimumSize = new Vector2(80, 0) });
            _fullScreenHeadersContainer.AddChild(new Label { Text = "REMOVED", CustomMinimumSize = new Vector2(90, 0) });
            _fullScreenHeadersContainer.AddChild(new Label { Text = "LEFT", CustomMinimumSize = new Vector2(80, 0) });

            var allCards = stats.Where(s => s.CardType != "Status")
                .OrderByDescending(s => s.RunDamage + (_includeConnectedForge ? s.ConnectedForgeTotal - s.ReceivedForgeTotal : 0))
                .ThenBy(s => s.FloorAdded).ToList();

            foreach (var stat in allCards)
            {
                HBoxContainer row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                
                Label nameLabel = new Label { Text = GetCardDisplayTitle(stat), CustomMinimumSize = new Vector2(300, 0) };
                Label playRateLabel = new Label { Text = $"{stat.TimesPlayed}/{stat.TimesDrawn} ({stat.PlayRate * 100:0.#}%)", CustomMinimumSize = new Vector2(150, 0) };
                playRateLabel.AddThemeColorOverride("font_color", new Color("A0A8B4"));
                
                // Effective Stats Calculations
                decimal effRun = stat.RunDamage + (_includeConnectedForge ? stat.ConnectedForgeTotal - stat.ReceivedForgeTotal : 0);
                decimal effAvgTot = stat.EncountersSeenTotal > 0 ? effRun / stat.EncountersSeenTotal : 0;
                
                decimal effHallway = stat.DamageHallway + (_includeConnectedForge ? stat.ConnectedForgeHallway - stat.ReceivedForgeHallway : 0);
                decimal effAvgHallway = stat.EncountersSeenHallway > 0 ? effHallway / stat.EncountersSeenHallway : 0;

                decimal effElite = stat.DamageElite + (_includeConnectedForge ? stat.ConnectedForgeElite - stat.ReceivedForgeElite : 0);
                decimal effAvgElite = stat.EncountersSeenElite > 0 ? effElite / stat.EncountersSeenElite : 0;

                decimal effBoss = stat.DamageBoss + (_includeConnectedForge ? stat.ConnectedForgeBoss - stat.ReceivedForgeBoss : 0);
                decimal effAvgBoss = stat.EncountersSeenBoss > 0 ? effBoss / stat.EncountersSeenBoss : 0;
                
                Color dataColor = (_includeConnectedForge && stat.ConnectedForgeTotal > 0) ? new Color("38BDF8") : new Color("4ADE80");

                Label allDmgLabel = new Label { Text = $"{effRun:0.##} ({effAvgTot:0.#}) (#{stat.EncountersSeenTotal})", CustomMinimumSize = new Vector2(220, 0) };
                allDmgLabel.AddThemeColorOverride("font_color", dataColor);

                Label hallwayLabel = new Label { Text = $"{effHallway:0.##} ({effAvgHallway:0.#}) (#{stat.EncountersSeenHallway})", CustomMinimumSize = new Vector2(200, 0) };
                hallwayLabel.AddThemeColorOverride("font_color", new Color("A0A8B4"));

                Label eliteLabel = new Label { Text = $"{effElite:0.##} ({effAvgElite:0.#}) (#{stat.EncountersSeenElite})", CustomMinimumSize = new Vector2(200, 0) };
                eliteLabel.AddThemeColorOverride("font_color", new Color("FACC15")); 

                Label bossLabel = new Label { Text = $"{effBoss:0.##} ({effAvgBoss:0.#}) (#{stat.EncountersSeenBoss})", CustomMinimumSize = new Vector2(200, 0) };
                bossLabel.AddThemeColorOverride("font_color", new Color("F87171"));
                
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
                row.AddChild(allDmgLabel); row.AddChild(hallwayLabel); row.AddChild(eliteLabel); row.AddChild(bossLabel);
                row.AddChild(addedLabel); row.AddChild(removedLabel); row.AddChild(leftLabel);
                _fullScreenRowsContainer.AddChild(row);
            }
            
        }
    }
}