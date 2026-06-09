using Godot;

namespace DeckTracker;

public static partial class DeckTrackerOverlay
{
    private static void BuildSmallOverlay(CanvasLayer layer)
    {
        _smallPanel = new PanelContainer { Position = new Vector2(20, 100), CustomMinimumSize = new Vector2(280, 50) };
        _smallPanel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.8f), CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4 });

        MarginContainer margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 10); margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_top", 10); margin.AddThemeConstantOverride("margin_bottom", 10);

        VBoxContainer mainCol = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        HBoxContainer header = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

        _titleLabel = new Label { Text = "Combat Damage", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _titleLabel.AddThemeColorOverride("font_color", new Color("FACC15"));

        _toggleForgeDmgBtnSmall = new Button { Text = "+Forge: OFF", FocusMode = Control.FocusModeEnum.None };
        _toggleForgeDmgBtnSmall.AddThemeFontSizeOverride("font_size", 12);
        _toggleForgeDmgBtnSmall.Pressed += ToggleForgeDamage;

        _expandBtn = new Button { Text = "[+]", FocusMode = Control.FocusModeEnum.None };
        _expandBtn.AddThemeFontSizeOverride("font_size", 12);
        _expandBtn.Pressed += OnExpandPressed;

        header.AddChild(_titleLabel);
        header.AddChild(_toggleForgeDmgBtnSmall);
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
        // Fixed 1720×900 panel centered on screen — prevents tab content width from changing the overlay size
        _fullScreenPanel = new PanelContainer { Visible = false };
        _fullScreenPanel.AnchorLeft = 0.5f; _fullScreenPanel.AnchorRight = 0.5f;
        _fullScreenPanel.AnchorTop = 0.5f; _fullScreenPanel.AnchorBottom = 0.5f;
        _fullScreenPanel.OffsetLeft = -900f; _fullScreenPanel.OffsetRight = 900f;
        _fullScreenPanel.OffsetTop = -450f; _fullScreenPanel.OffsetBottom = 450f;

        _fullScreenPanel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.05f, 0.05f, 0.08f, 0.98f), BorderColor = new Color("3A3A5C"), BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2, CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8 });

        MarginContainer margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 20); margin.AddThemeConstantOverride("margin_right", 20);
        margin.AddThemeConstantOverride("margin_top", 20); margin.AddThemeConstantOverride("margin_bottom", 20);

        VBoxContainer mainCol = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };

        // --- Header Row ---
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

        _toggleForgeDmgBtnLarge = new Button { Text = "Include Connected Forge: OFF", FocusMode = Control.FocusModeEnum.None };
        _toggleForgeDmgBtnLarge.Pressed += ToggleForgeDamage;

        _mergeVersionsBtnLarge = new Button { Text = "Merge Versions: OFF", FocusMode = Control.FocusModeEnum.None };
        _mergeVersionsBtnLarge.Pressed += ToggleMergeVersions;

        _hideZeroDamageBtnLarge = new Button { Text = "Hide 0 Damage: OFF", FocusMode = Control.FocusModeEnum.None };
        _hideZeroDamageBtnLarge.Pressed += ToggleHideZeroDamage;

        Button closeBtn = new Button { Text = "  X  ", FocusMode = Control.FocusModeEnum.None };
        closeBtn.AddThemeColorOverride("font_color", new Color("F87171"));
        closeBtn.Pressed += OnClosePressed;

        header.AddChild(title);
        header.AddChild(_act1Check);
        header.AddChild(_act2Check);
        header.AddChild(_act3Check);
        header.AddChild(_toggleRawForgeBtnLarge);
        header.AddChild(_toggleForgeDmgBtnLarge);
        header.AddChild(_mergeVersionsBtnLarge);
        header.AddChild(_hideZeroDamageBtnLarge);
        header.AddChild(closeBtn);
        mainCol.AddChild(header);

        // --- Tab Bar ---
        HBoxContainer tabsContainer = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

        _cardsTabBtn = new Button { Text = "Cards", FocusMode = Control.FocusModeEnum.None, ToggleMode = true, ButtonPressed = true };
        _relicsTabBtn = new Button { Text = "Relics", FocusMode = Control.FocusModeEnum.None, ToggleMode = true, ButtonPressed = false };
        _potionsTabBtn = new Button { Text = "Potions", FocusMode = Control.FocusModeEnum.None, ToggleMode = true, ButtonPressed = false };

        _cardsTabBtn.Pressed += () => SetActiveTab("Cards");
        _relicsTabBtn.Pressed += () => SetActiveTab("Relics");
        _potionsTabBtn.Pressed += () => SetActiveTab("Potions");

        tabsContainer.AddChild(_cardsTabBtn);
        tabsContainer.AddChild(_relicsTabBtn);
        tabsContainer.AddChild(_potionsTabBtn);
        mainCol.AddChild(tabsContainer);

        _playerFiltersContainer = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
        mainCol.AddChild(_playerFiltersContainer);

        mainCol.AddChild(new ColorRect { CustomMinimumSize = new Vector2(0, 2), Color = new Color(1, 1, 1, 0.3f) });

        // --- Headers & Rows Container ---
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
}
