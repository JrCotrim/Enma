using System.Reflection;
using System.Runtime.CompilerServices;
using Enma.Domain.Auditing;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Domain.Auditing;

public sealed class AuditLogTests
{
    private static readonly Guid Id = Guid.Parse(
        "11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrganizationId = Guid.Parse(
        "22222222-2222-2222-2222-222222222222");
    private static readonly Guid ActorUserId = Guid.Parse(
        "33333333-3333-3333-3333-333333333333");
    private static readonly Guid ActorMembershipId = Guid.Parse(
        "44444444-4444-4444-4444-444444444444");
    private static readonly Guid EntityId = Guid.Parse(
        "55555555-5555-5555-5555-555555555555");
    private static readonly DateTimeOffset OccurredAt = new(
        2026,
        8,
        27,
        10,
        30,
        0,
        TimeSpan.FromHours(-3));
    private const string TraceId = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void CreateAuthoritative_WithValidContext_CreatesImmutableRecord()
    {
        var details = new OrganizationRenamedAuditDetails(
            "Old organization",
            "New organization");

        AuditLog auditLog = CreateAuditLog(
            eventType: AuditEventType.OrganizationRenamed,
            entityType: AuditEntityType.Organization,
            details: details,
            traceId: TraceId);

        Assert.Equal(Id, auditLog.Id);
        Assert.Equal(OrganizationId, auditLog.OrganizationId);
        Assert.Equal(ActorUserId, auditLog.ActorUserId);
        Assert.Equal(ActorMembershipId, auditLog.ActorMembershipId);
        Assert.Equal(OrganizationRole.Administrator, auditLog.ActorRoleAtOccurrence);
        Assert.Equal(AuditEventType.OrganizationRenamed, auditLog.EventType);
        Assert.Equal(AuditEntityType.Organization, auditLog.EntityType);
        Assert.Equal(EntityId, auditLog.EntityId);
        Assert.Equal(OccurredAt.UtcDateTime, auditLog.OccurredAt.UtcDateTime);
        Assert.Equal(TimeSpan.Zero, auditLog.OccurredAt.Offset);
        Assert.Same(details, auditLog.Details);
        Assert.Equal(TraceId, auditLog.TraceId);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("organizationId")]
    [InlineData("actorUserId")]
    [InlineData("actorMembershipId")]
    [InlineData("entityId")]
    public void CreateAuthoritative_WithEmptyRequiredIdentifier_Throws(
        string expectedParameterName)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            CreateAuditLog(
                id: expectedParameterName == "id" ? Guid.Empty : Id,
                organizationId: expectedParameterName == "organizationId"
                    ? Guid.Empty
                    : OrganizationId,
                actorUserId: expectedParameterName == "actorUserId"
                    ? Guid.Empty
                    : ActorUserId,
                actorMembershipId: expectedParameterName == "actorMembershipId"
                    ? Guid.Empty
                    : ActorMembershipId,
                entityId: expectedParameterName == "entityId"
                    ? Guid.Empty
                    : EntityId));

        Assert.Equal(expectedParameterName, exception.ParamName);
    }

    [Fact]
    public void CreateAuthoritative_WithInvalidRole_Throws()
    {
        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                CreateAuditLog(actorRole: (OrganizationRole)999));

        Assert.Equal("actorRoleAtOccurrence", exception.ParamName);
    }

    [Fact]
    public void CreateAuthoritative_WithInvalidEventType_Throws()
    {
        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                CreateAuditLog(eventType: (AuditEventType)999));

        Assert.Equal("eventType", exception.ParamName);
    }

    [Fact]
    public void CreateAuthoritative_WithInvalidEntityType_Throws()
    {
        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                CreateAuditLog(entityType: (AuditEntityType)999));

        Assert.Equal("entityType", exception.ParamName);
    }

    [Fact]
    public void CreateAuthoritative_WithMismatchedEntityType_Throws()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            CreateAuditLog(entityType: AuditEntityType.Client));

        Assert.Equal("entityType", exception.ParamName);
    }

    [Fact]
    public void CreateAuthoritative_WithInvalidTimestamp_Throws()
    {
        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                CreateAuditLog(occurredAt: DateTimeOffset.MinValue));

        Assert.Equal("occurredAt", exception.ParamName);
    }

    [Fact]
    public void CreateAuthoritative_WithNullTraceId_PreservesNull()
    {
        AuditLog auditLog = CreateAuditLog(traceId: null);

        Assert.Null(auditLog.TraceId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456789abcdef")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcdeg")]
    [InlineData("00000000000000000000000000000000")]
    public void CreateAuthoritative_WithMalformedTraceId_Throws(string traceId)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            CreateAuditLog(traceId: traceId));

        Assert.Equal("traceId", exception.ParamName);
    }

    [Fact]
    public void CreateAuthoritative_WithMissingOrUnexpectedDetails_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            CreateAuditLog(
                eventType: AuditEventType.OrganizationRenamed,
                entityType: AuditEntityType.Organization));

        Assert.Throws<ArgumentException>(() =>
            CreateAuditLog(
                details: new OrganizationRenamedAuditDetails("Old", "New")));
    }

    [Fact]
    public void CreateAuthoritative_WithOversizedSerializedDetails_Throws()
    {
        var details = new OrganizationRenamedAuditDetails(
            new string(
                'a',
                AuditEventDetails.MaximumSerializedSizeInBytes),
            "New organization");

        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                CreateAuditLog(
                    eventType: AuditEventType.OrganizationRenamed,
                    entityType: AuditEntityType.Organization,
                    details: details));

        Assert.Equal("details", exception.ParamName);
    }

    [Fact]
    public void AuditLog_HasOnlyPrivateSettersAndNoMutationMethods()
    {
        PropertyInfo[] properties = typeof(AuditLog).GetProperties();
        MethodInfo[] mutationMethods = typeof(AuditLog)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .ToArray();

        Assert.All(
            properties,
            property => Assert.True(property.SetMethod?.IsPrivate));
        Assert.Empty(mutationMethods);
        Assert.Null(typeof(AuditLog).GetProperty("UpdatedAt"));
        Assert.Null(typeof(AuditLog).GetProperty("ExpiresAt"));
    }

    [Fact]
    public void AuthoritativeFactory_IsInternalAndApplicationIsNotFriend()
    {
        MethodInfo? factory = typeof(AuditLog).GetMethod(
            "CreateAuthoritative",
            BindingFlags.NonPublic | BindingFlags.Static);
        string?[] friendAssemblies = typeof(AuditLog).Assembly
            .GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(attribute => new AssemblyName(attribute.AssemblyName).Name)
            .ToArray();

        Assert.NotNull(factory);
        Assert.True(factory.IsAssembly);
        Assert.Contains("Enma.Infrastructure", friendAssemblies);
        Assert.Contains("Enma.UnitTests", friendAssemblies);
        Assert.DoesNotContain("Enma.Application", friendAssemblies);
    }

    private static AuditLog CreateAuditLog(
        Guid? id = null,
        Guid? organizationId = null,
        Guid? actorUserId = null,
        Guid? actorMembershipId = null,
        OrganizationRole actorRole = OrganizationRole.Administrator,
        AuditEventType eventType = AuditEventType.CalendarEventDeleted,
        AuditEntityType entityType = AuditEntityType.CalendarEvent,
        Guid? entityId = null,
        DateTimeOffset? occurredAt = null,
        AuditEventDetails? details = null,
        string? traceId = TraceId)
    {
        return AuditLog.CreateAuthoritative(
            id ?? Id,
            organizationId ?? OrganizationId,
            actorUserId ?? ActorUserId,
            actorMembershipId ?? ActorMembershipId,
            actorRole,
            eventType,
            entityType,
            entityId ?? EntityId,
            occurredAt ?? OccurredAt,
            details,
            traceId);
    }
}
