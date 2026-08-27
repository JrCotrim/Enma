using System.Collections;
using System.Reflection;
using Enma.Application.Auditing;
using Enma.Domain.Auditing;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Auditing;

public sealed class AuditIntentTests
{
    private static readonly Guid EntityId = Guid.Parse(
        "11111111-1111-1111-1111-111111111111");
    private static readonly Guid MembershipAId = Guid.Parse(
        "22222222-2222-2222-2222-222222222222");
    private static readonly Guid MembershipBId = Guid.Parse(
        "33333333-3333-3333-3333-333333333333");

    [Theory]
    [MemberData(nameof(ValidIntents))]
    public void Constructor_WithApprovedSemanticShape_CreatesIntent(
        AuditEventType eventType,
        AuditEntityType expectedEntityType,
        AuditEventDetails? details)
    {
        var intent = new AuditIntent(eventType, EntityId, details);

        Assert.Equal(eventType, intent.EventType);
        Assert.Equal(expectedEntityType, intent.EntityType);
        Assert.Equal(EntityId, intent.EntityId);
        Assert.Same(details, intent.Details);
    }

    [Fact]
    public void Constructor_WithEmptyEntityId_Throws()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            new AuditIntent(AuditEventType.ClientCreated, Guid.Empty));

        Assert.Equal("entityId", exception.ParamName);
    }

    [Fact]
    public void Constructor_WithUnknownEventType_Throws()
    {
        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new AuditIntent((AuditEventType)999, EntityId));

        Assert.Equal("eventType", exception.ParamName);
    }

    [Fact]
    public void Constructor_WithMissingMismatchedOrUnexpectedDetails_Throws()
    {
        var renamed = new OrganizationRenamedAuditDetails("Old", "New");
        var roleChanged = new OrganizationMembershipRoleChangedAuditDetails(
            OrganizationRole.Member,
            OrganizationRole.Administrator);

        Assert.Throws<ArgumentException>(() =>
            new AuditIntent(AuditEventType.OrganizationRenamed, EntityId));
        Assert.Throws<ArgumentException>(() =>
            new AuditIntent(
                AuditEventType.OrganizationRenamed,
                EntityId,
                roleChanged));
        Assert.Throws<ArgumentException>(() =>
            new AuditIntent(AuditEventType.ClientCreated, EntityId, renamed));
    }

    [Fact]
    public void PublicContract_CannotCarryAuthoritativeContextOrGenericMetadata()
    {
        string[] forbiddenNames =
        [
            "OrganizationId",
            "ActorUserId",
            "ActorMembershipId",
            "ActorRoleAtOccurrence",
            "OccurredAt",
            "TraceId"
        ];
        PropertyInfo[] properties = typeof(AuditIntent).GetProperties();
        ParameterInfo[] constructorParameters = typeof(AuditIntent)
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .ToArray();

        Assert.DoesNotContain(
            properties,
            property => forbiddenNames.Contains(
                property.Name,
                StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            constructorParameters,
            parameter => forbiddenNames.Contains(
                parameter.Name!,
                StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            properties.Select(property => property.PropertyType)
                .Concat(constructorParameters.Select(parameter => parameter.ParameterType)),
            type => typeof(IDictionary).IsAssignableFrom(type) ||
                type.FullName?.StartsWith(
                    "System.Text.Json",
                    StringComparison.Ordinal) == true);
        Assert.Equal(
            typeof(AuditEventDetails),
            properties.Single(property => property.Name == "Details").PropertyType);
    }

    [Fact]
    public void LayerAssemblies_DoNotReferenceInfrastructureOrApi()
    {
        string?[] forbiddenReferences = ["Enma.Infrastructure", "Enma.Api"];

        foreach (Assembly assembly in new[]
        {
            typeof(AuditIntent).Assembly,
            typeof(AuditLog).Assembly
        })
        {
            string?[] references = assembly
                .GetReferencedAssemblies()
                .Select(reference => reference.Name)
                .ToArray();

            Assert.DoesNotContain(references, forbiddenReferences.Contains);
        }
    }

    public static TheoryData<AuditEventType, AuditEntityType, AuditEventDetails?>
        ValidIntents =>
        new()
        {
            {
                AuditEventType.OrganizationRenamed,
                AuditEntityType.Organization,
                new OrganizationRenamedAuditDetails("Old", "New")
            },
            {
                AuditEventType.OrganizationMembershipRoleChanged,
                AuditEntityType.OrganizationMembership,
                new OrganizationMembershipRoleChangedAuditDetails(
                    OrganizationRole.Member,
                    OrganizationRole.Administrator)
            },
            {
                AuditEventType.OrganizationMembershipDeactivated,
                AuditEntityType.OrganizationMembership,
                null
            },
            {
                AuditEventType.OrganizationMembershipReactivated,
                AuditEntityType.OrganizationMembership,
                null
            },
            { AuditEventType.ClientCreated, AuditEntityType.Client, null },
            { AuditEventType.ClientRenamed, AuditEntityType.Client, null },
            { AuditEventType.ClientDeactivated, AuditEntityType.Client, null },
            { AuditEventType.ClientReactivated, AuditEntityType.Client, null },
            {
                AuditEventType.LegalProcessCreated,
                AuditEntityType.LegalProcess,
                null
            },
            {
                AuditEventType.LegalProcessTitleChanged,
                AuditEntityType.LegalProcess,
                null
            },
            {
                AuditEventType.LegalDeadlineCreated,
                AuditEntityType.LegalDeadline,
                null
            },
            {
                AuditEventType.LegalDeadlineDetailsChanged,
                AuditEntityType.LegalDeadline,
                new LegalDeadlineDetailsChangedAuditDetails(
                    [LegalDeadlineChangedField.DueDate])
            },
            {
                AuditEventType.LegalDeadlineCompleted,
                AuditEntityType.LegalDeadline,
                null
            },
            {
                AuditEventType.LegalDeadlineReopened,
                AuditEntityType.LegalDeadline,
                null
            },
            { AuditEventType.LegalTaskCreated, AuditEntityType.LegalTask, null },
            {
                AuditEventType.LegalTaskDetailsChanged,
                AuditEntityType.LegalTask,
                new LegalTaskDetailsChangedAuditDetails(
                    [LegalTaskChangedField.Description])
            },
            {
                AuditEventType.LegalTaskAssigneeChanged,
                AuditEntityType.LegalTask,
                new LegalTaskAssigneeChangedAuditDetails(
                    MembershipAId,
                    MembershipBId)
            },
            { AuditEventType.LegalTaskCompleted, AuditEntityType.LegalTask, null },
            { AuditEventType.LegalTaskReopened, AuditEntityType.LegalTask, null },
            {
                AuditEventType.CalendarEventCreated,
                AuditEntityType.CalendarEvent,
                null
            },
            {
                AuditEventType.CalendarEventUpdated,
                AuditEntityType.CalendarEvent,
                new CalendarEventUpdatedAuditDetails(
                    [CalendarEventChangedField.StartsAt])
            },
            {
                AuditEventType.CalendarEventAssigneeChanged,
                AuditEntityType.CalendarEvent,
                new CalendarEventAssigneeChangedAuditDetails(
                    MembershipAId,
                    null)
            },
            {
                AuditEventType.CalendarEventDeleted,
                AuditEntityType.CalendarEvent,
                null
            },
            {
                AuditEventType.LegalDocumentUploaded,
                AuditEntityType.LegalDocument,
                null
            }
        };
}
