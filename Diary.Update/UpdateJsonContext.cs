using System.Text.Json.Serialization;

namespace Diary.Update;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(UpdateJournalEntry))]
[JsonSerializable(typeof(UpdateMachineVersion))]
[JsonSerializable(typeof(UpdateManifestEnvelope))]
[JsonSerializable(typeof(UpdateTransactionPlan))]
[JsonSerializable(typeof(UpdateTransactionStatus))]
public sealed partial class UpdateJsonContext : JsonSerializerContext;
