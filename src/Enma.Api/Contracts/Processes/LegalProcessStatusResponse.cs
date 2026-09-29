using System.Text.Json.Serialization;

namespace Enma.Api.Contracts.Processes;

[JsonConverter(typeof(JsonStringEnumConverter<LegalProcessStatusResponse>))]
public enum LegalProcessStatusResponse
{
    [JsonStringEnumMemberName("inProgress")]
    InProgress = 1,

    [JsonStringEnumMemberName("suspended")]
    Suspended = 2,

    [JsonStringEnumMemberName("closed")]
    Closed = 3
}
