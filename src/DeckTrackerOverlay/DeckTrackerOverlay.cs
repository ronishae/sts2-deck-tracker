using Godot;

namespace DeckTracker;

public static partial class DeckTrackerOverlay
{
    private static CanvasLayer? _instance;

    // --- Small UI Elements ---
    private static PanelContainer? _smallPanel;
    private static VBoxContainer? _smallRowsContainer;
    private static Label? _titleLabel;
    private static Button? _expandBtn;
    private static Button? _toggleForgeDmgBtnSmall;

    // Small-panel placement/drag state. Initial right-side placement runs once so a later content resize or a
    // user drag is never overridden.
    private static bool _smallPanelPositioned;
    private static bool _smallPanelDragging;
    private static Vector2 _smallPanelDragOffset;

    // --- Full Screen UI Elements ---
    private static PanelContainer? _fullScreenPanel;
    private static VBoxContainer? _fullScreenRowsContainer;
    private static HBoxContainer? _fullScreenHeadersContainer;
    private static Button? _toggleForgeDmgBtnLarge;
    private static Button? _toggleRawForgeBtnLarge;
    private static Button? _mergeVersionsBtnLarge;
    private static Button? _hideZeroDamageBtnLarge;
    private static Button? _thisCombatOnlyBtnLarge;
    private static CheckBox? _act1Check;
    private static CheckBox? _act2Check;
    private static CheckBox? _act3Check;

    // --- Tab System ---
    private static Button? _cardsTabBtn;
    private static Button? _relicsTabBtn;
    private static Button? _potionsTabBtn;
    private static string _activeTab = "Cards";

    // --- Player Filter State ---
    private static HBoxContainer? _playerFiltersContainer;
    // All player indices enabled by default; the filter only matters when PlayerLabels has >1 entry
    private static readonly HashSet<int> _enabledPlayers = new() { 0, 1, 2, 3 };

    // --- State & Data ---
    private static bool _isHookedToProcess;

    private static bool _includeConnectedForge = true;
    private static bool _showRawForge;
    private static bool _mergeCardVersions = true;
    private static bool _hideZeroDamageCards = true;
    // When on, the card list shows only cards active in the current combat; stacked rows count only the
    // copies made this combat (e.g. only this combat's Shivs), keeping the overlay uncluttered.
    private static bool _thisCombatOnly;
    // Tracking ids of generator rows the user has expanded to reveal their generated cards. Generators
    // are collapsed by default; this persists expand state across redraws within a session.
    private static readonly HashSet<string> _expandedGenerators = new();

    private static bool _act1Enabled = true;
    private static bool _act2Enabled = true;
    private static bool _act3Enabled = true;

    private static List<CardStats> _latestStats = [];

    // Throttle for the out-of-combat deck-change poll (frames between checks).
    private const int DeckPollIntervalFrames = 10;
    private static int _deckPollCounter;

    private static bool _smallUIVisibleInternal = true;
    private static bool _hWasPressed;
    private static bool _tabWasPressed;
    private static bool _logLevelToggleWasPressed;

    // --- Sort State ---
    private class SortState
    {
        public string Column { get; set; } = "RUN_DMG";
        public bool Ascending { get; set; } = false;
    }
    private static SortState _currentSort = new SortState();

    public static void EnsureCreated()
    {
        if (GodotObject.IsInstanceValid(_instance)) return;

        if (Engine.GetMainLoop() is SceneTree tree && tree.Root != null)
        {
            if (!_isHookedToProcess)
            {
                tree.ProcessFrame += OnProcessFrame;
                tree.Root.TreeExiting += OnGameExiting;
                _isHookedToProcess = true;
            }

            _instance = new CanvasLayer { Layer = 100, Name = "DeckTrackerOverlay" };

            BuildSmallOverlay(_instance);
            BuildFullScreenOverlay(_instance);

            tree.Root.CallDeferred(Node.MethodName.AddChild, _instance);
        }
    }

    private static void OnGameExiting()
    {
        if (GodotObject.IsInstanceValid(_instance))
        {
            Log.Info("OnGameExiting. Freeing overlay nodes.");
            _instance.Free();
            _instance = null;
        }
    }

    private static void OnProcessFrame()
    {
        HandleInputs();
        if (!GodotObject.IsInstanceValid(_smallRowsContainer)) return;

        // Detect out-of-combat deck edits (add/remove/upgrade/enchant) and resync so they show instantly.
        // PollDeckChange publishes when the deck changed; the dequeue loop below redraws this same frame.
        if (++_deckPollCounter >= DeckPollIntervalFrames)
        {
            _deckPollCounter = 0;
            HookPatches.PollDeckChange();
        }

        // Pull a fresh snapshot at most once per frame; the registry clones only when state changed since
        // the last pull, so per-event mutations no longer clone the whole ledger.
        var snapshot = CardRegistry.DrainPendingSnapshot();
        if (snapshot != null)
        {
            _latestStats = snapshot;
            RedrawUI(_latestStats);
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

        // Toggle full VeryDebug logging on demand for bug reports; ships quiet at Info (see Log.Level).
        bool logTogglePressed = Input.IsKeyPressed(Key.J);
        if (logTogglePressed && !_logLevelToggleWasPressed)
        {
            Log.Level = Log.Level == LogLevel.VeryDebug ? LogLevel.Info : LogLevel.VeryDebug;
            Log.Info($"HandleInputs. Log level toggled. Level: {Log.Level}");
        }
        _logLevelToggleWasPressed = logTogglePressed;
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

    private static void RedrawUI(List<CardStats> stats)
    {
        UpdateSmallUI(stats);
        UpdateFullScreenUI(stats);
    }

    private static void UpdateFullScreenUI(List<CardStats> stats)
    {
        if (!GodotObject.IsInstanceValid(_fullScreenPanel) || !_fullScreenPanel.Visible || !GodotObject.IsInstanceValid(_fullScreenRowsContainer) || !GodotObject.IsInstanceValid(_fullScreenHeadersContainer)) return;

        foreach (Node child in _fullScreenRowsContainer.GetChildren()) { _fullScreenRowsContainer.RemoveChild(child); child.QueueFree(); }
        foreach (Node child in _fullScreenHeadersContainer.GetChildren()) { _fullScreenHeadersContainer.RemoveChild(child); child.QueueFree(); }

        RebuildPlayerFilters();

        switch (_activeTab)
        {
            case "Cards":
                RenderFullScreenCards(stats);
                break;
            case "Relics":
                RenderFullScreenRelics();
                break;
            case "Potions":
                RenderFullScreenPotions();
                break;
        }
    }
}
