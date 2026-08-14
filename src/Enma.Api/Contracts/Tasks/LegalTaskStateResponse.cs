using System.Text.Json.Serialization;

namespace Enma.Api.Contracts.Tasks;

[JsonConverter(typeof(JsonStringEnumConverter<LegalTaskStateResponse>))]
public enum LegalTaskStateResponse
{
    [JsonStringEnumMemberName("pending")]
    Pending = 0,

    [JsonStringEnumMemberName("completed")]
    Completed = 1
}
