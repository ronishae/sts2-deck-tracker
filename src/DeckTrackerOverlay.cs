using Godot;

namespace DeckTracker;

public static class DeckTrackerOverlay
{
    // We hold a reference to a standard Godot CanvasLayer now
    private static CanvasLayer? _instance;

    public static void EnsureCreated()
    {
        // If it already exists and is valid, do nothing
        if (GodotObject.IsInstanceValid(_instance)) return;

        if (Engine.GetMainLoop() is SceneTree tree && tree.Root != null)
        {
            GD.Print("[DeckTracker] SceneTree is ready. Creating UI using standard Godot nodes!");
            
            // 1. Instantiate a standard, built-in CanvasLayer
            _instance = new CanvasLayer
            {
                Layer = 100, // Draw on top
                Name = "DeckTrackerOverlay"
            };

            // 2. Instantiate a standard ColorRect
            ColorRect rect = new ColorRect
            {
                Color = new Color(0.8f, 0.2f, 0.6f, 0.8f), // Hot pink, slightly transparent
                CustomMinimumSize = new Vector2(250, 400),
                Position = new Vector2(50, 150)
            };

            // 3. Attach the rectangle to the layer
            _instance.AddChild(rect);

            // 4. Safely ask the engine to add our layer to the main screen
            tree.Root.CallDeferred(Node.MethodName.AddChild, _instance);
            
            GD.Print("[DeckTracker] Successfully submitted UI to the SceneTree!");
        }
    }
}