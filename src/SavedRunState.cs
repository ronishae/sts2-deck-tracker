namespace DeckTracker;

public sealed class SavedRunState
{
    public string RunSeed { get; set; } = "";
    public Dictionary<string, CardStats> Totals { get; set; } = new();
    // NEW: Include potions in the save file!
    public Dictionary<string, PotionStats> Potions { get; set; } = new();
    // We leave this empty dictionary here so older save files don't crash when deserializing!
    public Dictionary<string, int> TypeCounters { get; set; } = new(); 
}


[System.Text.Json.Serialization.JsonSerializable(typeof(SavedRunState))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ActData))]
internal partial class SavedStateCtx : System.Text.Json.Serialization.JsonSerializerContext { }