using System.Text.Json.Serialization;

namespace Enma.Api.Contracts.Deadlines;

[JsonConverter(typeof(JsonStringEnumConverter<LegalDeadlineStateResponse>))]
public enum LegalDeadlineStateResponse
{
    Pending = 0,
    Completed = 1
}
