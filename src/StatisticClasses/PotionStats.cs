namespace DeckTracker;

public class PotionStats : EntityStats
{
    public int FloorObtained { get; set; } = -1;
    public int FloorUsed { get; set; } = -1;
    public int FloorDiscarded { get; set; } = -1;

    // Owning player's NetId (Steam id). Persisted so potion->id resolution survives save/load and
    // works for remote players, whose live PotionModel is a network clone (reference equality fails).
    public string? OwnerNetId { get; set; }

    public override EntityStats Clone()
    {
        var clone = new PotionStats
        {
            FloorObtained = FloorObtained,
            FloorUsed = FloorUsed,
            FloorDiscarded = FloorDiscarded,
            OwnerNetId = OwnerNetId,
        };
        CopyBaseFields(clone);
        return clone;
    }
}