using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public abstract class EntityStats
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    
    // NOTE: for cards, the CardModel represents the master deck version, but the cards played
    // are cloned at the start of combat, so these will not represent the live played cards
    public AbstractModel? Model { get; set; }
    
    // Run Lifecycle
    public int FloorAdded { get; set; }
    public int FloorRemoved { get; set; } = -1;
    public int FloorLeftDeck { get; set; } = -1;
    public bool IsActive { get; set; } = true; // True if in deck OR currently owned relic
    
    // Act Splits
    public ActData Act1 { get; set; } = new();
    public ActData Act2 { get; set; } = new();
    public ActData Act3 { get; set; } = new();
    public ActData Act4 { get; set; } = new();
    
    // Shared Metrics
    public decimal CombatDamage { get; set; }
    public decimal RunDamage { get; set; }

    // Forge specifics
    public decimal RawForgeCombat { get; set; }
    public decimal ConnectedForgeCombat { get; set; }
    public decimal ReceivedForgeCombat { get; set; }
    
    public ActData? GetAct(int actNum)
    {
        return actNum switch
        {
            1 => Act1,
            2 => Act2,
            3 => Act3,
            4 => Act4,
            _ => null
        };
    }

    public void AddCombatDamage(decimal amount, int actNum, string combatType)
    {
        CombatDamage += amount;
        RunDamage += amount;
        GetAct(actNum)?.AddDamage(combatType, amount);
    }

    // Enforce that all subclasses must be able to clone themselves
    public abstract EntityStats Clone();

    // Helper for subclasses to copy the base fields easily
    protected void CopyBaseFields(EntityStats cloneTarget)
    {
        cloneTarget.Id = Id;
        cloneTarget.DisplayName = DisplayName;
        cloneTarget.FloorAdded = FloorAdded;
        cloneTarget.FloorRemoved = FloorRemoved;
        cloneTarget.FloorLeftDeck = FloorLeftDeck;
        cloneTarget.IsActive = IsActive;
        cloneTarget.Act1 = Act1.Clone();
        cloneTarget.Act2 = Act2.Clone();
        cloneTarget.Act3 = Act3.Clone();
        cloneTarget.Act4 = Act4.Clone();
        cloneTarget.CombatDamage = CombatDamage;
        cloneTarget.RunDamage = RunDamage;
        cloneTarget.RawForgeCombat = RawForgeCombat;
        cloneTarget.ConnectedForgeCombat = ConnectedForgeCombat;
        cloneTarget.ReceivedForgeCombat = ReceivedForgeCombat;
        cloneTarget.Model = Model;
    }
}