namespace DeckTracker;

public abstract class EntityStats
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    
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
    }
}