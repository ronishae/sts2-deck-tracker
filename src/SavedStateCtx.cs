namespace DeckTracker;

[System.Text.Json.Serialization.JsonSerializable(typeof(SavedRunState))]
[System.Text.Json.Serialization.JsonSerializable(typeof(RunLog))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ActData))]
[System.Text.Json.Serialization.JsonSerializable(typeof(CardStats))]
[System.Text.Json.Serialization.JsonSerializable(typeof(PotionStats))]
[System.Text.Json.Serialization.JsonSerializable(typeof(RelicStats))]
internal partial class SavedStateCtx : System.Text.Json.Serialization.JsonSerializerContext { }
