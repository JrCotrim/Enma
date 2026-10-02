using System.Text.Json.Serialization;

namespace Enma.Api.Contracts.Clients;

[JsonConverter(typeof(JsonStringEnumConverter<ClientPersonTypeResponse>))]
public enum ClientPersonTypeResponse
{
    [JsonStringEnumMemberName("individual")]
    Individual = 1,

    [JsonStringEnumMemberName("company")]
    Company = 2
}
