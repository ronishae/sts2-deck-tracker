namespace DeckTracker;

public class PotionStats : EntityStats
{
    public int FloorObtained { get; set; } = -1;
    public int FloorUsed { get; set; } = -1;
    public int FloorDiscarded { get; set; } = -1;
    public override EntityStats Clone()
    {
        var clone = new PotionStats
        {
            FloorObtained = FloorObtained,
            FloorUsed = FloorUsed,
            FloorDiscarded = FloorDiscarded,
        };
        CopyBaseFields(clone);
        return clone;
    }
}