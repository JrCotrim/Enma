using System.Text.Json.Serialization;

namespace Enma.Api.Contracts.Notifications;

[JsonConverter(typeof(JsonStringEnumConverter<NotificationSourceTypeResponse>))]
public enum NotificationSourceTypeResponse
{
    [JsonStringEnumMemberName("legalDeadline")]
    LegalDeadline = 0,

    [JsonStringEnumMemberName("legalTask")]
    LegalTask = 1,

    [JsonStringEnumMemberName("calendarEvent")]
    CalendarEvent = 2
}
