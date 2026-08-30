using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Enma.Domain.Organizations;

namespace Enma.Domain.Auditing;

public abstract class AuditEventDetails
{
    public const int MaximumSerializedSizeInBytes = 8 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

    private protected AuditEventDetails()
    {
    }

    protected static string ValidateText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                AuditLogErrors.DetailsValueRequired,
                parameterName);
        }

        return value.Trim();
    }

    protected static IReadOnlyList<T> ValidateChangedFields<T>(
        IEnumerable<T> changedFields)
        where T : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(changedFields);

        var uniqueFields = new HashSet<T>();
        var validatedFields = new List<T>();

        foreach (T field in changedFields)
        {
            if (!Enum.IsDefined(field))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(changedFields),
                    AuditLogErrors.ChangedFieldInvalid);
            }

            if (!uniqueFields.Add(field))
            {
                throw new ArgumentException(
                    AuditLogErrors.ChangedFieldDuplicate,
                    nameof(changedFields));
            }

            validatedFields.Add(field);
        }

        if (validatedFields.Count == 0)
        {
            throw new ArgumentException(
                AuditLogErrors.ChangedFieldsRequired,
                nameof(changedFields));
        }

        validatedFields.Sort();
        return validatedFields.AsReadOnly();
    }

    protected static void ValidateAssigneeChange(
        Guid? oldAssigneeMembershipId,
        Guid? newAssigneeMembershipId)
    {
        if (oldAssigneeMembershipId == Guid.Empty)
        {
            throw new ArgumentException(
                AuditLogErrors.AssigneeMembershipIdInvalid,
                nameof(oldAssigneeMembershipId));
        }

        if (newAssigneeMembershipId == Guid.Empty)
        {
            throw new ArgumentException(
                AuditLogErrors.AssigneeMembershipIdInvalid,
                nameof(newAssigneeMembershipId));
        }

        if (oldAssigneeMembershipId == newAssigneeMembershipId)
        {
            throw new ArgumentException(
                AuditLogErrors.DetailsMustRepresentChange,
                nameof(newAssigneeMembershipId));
        }
    }

    internal static void ValidateSerializedSize(AuditEventDetails details)
    {
        _ = Serialize(details);
    }

    internal static string? Serialize(AuditEventDetails? details)
    {
        if (details is null)
        {
            return null;
        }

        string serializedDetails = JsonSerializer.Serialize(
            details,
            details.GetType(),
            SerializerOptions);

        if (Encoding.UTF8.GetByteCount(serializedDetails) >
            MaximumSerializedSizeInBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(details),
                AuditLogErrors.DetailsTooLarge);
        }

        return serializedDetails;
    }

    internal static AuditEventDetails? Deserialize(
        AuditEventType eventType,
        string? serializedDetails)
    {
        if (serializedDetails is null)
        {
            eventType.ValidateDetails(null);
            return null;
        }

        AuditEventDetails details = eventType switch
        {
            AuditEventType.OrganizationRenamed =>
                Deserialize<OrganizationRenamedAuditDetails>(serializedDetails),
            AuditEventType.OrganizationMembershipRoleChanged =>
                Deserialize<OrganizationMembershipRoleChangedAuditDetails>(
                    serializedDetails),
            AuditEventType.OrganizationInvitationCreated =>
                Deserialize<OrganizationInvitationCreatedAuditDetails>(
                    serializedDetails),
            AuditEventType.LegalDeadlineDetailsChanged =>
                new LegalDeadlineDetailsChangedAuditDetails(
                    DeserializeChangedFields<LegalDeadlineChangedField>(
                        serializedDetails)),
            AuditEventType.LegalTaskDetailsChanged =>
                new LegalTaskDetailsChangedAuditDetails(
                    DeserializeChangedFields<LegalTaskChangedField>(
                        serializedDetails)),
            AuditEventType.LegalTaskAssigneeChanged =>
                Deserialize<LegalTaskAssigneeChangedAuditDetails>(
                    serializedDetails),
            AuditEventType.CalendarEventUpdated =>
                new CalendarEventUpdatedAuditDetails(
                    DeserializeChangedFields<CalendarEventChangedField>(
                        serializedDetails)),
            AuditEventType.CalendarEventAssigneeChanged =>
                Deserialize<CalendarEventAssigneeChangedAuditDetails>(
                    serializedDetails),
            _ => throw new JsonException(
                AuditLogErrors.DetailsInvalidForEventType)
        };

        eventType.ValidateDetails(details);
        return details;
    }

    private static TDetails Deserialize<TDetails>(string serializedDetails)
        where TDetails : AuditEventDetails
    {
        return JsonSerializer.Deserialize<TDetails>(
                serializedDetails,
                SerializerOptions)
            ?? throw new JsonException(
                AuditLogErrors.DetailsInvalidForEventType);
    }

    private static TField[] DeserializeChangedFields<TField>(
        string serializedDetails)
        where TField : struct, Enum
    {
        ChangedFieldsEnvelope<TField>? envelope =
            JsonSerializer.Deserialize<ChangedFieldsEnvelope<TField>>(
                serializedDetails,
                SerializerOptions);

        return envelope?.ChangedFields
            ?? throw new JsonException(
                AuditLogErrors.DetailsInvalidForEventType);
    }

    private sealed record ChangedFieldsEnvelope<TField>(TField[] ChangedFields)
        where TField : struct, Enum;
}

