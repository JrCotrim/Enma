using System.Text.Json.Serialization;

namespace Enma.Api.Contracts.Agenda;

[JsonConverter(typeof(JsonStringEnumConverter<AgendaItemKindResponse>))]
public enum AgendaItemKindResponse
{
    [JsonStringEnumMemberName("deadline")]
    Deadline = 0,

    [JsonStringEnumMemberName("task")]
    Task = 1,

    [JsonStringEnumMemberName("calendarEvent")]
    CalendarEvent = 2
}
