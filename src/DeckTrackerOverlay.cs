using Godot;
using System.Collections.Generic;
using System.Collections.Concurrent;

namespace DeckTracker;

public static class DeckTrackerOverlay
{
    private static CanvasLayer? _instance;
    private static VBoxContainer? _rowsContainer;
    
    // Thread-safe bucket to pass data from the background combat thread to the main UI thread
    private static readonly ConcurrentQueue<List<CardStats>> _updateQueue = new();
    private static bool _isHookedToProcess = false;

    public static void EnsureCreated()
    {
        if (GodotObject.IsInstanceValid(_instance)) return;

        if (Engine.GetMainLoop() is SceneTree tree && tree.Root != null)
        {
            // 1. Hook into the main thread process loop safely
            if (!_isHookedToProcess)
            {
                tree.ProcessFrame += OnProcessFrame;
                DeckDamageService.Changed += (stats) => _updateQueue.Enqueue(stats);
                _isHookedToProcess = true;
            }

            // 2. Build the UI Tree using only standard Godot nodes
            _instance = new CanvasLayer { Layer = 100, Name = "DeckTrackerOverlay" };
            
            PanelContainer bg = new PanelContainer { Position = new Vector2(20, 100), CustomMinimumSize = new Vector2(200, 50) };
            bg.AddThemeStyleboxOverride("panel", new StyleBoxFlat 
            { 
                BgColor = new Color(0, 0, 0, 0.8f), // Dark semi-transparent background
                CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4,
                CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4
            });

            MarginContainer margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left", 10);
            margin.AddThemeConstantOverride("margin_right", 10);
            margin.AddThemeConstantOverride("margin_top", 10);
            margin.AddThemeConstantOverride("margin_bottom", 10);

            _rowsContainer = new VBoxContainer();
            
            // Header
            Label title = new Label { Text = "Deck Tracker" };
            title.AddThemeColorOverride("font_color", new Color("FACC15")); // Yellow title
            
            _rowsContainer.AddChild(title);
            _rowsContainer.AddChild(new ColorRect { CustomMinimumSize = new Vector2(0, 2), Color = new Color(1, 1, 1, 0.3f) });

            margin.AddChild(_rowsContainer);
            bg.AddChild(margin);
            _instance.AddChild(bg);

            // 3. Attach to screen
            tree.Root.CallDeferred(Node.MethodName.AddChild, _instance);
        }
    }

    // This runs on Godot's Main Thread safely every frame
    private static void OnProcessFrame()
    {
        if (!GodotObject.IsInstanceValid(_rowsContainer)) return;

        bool hasUpdate = false;
        List<CardStats>? latestStats = null;

        // Drain the queue, keeping only the absolute newest data
        while (_updateQueue.TryDequeue(out var stats))
        {
            latestStats = stats;
            hasUpdate = true;
        }

        if (hasUpdate && latestStats != null)
        {
            RedrawUI(latestStats);
        }
    }

    private static void RedrawUI(List<CardStats> stats)
    {
        if (_rowsContainer == null) return;

        // Clear old rows (skipping the Title [index 0] and Separator [index 1])
        int childCount = _rowsContainer.GetChildCount();
        for (int i = childCount - 1; i >= 2; i--)
        {
            Node child = _rowsContainer.GetChild(i);
            _rowsContainer.RemoveChild(child);
            child.QueueFree();
        }

        // Add the new updated rows
        foreach (var stat in stats)
        {
            HBoxContainer row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            
            Label nameLabel = new Label { Text = stat.DisplayName, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            Label damageLabel = new Label { Text = stat.TotalDamage.ToString("0.##") };
            damageLabel.AddThemeColorOverride("font_color", new Color("4ADE80")); // Green damage text

            row.AddChild(nameLabel);
            row.AddChild(damageLabel);
            _rowsContainer.AddChild(row);
        }
    }
}