public sealed class OrganizationRenamedAuditDetails : AuditEventDetails
{
    public OrganizationRenamedAuditDetails(string oldName, string newName)
    {
        OldName = ValidateText(oldName, nameof(oldName));
        NewName = ValidateText(newName, nameof(newName));

        if (StringComparer.Ordinal.Equals(OldName, NewName))
        {
            throw new ArgumentException(
                AuditLogErrors.DetailsMustRepresentChange,
                nameof(newName));
        }
    }

    public string OldName { get; }

    public string NewName { get; }
}

public sealed class OrganizationMembershipRoleChangedAuditDetails : AuditEventDetails
{
    public OrganizationMembershipRoleChangedAuditDetails(
        OrganizationRole oldRole,
        OrganizationRole newRole)
    {
        if (!Enum.IsDefined(oldRole))
        {
            throw new ArgumentOutOfRangeException(
                nameof(oldRole),
                AuditLogErrors.ActorRoleInvalid);
        }

        if (!Enum.IsDefined(newRole))
        {
            throw new ArgumentOutOfRangeException(
                nameof(newRole),
                AuditLogErrors.ActorRoleInvalid);
        }

        if (oldRole == newRole)
        {
            throw new ArgumentException(
                AuditLogErrors.DetailsMustRepresentChange,
                nameof(newRole));
        }

        OldRole = oldRole;
        NewRole = newRole;
    }

    public OrganizationRole OldRole { get; }

    public OrganizationRole NewRole { get; }
}

public sealed class OrganizationInvitationCreatedAuditDetails : AuditEventDetails
{
    public OrganizationInvitationCreatedAuditDetails(OrganizationRole role)
    {
        if (role is not (
            OrganizationRole.Administrator or OrganizationRole.Member))
        {
            throw new ArgumentOutOfRangeException(
                nameof(role),
                AuditLogErrors.OrganizationInvitationRoleInvalid);
        }

        Role = role;
    }

    public OrganizationRole Role { get; }
}

/// <summary>
/// Numeric values are permanent. Only append new values; never reuse one.
/// </summary>
public enum LegalDeadlineChangedField
{
    Title = 1,
    DueDate = 2
}

public sealed class LegalDeadlineDetailsChangedAuditDetails : AuditEventDetails
{
    public LegalDeadlineDetailsChangedAuditDetails(
        IEnumerable<LegalDeadlineChangedField> changedFields)
    {
        ChangedFields = ValidateChangedFields(changedFields);
    }

    public IReadOnlyList<LegalDeadlineChangedField> ChangedFields { get; }
}

/// <summary>
/// Numeric values are permanent. Only append new values; never reuse one.
/// </summary>
public enum LegalTaskChangedField
{
    Title = 1,
    Description = 2,
    DueDate = 3,
    ProcessId = 4
}

public sealed class LegalTaskDetailsChangedAuditDetails : AuditEventDetails
{
    public LegalTaskDetailsChangedAuditDetails(
        IEnumerable<LegalTaskChangedField> changedFields)
    {
        ChangedFields = ValidateChangedFields(changedFields);
    }

    public IReadOnlyList<LegalTaskChangedField> ChangedFields { get; }
}

public sealed class LegalTaskAssigneeChangedAuditDetails : AuditEventDetails
{
    public LegalTaskAssigneeChangedAuditDetails(
        Guid? oldAssigneeMembershipId,
        Guid? newAssigneeMembershipId)
    {
        ValidateAssigneeChange(
            oldAssigneeMembershipId,
            newAssigneeMembershipId);

        OldAssigneeMembershipId = oldAssigneeMembershipId;
        NewAssigneeMembershipId = newAssigneeMembershipId;
    }

    public Guid? OldAssigneeMembershipId { get; }

    public Guid? NewAssigneeMembershipId { get; }
}

/// <summary>
/// Numeric values are permanent. Only append new values; never reuse one.
/// </summary>
public enum CalendarEventChangedField
{
    Title = 1,
    Description = 2,
    StartsAt = 3,
    EndsAt = 4,
    Location = 5,
    ClientId = 6,
    ProcessId = 7
}

public sealed class CalendarEventUpdatedAuditDetails : AuditEventDetails
{
    public CalendarEventUpdatedAuditDetails(
        IEnumerable<CalendarEventChangedField> changedFields)
    {
        ChangedFields = ValidateChangedFields(changedFields);
    }

    public IReadOnlyList<CalendarEventChangedField> ChangedFields { get; }
}

public sealed class CalendarEventAssigneeChangedAuditDetails : AuditEventDetails
{
    public CalendarEventAssigneeChangedAuditDetails(
        Guid? oldAssigneeMembershipId,
        Guid? newAssigneeMembershipId)
    {
        ValidateAssigneeChange(
            oldAssigneeMembershipId,
            newAssigneeMembershipId);

        OldAssigneeMembershipId = oldAssigneeMembershipId;
        NewAssigneeMembershipId = newAssigneeMembershipId;
    }

    public Guid? OldAssigneeMembershipId { get; }

    public Guid? NewAssigneeMembershipId { get; }
}
