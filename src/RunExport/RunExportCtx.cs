using System.Text.Json.Serialization;

namespace DeckTracker;

// Dedicated source-generated serializer for the user-facing run export. Indented for readability; kept
// separate from SavedStateCtx so the compact internal save file is unaffected.
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(RunLog))]
internal partial class RunExportCtx : JsonSerializerContext { }
