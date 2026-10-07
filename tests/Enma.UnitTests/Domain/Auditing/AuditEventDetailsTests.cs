using System.Text.Json;
using Enma.Domain.Auditing;
using Enma.Domain.Finance;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;

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

    [Theory]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public void OrganizationInvitationCreated_WithInvitableRole_PreservesRole(
        OrganizationRole role)
    {
        var details = new OrganizationInvitationCreatedAuditDetails(role);

        Assert.Equal(role, details.Role);
        Assert.Equal(
            [nameof(OrganizationInvitationCreatedAuditDetails.Role)],
            details.GetType()
                .GetProperties()
                .Select(property => property.Name)
                .ToArray());
    }

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData((OrganizationRole)999)]
    public void OrganizationInvitationCreated_WithForbiddenRole_Throws(
        OrganizationRole role)
    {
        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new OrganizationInvitationCreatedAuditDetails(role));

        Assert.Equal("role", exception.ParamName);
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

    [Fact]
    public void OrganizationInvitationCreated_DeserializationRejectsSecurityMetadata()
    {
        const string Json =
            """
            {"role":3,"invitedEmail":"recipient@example.test","tokenHash":"secret"}
            """;

        Assert.Throws<JsonException>(() => AuditEventDetails.Deserialize(
            AuditEventType.OrganizationInvitationCreated,
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
                AuditEventType.OrganizationInvitationCreated,
                new OrganizationInvitationCreatedAuditDetails(
                    OrganizationRole.Member)
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
            },
            {
                AuditEventType.LegalProcessDetailsChanged,
                new LegalProcessDetailsChangedAuditDetails(
                    [
                        LegalProcessChangedField.CourtOrAuthority,
                        LegalProcessChangedField.ProcessNumber
                    ])
            },
            {
                AuditEventType.LegalProcessStatusChanged,
                new LegalProcessStatusChangedAuditDetails(
                    LegalProcessStatus.Closed,
                    LegalProcessStatus.InProgress)
            },
            {
                AuditEventType.LegalProcessResponsibleChanged,
                new LegalProcessResponsibleChangedAuditDetails(
                    null,
                    MembershipAId)
            },
            {
                AuditEventType.LegalDeadlineResponsibleChanged,
                new LegalDeadlineResponsibleChangedAuditDetails(
                    MembershipAId,
                    null)
            },
            {
                AuditEventType.PaymentInstallmentPaymentReversed,
                new PaymentInstallmentPaymentReversedAuditDetails(
                    PaymentReversalReason.WrongInstallment)
            },
            {
                AuditEventType.OrganizationOwnershipTransferred,
                new OrganizationOwnershipTransferredAuditDetails(
                    MembershipAId,
                    MembershipBId)
            }
        };

    [Fact]
    public void OwnershipTransferred_SerializesOnlyMembershipIdentifiers()
    {
        string serialized = Assert.IsType<string>(AuditEventDetails.Serialize(
            new OrganizationOwnershipTransferredAuditDetails(
                MembershipAId,
                MembershipBId)));

        Assert.Equal(
            $$"""{"previousOwnerMembershipId":"{{MembershipAId:D}}","newOwnerMembershipId":"{{MembershipBId:D}}"}""",
            serialized);
    }

    [Theory]
    [InlineData(true, false, "previousOwnerMembershipId")]
    [InlineData(false, true, "newOwnerMembershipId")]
    public void OwnershipTransferred_WithEmptyIdentifier_Throws(
        bool emptyPrevious,
        bool emptyNew,
        string expectedParameterName)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            new OrganizationOwnershipTransferredAuditDetails(
                emptyPrevious ? Guid.Empty : MembershipAId,
                emptyNew ? Guid.Empty : MembershipBId));

        Assert.Equal(expectedParameterName, exception.ParamName);
    }

    [Fact]
    public void OwnershipTransferred_WithoutChange_Throws()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            new OrganizationOwnershipTransferredAuditDetails(
                MembershipAId,
                MembershipAId));

        Assert.Equal("newOwnerMembershipId", exception.ParamName);
    }

    [Fact]
    public void OwnershipTransferred_RequiresDetailsAndRejectsOtherTypes()
    {
        Assert.Throws<ArgumentException>(() =>
            AuditEventType.OrganizationOwnershipTransferred.ValidateDetails(null));
        Assert.Throws<ArgumentException>(() =>
            AuditEventType.OrganizationOwnershipTransferred.ValidateDetails(
                new OrganizationRenamedAuditDetails("Old", "New")));
        Assert.Throws<ArgumentException>(() =>
            AuditEventType.OrganizationRenamed.ValidateDetails(
                new OrganizationOwnershipTransferredAuditDetails(
                    MembershipAId,
                    MembershipBId)));
    }

    [Theory]
    [InlineData("""{"previousOwnerMembershipId":"5d5a1b36-0ec1-4d5f-a2f1-5f0b4d5b0a11","newOwnerMembershipId":"0f3d7b55-63a2-4a39-9f6e-3d9f5b7c4e22","previousOwnerUserId":"6a9a1b36-0ec1-4d5f-a2f1-5f0b4d5b0a33"}""")]
    [InlineData("""{"previousOwnerMembershipId":"5d5a1b36-0ec1-4d5f-a2f1-5f0b4d5b0a11","newOwnerMembershipId":"0f3d7b55-63a2-4a39-9f6e-3d9f5b7c4e22","organizationName":"Synthetic"}""")]
    public void OwnershipTransferred_DeserializationRejectsExtraData(string json)
    {
        Assert.Throws<JsonException>(() => AuditEventDetails.Deserialize(
            AuditEventType.OrganizationOwnershipTransferred,
            json));
    }

    [Fact]
    public void OwnershipTransferred_DeserializationRejectsEmptyIdentifier()
    {
        Assert.Throws<ArgumentException>(() => AuditEventDetails.Deserialize(
            AuditEventType.OrganizationOwnershipTransferred,
            """{"previousOwnerMembershipId":"00000000-0000-0000-0000-000000000000","newOwnerMembershipId":"0f3d7b55-63a2-4a39-9f6e-3d9f5b7c4e22"}"""));
    }

    [Theory]
    [InlineData(PaymentReversalReason.RegisteredByMistake, 1)]
    [InlineData(PaymentReversalReason.WrongInstallment, 2)]
    [InlineData(PaymentReversalReason.PaymentNotCompleted, 3)]
    [InlineData(PaymentReversalReason.Other, 4)]
    public void PaymentReversalReason_HasPermanentValueAndSerializesOnlyReason(
        PaymentReversalReason reason,
        int numericValue)
    {
        Assert.Equal(numericValue, (int)reason);

        string serialized = Assert.IsType<string>(AuditEventDetails.Serialize(
            new PaymentInstallmentPaymentReversedAuditDetails(reason)));

        Assert.Equal($$"""{"reason":{{numericValue}}}""", serialized);
    }

    [Fact]
    public void PaymentReversalReason_ValuesAreComplete()
    {
        Assert.Equal(
            [1, 2, 3, 4],
            Enum.GetValues<PaymentReversalReason>()
                .Select(reason => (int)reason)
                .Order());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public void PaymentInstallmentPaymentReversed_WithUndefinedReason_Throws(
        int reason)
    {
        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new PaymentInstallmentPaymentReversedAuditDetails(
                    (PaymentReversalReason)reason));

        Assert.Equal("reason", exception.ParamName);
    }

    [Fact]
    public void PaymentInstallmentPaymentReversed_RequiresDetailsAndRejectsOtherTypes()
    {
        Assert.Throws<ArgumentException>(() =>
            AuditEventType.PaymentInstallmentPaymentReversed.ValidateDetails(null));
        Assert.Throws<ArgumentException>(() =>
            AuditEventType.PaymentInstallmentPaymentReversed.ValidateDetails(
                new LegalDeadlineResponsibleChangedAuditDetails(
                    null,
                    MembershipAId)));
        Assert.Throws<ArgumentException>(() =>
            AuditEventType.PaymentInstallmentPaid.ValidateDetails(
                new PaymentInstallmentPaymentReversedAuditDetails(
                    PaymentReversalReason.Other)));
    }

    [Fact]
    public void PaymentInstallmentPaymentReversed_DeserializationRejectsUndefinedReason()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AuditEventDetails.Deserialize(
                AuditEventType.PaymentInstallmentPaymentReversed,
                """{"reason":99}"""));
    }

    [Theory]
    [InlineData("""{"reason":2,"amount":"100.00"}""")]
    [InlineData("""{"reason":2,"paidAt":"2026-10-03T12:00:00Z"}""")]
    [InlineData("""{"reason":2,"note":"free text"}""")]
    public void PaymentInstallmentPaymentReversed_DeserializationRejectsExtraData(
        string json)
    {
        Assert.Throws<JsonException>(() => AuditEventDetails.Deserialize(
            AuditEventType.PaymentInstallmentPaymentReversed,
            json));
    }

    [Fact]
    public void LegalProcessStatusChanged_WithInvalidChange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LegalProcessStatusChangedAuditDetails(
                (LegalProcessStatus)99,
                LegalProcessStatus.Closed));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LegalProcessStatusChangedAuditDetails(
                LegalProcessStatus.Closed,
                (LegalProcessStatus)0));
        ArgumentException sameStatus = Assert.Throws<ArgumentException>(() =>
            new LegalProcessStatusChangedAuditDetails(
                LegalProcessStatus.Suspended,
                LegalProcessStatus.Suspended));

        Assert.Equal("newStatus", sameStatus.ParamName);
    }

    [Theory]
    [InlineData(true, false, "oldResponsibleMembershipId")]
    [InlineData(false, true, "newResponsibleMembershipId")]
    public void LegalProcessResponsibleChanged_WithEmptyIdentifier_Throws(
        bool oldIsEmpty,
        bool newIsEmpty,
        string expectedParameterName)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            new LegalProcessResponsibleChangedAuditDetails(
                oldIsEmpty ? Guid.Empty : MembershipAId,
                newIsEmpty ? Guid.Empty : MembershipBId));

        Assert.Equal(expectedParameterName, exception.ParamName);
    }

    [Fact]
    public void LegalProcessResponsibleChanged_WithoutChange_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new LegalProcessResponsibleChangedAuditDetails(null, null));
        Assert.Throws<ArgumentException>(() =>
            new LegalProcessResponsibleChangedAuditDetails(
                MembershipAId,
                MembershipAId));
    }

    [Theory]
    [InlineData(true, false, "oldResponsibleMembershipId")]
    [InlineData(false, true, "newResponsibleMembershipId")]
    public void LegalDeadlineResponsibleChanged_WithEmptyIdentifier_Throws(
        bool oldIsEmpty,
        bool newIsEmpty,
        string expectedParameterName)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            new LegalDeadlineResponsibleChangedAuditDetails(
                oldIsEmpty ? Guid.Empty : MembershipAId,
                newIsEmpty ? Guid.Empty : MembershipBId));

        Assert.Equal(expectedParameterName, exception.ParamName);
    }

    [Fact]
    public void LegalDeadlineResponsibleChanged_WithoutChange_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new LegalDeadlineResponsibleChangedAuditDetails(null, null));
        Assert.Throws<ArgumentException>(() =>
            new LegalDeadlineResponsibleChangedAuditDetails(
                MembershipAId,
                MembershipAId));
    }

    [Fact]
    public void LegalDeadlineResponsibleChanged_SerializesOnlyMembershipIdentifiers()
    {
        string serialized = Assert.IsType<string>(AuditEventDetails.Serialize(
            new LegalDeadlineResponsibleChangedAuditDetails(
                null,
                MembershipBId)));

        Assert.Equal(
            $$"""{"oldResponsibleMembershipId":null,"newResponsibleMembershipId":"{{MembershipBId:D}}"}""",
            serialized);
    }

    [Fact]
    public void LegalDeadlineResponsibleChanged_RequiresDetailsAndRejectsOtherTypes()
    {
        Assert.Throws<ArgumentException>(() =>
            AuditEventType.LegalDeadlineResponsibleChanged.ValidateDetails(null));
        Assert.Throws<ArgumentException>(() =>
            AuditEventType.LegalDeadlineResponsibleChanged.ValidateDetails(
                new LegalProcessResponsibleChangedAuditDetails(
                    null,
                    MembershipAId)));
        Assert.Throws<ArgumentException>(() =>
            AuditEventType.LegalProcessResponsibleChanged.ValidateDetails(
                new LegalDeadlineResponsibleChangedAuditDetails(
                    null,
                    MembershipAId)));
    }

    [Fact]
    public void LegalProcessDetailsChanged_SerializesOnlyFieldIdentifiers()
    {
        string serialized = Assert.IsType<string>(AuditEventDetails.Serialize(
            new LegalProcessDetailsChangedAuditDetails(
                [LegalProcessChangedField.ProcessNumber])));

        Assert.Equal("""{"changedFields":[1]}""", serialized);
    }
}
