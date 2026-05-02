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
    
    // --- Full Screen UI Elements ---
    private static PanelContainer? _fullScreenPanel;
    private static VBoxContainer? _fullScreenRowsContainer;
    
    // --- Thread-Safe Data ---
    private static readonly ConcurrentQueue<List<CardStats>> _updateQueue = new();
    private static bool _isHookedToProcess = false;

    private static bool _showRunStats = false; 
    private static List<CardStats> _latestStats = new();

    public static void EnsureCreated()
    {
        if (GodotObject.IsInstanceValid(_instance)) return;

        if (Engine.GetMainLoop() is SceneTree tree && tree.Root != null)
        {
            if (!_isHookedToProcess)
            {
                tree.ProcessFrame += OnProcessFrame;
                // CHANGED THIS LINE TO LISTEN TO THE REGISTRY:
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
        PanelContainer bg = new PanelContainer { Position = new Vector2(20, 100), CustomMinimumSize = new Vector2(240, 50) };
        bg.AddThemeStyleboxOverride("panel", new StyleBoxFlat 
        { 
            BgColor = new Color(0, 0, 0, 0.8f),
            CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4
        });

        MarginContainer margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 10);
        margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_top", 10);
        margin.AddThemeConstantOverride("margin_bottom", 10);

        VBoxContainer mainCol = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        
        HBoxContainer header = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        
        _titleLabel = new Label { Text = "Tracker (Combat)", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _titleLabel.AddThemeColorOverride("font_color", new Color("FACC15")); 
        
        _toggleBtn = new Button { Text = "View Run", FocusMode = Control.FocusModeEnum.None };
        _toggleBtn.AddThemeFontSizeOverride("font_size", 12);
        _toggleBtn.Pressed += OnTogglePressed; 

        _expandBtn = new Button { Text = "[+]", FocusMode = Control.FocusModeEnum.None };
        _expandBtn.AddThemeFontSizeOverride("font_size", 12);
        _expandBtn.Pressed += OnExpandPressed;

        header.AddChild(_titleLabel);
        header.AddChild(_toggleBtn);
        header.AddChild(_expandBtn);

        mainCol.AddChild(header);
        mainCol.AddChild(new ColorRect { CustomMinimumSize = new Vector2(0, 2), Color = new Color(1, 1, 1, 0.3f) });

        ScrollContainer scroll = new ScrollContainer 
        { 
            CustomMinimumSize = new Vector2(0, 250), 
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto
        };

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
        _fullScreenPanel.OffsetLeft = 100;
        _fullScreenPanel.OffsetTop = 100;
        _fullScreenPanel.OffsetRight = -100;
        _fullScreenPanel.OffsetBottom = -100;

        _fullScreenPanel.AddThemeStyleboxOverride("panel", new StyleBoxFlat 
        { 
            BgColor = new Color(0.05f, 0.05f, 0.08f, 0.98f), 
            BorderColor = new Color("3A3A5C"),
            BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8
        });

        MarginContainer margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 20);
        margin.AddThemeConstantOverride("margin_right", 20);
        margin.AddThemeConstantOverride("margin_top", 20);
        margin.AddThemeConstantOverride("margin_bottom", 20);

        VBoxContainer mainCol = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };

        HBoxContainer header = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        
        Label title = new Label { Text = "Detailed Deck Statistics (Current Run)", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        title.AddThemeColorOverride("font_color", new Color("FACC15"));
        title.AddThemeFontSizeOverride("font_size", 24);

        Button closeBtn = new Button { Text = "  X  ", FocusMode = Control.FocusModeEnum.None };
        closeBtn.AddThemeColorOverride("font_color", new Color("F87171"));
        closeBtn.Pressed += OnClosePressed;

        header.AddChild(title);
        header.AddChild(closeBtn);
        mainCol.AddChild(header);
        mainCol.AddChild(new ColorRect { CustomMinimumSize = new Vector2(0, 2), Color = new Color(1, 1, 1, 0.3f) });

        HBoxContainer tableHeaders = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        
        Label colCard = new Label { Text = "CARD NAME", CustomMinimumSize = new Vector2(400, 0) };
        colCard.AddThemeColorOverride("font_color", new Color("687480"));

        Label colFloor = new Label { Text = "FLOOR ADDED", CustomMinimumSize = new Vector2(150, 0) };
        colFloor.AddThemeColorOverride("font_color", new Color("687480"));
        
        Label colRunDmg = new Label { Text = "TOTAL RUN DAMAGE", CustomMinimumSize = new Vector2(200, 0) };
        colRunDmg.AddThemeColorOverride("font_color", new Color("687480"));

        tableHeaders.AddChild(colCard);
        tableHeaders.AddChild(colFloor);
        tableHeaders.AddChild(colRunDmg);
        
        mainCol.AddChild(tableHeaders);
        mainCol.AddChild(new ColorRect { CustomMinimumSize = new Vector2(0, 1), Color = new Color(1, 1, 1, 0.1f) });

        ScrollContainer scroll = new ScrollContainer 
        { 
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto
        };

        _fullScreenRowsContainer = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(_fullScreenRowsContainer);
        mainCol.AddChild(scroll);

        margin.AddChild(mainCol);
        _fullScreenPanel.AddChild(margin);
        layer.AddChild(_fullScreenPanel);
    }

    private static void OnTogglePressed()
    {
        _showRunStats = !_showRunStats;
        if (_titleLabel != null) _titleLabel.Text = _showRunStats ? "Tracker (Run)" : "Tracker (Combat)";
        if (_toggleBtn != null) _toggleBtn.Text = _showRunStats ? "View Combat" : "View Run";
        RedrawUI(_latestStats);
    }

    private static void OnExpandPressed()
    {
        if (_fullScreenPanel != null) _fullScreenPanel.Visible = true;
        RedrawUI(_latestStats); 
    }

    private static void OnClosePressed()
    {
        if (_fullScreenPanel != null) _fullScreenPanel.Visible = false;
    }

    private static void OnProcessFrame()
    {
        if (!GodotObject.IsInstanceValid(_smallRowsContainer)) return;

        bool hasUpdate = false;
        while (_updateQueue.TryDequeue(out var stats))
        {
            _latestStats = stats;
            hasUpdate = true;
        }

        if (hasUpdate)
        {
            RedrawUI(_latestStats);
        }
    }

    private static void RedrawUI(List<CardStats> stats)
    {
        // 1. Update Small UI (Filters out 0 damage cards)
        if (GodotObject.IsInstanceValid(_smallRowsContainer))
        {
            foreach (Node child in _smallRowsContainer.GetChildren())
            {
                _smallRowsContainer.RemoveChild(child);
                child.QueueFree();
            }
            
            var allCards = stats
                .Where(s => s.CardType != "Status") 
                .OrderByDescending(s => s.RunDamage)
                .ThenBy(s => s.FloorAdded)
                .ToList();
            
            foreach (var stat in allCards)
            {
                HBoxContainer row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                Label nameLabel = new Label { Text = stat.DisplayName, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                decimal damageToShow = _showRunStats ? stat.RunDamage : stat.CombatDamage;
                Label damageLabel = new Label { Text = damageToShow.ToString("0.##") };
                damageLabel.AddThemeColorOverride("font_color", new Color("4ADE80")); 

                row.AddChild(nameLabel);
                row.AddChild(damageLabel);
                _smallRowsContainer.AddChild(row);
            }
        }

        // 2. Update Full Screen UI (No filters! Shows the entire deck)
        if (GodotObject.IsInstanceValid(_fullScreenPanel) && _fullScreenPanel.Visible && GodotObject.IsInstanceValid(_fullScreenRowsContainer))
        {
            foreach (Node child in _fullScreenRowsContainer.GetChildren())
            {
                _fullScreenRowsContainer.RemoveChild(child);
                child.QueueFree();
            }

            // Notice we removed the `.Where` filter! It just sorts by Run Damage now.
            var allCards = stats.OrderByDescending(s => s.RunDamage).ThenBy(s => s.FloorAdded).ToList();

            foreach (var stat in allCards)
            {
                HBoxContainer row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                
                Label nameLabel = new Label { Text = stat.DisplayName, CustomMinimumSize = new Vector2(400, 0) };
                Label floorLabel = new Label { Text = stat.FloorAdded.ToString(), CustomMinimumSize = new Vector2(150, 0) };
                floorLabel.AddThemeColorOverride("font_color", new Color("A0A8B4")); 
                Label damageLabel = new Label { Text = stat.RunDamage.ToString("0.##"), CustomMinimumSize = new Vector2(200, 0) };
                damageLabel.AddThemeColorOverride("font_color", new Color("4ADE80"));

                row.AddChild(nameLabel);
                row.AddChild(floorLabel);
                row.AddChild(damageLabel);
                _fullScreenRowsContainer.AddChild(row);
            }
        }
    }
}