using Godot;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;

namespace DeckTracker;

public static class DeckTrackerOverlay
{
    private static CanvasLayer? _instance;
    private static VBoxContainer? _rowsContainer;
    private static Label? _titleLabel;
    private static Button? _toggleBtn;
    
    private static readonly ConcurrentQueue<List<CardStats>> _updateQueue = new();
    private static bool _isHookedToProcess = false;

    // View State Tracking
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
                DeckDamageService.Changed += (stats) => _updateQueue.Enqueue(stats);
                _isHookedToProcess = true;
            }

            _instance = new CanvasLayer { Layer = 100, Name = "DeckTrackerOverlay" };
            
            PanelContainer bg = new PanelContainer { Position = new Vector2(20, 100), CustomMinimumSize = new Vector2(220, 50) };
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

            _rowsContainer = new VBoxContainer();
            
            // --- Header Row ---
            HBoxContainer header = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            
            _titleLabel = new Label { Text = "Tracker (Combat)", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _titleLabel.AddThemeColorOverride("font_color", new Color("FACC15")); 
            
            _toggleBtn = new Button { Text = "View Run", FocusMode = Control.FocusModeEnum.None };
            _toggleBtn.AddThemeFontSizeOverride("font_size", 12);
            _toggleBtn.Pressed += OnTogglePressed; // Hook up the click event

            header.AddChild(_titleLabel);
            header.AddChild(_toggleBtn);
            // ------------------

            _rowsContainer.AddChild(header);
            _rowsContainer.AddChild(new ColorRect { CustomMinimumSize = new Vector2(0, 2), Color = new Color(1, 1, 1, 0.3f) });

            margin.AddChild(_rowsContainer);
            bg.AddChild(margin);
            _instance.AddChild(bg);

            tree.Root.CallDeferred(Node.MethodName.AddChild, _instance);
        }
    }

    private static void OnTogglePressed()
    {
        _showRunStats = !_showRunStats;
        
        // Update the text on the UI
        if (_titleLabel != null) _titleLabel.Text = _showRunStats ? "Tracker (Run)" : "Tracker (Combat)";
        if (_toggleBtn != null) _toggleBtn.Text = _showRunStats ? "View Combat" : "View Run";

        // Instantly redraw the UI using the latest data bucket we have
        RedrawUI(_latestStats);
    }

    private static void OnProcessFrame()
    {
        if (!GodotObject.IsInstanceValid(_rowsContainer)) return;

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
        if (_rowsContainer == null) return;

        // Clean up old rows
        int childCount = _rowsContainer.GetChildCount();
        for (int i = childCount - 1; i >= 2; i--)
        {
            Node child = _rowsContainer.GetChild(i);
            _rowsContainer.RemoveChild(child);
            child.QueueFree();
        }

        // Sort and Filter the stats based on what mode the user is currently viewing
        var sortedStats = _showRunStats 
            ? stats.Where(s => s.RunDamage > 0).OrderByDescending(s => s.RunDamage).ToList()
            : stats.Where(s => s.CombatDamage > 0).OrderByDescending(s => s.CombatDamage).ToList();

        foreach (var stat in sortedStats)
        {
            HBoxContainer row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            
            Label nameLabel = new Label { Text = stat.DisplayName, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            
            // Pick the right damage number to show
            decimal damageToShow = _showRunStats ? stat.RunDamage : stat.CombatDamage;
            
            Label damageLabel = new Label { Text = damageToShow.ToString("0.##") };
            damageLabel.AddThemeColorOverride("font_color", new Color("4ADE80")); 

            row.AddChild(nameLabel);
            row.AddChild(damageLabel);
            _rowsContainer.AddChild(row);
        }
    }
}