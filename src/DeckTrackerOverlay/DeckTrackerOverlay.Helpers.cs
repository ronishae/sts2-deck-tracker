using Godot;

namespace DeckTracker;

public static partial class DeckTrackerOverlay
{
    private static string GetEntityDisplayTitle(EntityStats stat)
    {
        string title = stat.DisplayName;

        if (stat is CardStats card)
        {
            if (!string.IsNullOrEmpty(card.Enchantment) && card.Enchantment != "None") title += $" [{card.Enchantment}]";
            if (card.UpgradeLevel > 0 && !title.Contains('+')) title += $"+{card.UpgradeLevel}";
            if (card.CopiesInDeck > 1) title += $" x{card.CopiesInDeck}";
        }

        return title;
    }

    private static ActData AggregateActData(EntityStats stat)
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

    private static Button CreateSortableHeader(string text, string sortKey, float minWidth)
    {
        string displayText = text;
        if (_currentSort.Column == sortKey)
        {
            displayText += _currentSort.Ascending ? " ▲" : " ▼";
        }

        Button btn = new Button
        {
            Text = displayText,
            CustomMinimumSize = new Vector2(minWidth, 0),
            FocusMode = Control.FocusModeEnum.None,
            Flat = true,
            Alignment = HorizontalAlignment.Left
        };

        btn.AddThemeColorOverride("font_color", new Color("FACC15"));

        btn.Pressed += () =>
        {
            if (_currentSort.Column == sortKey)
            {
                _currentSort.Ascending = !_currentSort.Ascending;
            }
            else
            {
                _currentSort.Column = sortKey;
                // Default to descending for numbers, ascending for strings (like names)
                _currentSort.Ascending = sortKey == "NAME";
            }
            RedrawUI(_latestStats);
        };

        return btn;
    }

    // Filters/colours only matter in co-op; in singleplayer everything is one (transparent) player.
    private static bool IsMultiplayer() => CardRegistry.PlayerLabels.Count > 1;

    // One base colour per player; both the row tint and the text/checkbox colour derive from it so they match.
    private static Color GetPlayerBaseColor(int playerIndex) => playerIndex switch
    {
        0 => new Color("A78BFA"),
        1 => new Color("60A5FA"),
        2 => new Color("4ADE80"),
        3 => new Color("FB923C"),
        _ => new Color("E2E8F0")
    };

    private static Color GetPlayerRowBgColor(int playerIndex)
    {
        // Singleplayer: keep rows transparent. Multiplayer: tint every player (including player 0).
        if (!IsMultiplayer())
        {
            return new Color(0, 0, 0, 0);
        }
        var c = GetPlayerBaseColor(playerIndex);
        return new Color(c.R, c.G, c.B, 0.09f);
    }

    private static Color GetPlayerTextColor(int playerIndex)
    {
        // Singleplayer falls back to the default light text; multiplayer uses the player's base colour.
        return IsMultiplayer() ? GetPlayerBaseColor(playerIndex) : new Color("E2E8F0");
    }

    private static PanelContainer CreateHoverableRow(Control content, Color? bgTint = null)
    {
        PanelContainer panel = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        panel.MouseFilter = Control.MouseFilterEnum.Pass;

        var normalBg = bgTint ?? new Color(0, 0, 0, 0);
        var hoverBg = new Color(
            Mathf.Min(1f, normalBg.R + 0.15f),
            Mathf.Min(1f, normalBg.G + 0.15f),
            Mathf.Min(1f, normalBg.B + 0.15f),
            Mathf.Max(0.08f, normalBg.A + 0.05f)
        );

        StyleBoxFlat style = new StyleBoxFlat { BgColor = normalBg };
        panel.AddThemeStyleboxOverride("panel", style);

        panel.MouseEntered += () => style.BgColor = hoverBg;
        panel.MouseExited += () => style.BgColor = normalBg;

        panel.AddChild(content);
        return panel;
    }
}
