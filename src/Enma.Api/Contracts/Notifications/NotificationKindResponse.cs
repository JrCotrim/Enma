using System.Text.Json.Serialization;

namespace Enma.Api.Contracts.Notifications;

[JsonConverter(typeof(JsonStringEnumConverter<NotificationKindResponse>))]
public enum NotificationKindResponse
{
    [JsonStringEnumMemberName("legalDeadlineDueSoon")]
    LegalDeadlineDueSoon = 0,

    [JsonStringEnumMemberName("legalTaskDueSoon")]
    LegalTaskDueSoon = 1,

    [JsonStringEnumMemberName("calendarEventStartingSoon")]
    CalendarEventStartingSoon = 2
}
