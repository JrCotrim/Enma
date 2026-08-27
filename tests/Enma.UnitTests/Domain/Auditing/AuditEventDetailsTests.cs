using System.Text.Json;
using Enma.Domain.Auditing;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Domain.Auditing;

public sealed class AuditEventDetailsTests
{
    private static readonly Guid MembershipAId = Guid.Parse(
        "11111111-1111-1111-1111-111111111111");
    private static readonly Guid MembershipBId = Guid.Parse(
        "22222222-2222-2222-2222-222222222222");

    [Fact]
    public void OrganizationRenamed_WithValidNames_NormalizesAndPreservesValues()
    {
        var details = new OrganizationRenamedAuditDetails(
            " Old organization ",
            " New organization ");

        Assert.Equal("Old organization", details.OldName);
        Assert.Equal("New organization", details.NewName);
    }

    [Theory]
    [InlineData(null, "New organization", "oldName")]
    [InlineData("Old organization", " ", "newName")]
    [InlineData("Same", "Same", "newName")]
    public void OrganizationRenamed_WithInvalidNames_Throws(
        string? oldName,
        string? newName,
        string expectedParameterName)
    {
        ArgumentException exception = Assert.ThrowsAny<ArgumentException>(() =>
            new OrganizationRenamedAuditDetails(oldName!, newName!));

        Assert.Equal(expectedParameterName, exception.ParamName);
    }

    [Fact]
    public void MembershipRoleChanged_WithValidRoles_PreservesValues()
    {
        var details = new OrganizationMembershipRoleChangedAuditDetails(
            OrganizationRole.Member,
            OrganizationRole.Administrator);

        Assert.Equal(OrganizationRole.Member, details.OldRole);
        Assert.Equal(OrganizationRole.Administrator, details.NewRole);
    }

    [Theory]
    [InlineData(999, 2, "oldRole")]
    [InlineData(3, 999, "newRole")]
    [InlineData(3, 3, "newRole")]
    public void MembershipRoleChanged_WithInvalidChange_Throws(
        int oldRole,
        int newRole,
        string expectedParameterName)
    {
        ArgumentException exception = Assert.ThrowsAny<ArgumentException>(() =>
            new OrganizationMembershipRoleChangedAuditDetails(
                (OrganizationRole)oldRole,
                (OrganizationRole)newRole));

        Assert.Equal(expectedParameterName, exception.ParamName);
    }

    [Fact]
    public void ChangedFields_AreClosedCanonicalAndDefensivelyCopied()
    {
        LegalTaskChangedField[] source =
        [
            LegalTaskChangedField.ProcessId,
            LegalTaskChangedField.Title
        ];

        var details = new LegalTaskDetailsChangedAuditDetails(source);
        source[0] = LegalTaskChangedField.Description;

        Assert.Equal(
            [LegalTaskChangedField.Title, LegalTaskChangedField.ProcessId],
            details.ChangedFields);
    }

    [Fact]
    public void ChangedFields_WithNoValues_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new LegalDeadlineDetailsChangedAuditDetails([]));
    }

    [Fact]
    public void ChangedFields_WithUnknownValue_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CalendarEventUpdatedAuditDetails(
                [(CalendarEventChangedField)999]));
    }

    [Fact]
    public void ChangedFields_WithDuplicateValue_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new LegalTaskDetailsChangedAuditDetails(
                [LegalTaskChangedField.Title, LegalTaskChangedField.Title]));
    }

    [Theory]
    [MemberData(nameof(ValidAssigneeChanges))]
    public void AssigneeChanged_WithEffectiveChange_PreservesValues(
        Guid? oldAssigneeMembershipId,
        Guid? newAssigneeMembershipId)
    {
        var details = new LegalTaskAssigneeChangedAuditDetails(
            oldAssigneeMembershipId,
            newAssigneeMembershipId);

        Assert.Equal(
            oldAssigneeMembershipId,
            details.OldAssigneeMembershipId);
        Assert.Equal(
            newAssigneeMembershipId,
            details.NewAssigneeMembershipId);
    }

    [Theory]
    [MemberData(nameof(InvalidAssigneeChanges))]
    public void AssigneeChanged_WithInvalidChange_Throws(
        Guid? oldAssigneeMembershipId,
        Guid? newAssigneeMembershipId,
        string expectedParameterName)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            new CalendarEventAssigneeChangedAuditDetails(
                oldAssigneeMembershipId,
                newAssigneeMembershipId));

        Assert.Equal(expectedParameterName, exception.ParamName);
    }

    [Theory]
    [MemberData(nameof(SerializableDetails))]
    public void Serialization_RoundTripsClosedDetailsWithoutTypeMetadata(
        AuditEventType eventType,
        AuditEventDetails details)
    {
        string serialized = Assert.IsType<string>(
            AuditEventDetails.Serialize(details));

        AuditEventDetails roundTripped = Assert.IsAssignableFrom<AuditEventDetails>(
            AuditEventDetails.Deserialize(eventType, serialized));

        Assert.Equal(details.GetType(), roundTripped.GetType());
        Assert.Equal(serialized, AuditEventDetails.Serialize(roundTripped));
        Assert.DoesNotContain("$type", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("assembly", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialization_WithUnexpectedMetadata_RejectsJson()
    {
        const string Json =
            """
            {"oldName":"Old","newName":"New","arbitrary":"metadata"}
            """;

        Assert.Throws<JsonException>(() => AuditEventDetails.Deserialize(
            AuditEventType.OrganizationRenamed,
            Json));
    }

    public static TheoryData<Guid?, Guid?> ValidAssigneeChanges =>
        new()
        {
            { null, MembershipAId },
            { MembershipAId, MembershipBId },
            { MembershipAId, null }
        };

    public static TheoryData<Guid?, Guid?, string> InvalidAssigneeChanges =>
        new()
        {
            { Guid.Empty, MembershipAId, "oldAssigneeMembershipId" },
            { MembershipAId, Guid.Empty, "newAssigneeMembershipId" },
            { null, null, "newAssigneeMembershipId" },
            { MembershipAId, MembershipAId, "newAssigneeMembershipId" }
        };

    public static TheoryData<AuditEventType, AuditEventDetails>
        SerializableDetails =>
        new()
        {
            {
                AuditEventType.OrganizationRenamed,
                new OrganizationRenamedAuditDetails("Old", "New")
            },
            {
                AuditEventType.OrganizationMembershipRoleChanged,
                new OrganizationMembershipRoleChangedAuditDetails(
                    OrganizationRole.Member,
                    OrganizationRole.Administrator)
            },
            {
                AuditEventType.LegalDeadlineDetailsChanged,
                new LegalDeadlineDetailsChangedAuditDetails(
                    [LegalDeadlineChangedField.DueDate])
            },
            {
                AuditEventType.LegalTaskDetailsChanged,
                new LegalTaskDetailsChangedAuditDetails(
                    [LegalTaskChangedField.Description])
            },
            {
                AuditEventType.LegalTaskAssigneeChanged,
                new LegalTaskAssigneeChangedAuditDetails(
                    MembershipAId,
                    MembershipBId)
            },
            {
                AuditEventType.CalendarEventUpdated,
                new CalendarEventUpdatedAuditDetails(
                    [CalendarEventChangedField.Location])
            },
            {
                AuditEventType.CalendarEventAssigneeChanged,
                new CalendarEventAssigneeChangedAuditDetails(
                    MembershipAId,
                    MembershipBId)
            }
        };
}
