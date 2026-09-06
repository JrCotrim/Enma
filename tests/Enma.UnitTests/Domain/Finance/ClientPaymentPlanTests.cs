using Enma.Domain.Finance;

namespace Enma.UnitTests.Domain.Finance;

public sealed class ClientPaymentPlanTests
{
    private static readonly Guid OrganizationId = Guid.NewGuid();
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_ValidMonthlyPlan_GeneratesCompleteSchedule()
    {
        var plan = new ClientPaymentPlan(
            OrganizationId,
            ClientId,
            6000m,
            6,
            new DateOnly(2026, 9, 10),
            CreatedAt);

        Assert.NotEqual(Guid.Empty, plan.Id);
        Assert.Equal(OrganizationId, plan.OrganizationId);
        Assert.Equal(ClientId, plan.ClientId);
        Assert.Equal(6000m, plan.TotalAmount);
        Assert.Equal(6, plan.InstallmentCount);
        Assert.Equal(new DateOnly(2026, 9, 10), plan.FirstDueDate);
        Assert.Equal(CreatedAt, plan.CreatedAt);

        Assert.Equal(6, plan.Installments.Count);
        Assert.Equal(
            6000m,
            plan.Installments.Sum(installment => installment.Amount));

        Assert.Equal(
            Enumerable.Range(1, 6),
            plan.Installments.Select(installment => installment.SequenceNumber));

        Assert.All(
            plan.Installments,
            installment =>
            {
                Assert.NotEqual(Guid.Empty, installment.Id);
                Assert.Equal(OrganizationId, installment.OrganizationId);
                Assert.Equal(plan.Id, installment.PaymentPlanId);
                Assert.Equal(1000m, installment.Amount);
                Assert.Equal(CreatedAt, installment.CreatedAt);
                Assert.Null(installment.PaidAt);
            });
    }

    [Fact]
    public void Constructor_AmountNotEvenlyDivisible_DistributesResidualCentsFromFirstInstallment()
    {
        var plan = new ClientPaymentPlan(
            OrganizationId,
            ClientId,
            100m,
            3,
            new DateOnly(2026, 9, 10),
            CreatedAt);

        Assert.Equal(
            [33.34m, 33.33m, 33.33m],
            plan.Installments
                .Select(installment => installment.Amount)
                .ToArray());

        Assert.Equal(
            100m,
            plan.Installments.Sum(installment => installment.Amount));
    }

    [Fact]
    public void Constructor_JanuaryThirtyFirst_PreservesOriginalAnchorDayAcrossMonths()
    {
        var plan = new ClientPaymentPlan(
            OrganizationId,
            ClientId,
            400m,
            4,
            new DateOnly(2027, 1, 31),
            CreatedAt);

        Assert.Equal(
            [
                new DateOnly(2027, 1, 31),
                new DateOnly(2027, 2, 28),
                new DateOnly(2027, 3, 31),
                new DateOnly(2027, 4, 30)
            ],
            plan.Installments
                .Select(installment => installment.DueDate)
                .ToArray());
    }

    [Fact]
    public void Constructor_JanuaryThirtyFirstInLeapYear_UsesFebruaryTwentyNinthAndRestoresAnchor()
    {
        var plan = new ClientPaymentPlan(
            OrganizationId,
            ClientId,
            300m,
            3,
            new DateOnly(2028, 1, 31),
            CreatedAt);

        Assert.Equal(
            [
                new DateOnly(2028, 1, 31),
                new DateOnly(2028, 2, 29),
                new DateOnly(2028, 3, 31)
            ],
            plan.Installments
                .Select(installment => installment.DueDate)
                .ToArray());
    }

    [Fact]
    public void Constructor_OneCentSingleInstallment_PreservesExactAmount()
    {
        var plan = new ClientPaymentPlan(
            OrganizationId,
            ClientId,
            0.01m,
            1,
            new DateOnly(2026, 9, 10),
            CreatedAt);

        PaymentInstallment installment = Assert.Single(plan.Installments);
        Assert.Equal(0.01m, installment.Amount);
    }

    [Fact]
    public void Constructor_EmptyOrganizationId_Rejects()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new ClientPaymentPlan(
                Guid.Empty,
                ClientId,
                100m,
                1,
                new DateOnly(2026, 9, 10),
                CreatedAt));

        Assert.Equal("organizationId", exception.ParamName);
    }

    [Fact]
    public void Constructor_EmptyClientId_Rejects()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new ClientPaymentPlan(
                OrganizationId,
                Guid.Empty,
                100m,
                1,
                new DateOnly(2026, 9, 10),
                CreatedAt));

        Assert.Equal("clientId", exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveAmount_Rejects(decimal amount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ClientPaymentPlan(
                OrganizationId,
                ClientId,
                amount,
                1,
                new DateOnly(2026, 9, 10),
                CreatedAt));
    }

    [Fact]
    public void Constructor_AmountWithMoreThanTwoDecimalPlaces_Rejects()
    {
        Assert.Throws<ArgumentException>(
            () => new ClientPaymentPlan(
                OrganizationId,
                ClientId,
                10.001m,
                1,
                new DateOnly(2026, 9, 10),
                CreatedAt));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(121)]
    public void Constructor_InvalidInstallmentCount_Rejects(int installmentCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ClientPaymentPlan(
                OrganizationId,
                ClientId,
                100m,
                installmentCount,
                new DateOnly(2026, 9, 10),
                CreatedAt));
    }

    [Fact]
    public void Constructor_MoreInstallmentsThanAvailableCents_Rejects()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ClientPaymentPlan(
                OrganizationId,
                ClientId,
                0.02m,
                3,
                new DateOnly(2026, 9, 10),
                CreatedAt));
    }

    [Fact]
    public void Constructor_MinimumDueDate_Rejects()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ClientPaymentPlan(
                OrganizationId,
                ClientId,
                100m,
                1,
                DateOnly.MinValue,
                CreatedAt));
    }

    [Fact]
    public void Constructor_MinimumCreatedAt_Rejects()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ClientPaymentPlan(
                OrganizationId,
                ClientId,
                100m,
                1,
                new DateOnly(2026, 9, 10),
                DateTimeOffset.MinValue));
    }

    [Fact]
    public void Constructor_MaximumSupportedAmount_Accepts()
    {
        const decimal maximumAmount = 9_999_999_999_999_999.99m;

        var plan = new ClientPaymentPlan(
            OrganizationId,
            ClientId,
            maximumAmount,
            1,
            new DateOnly(2026, 9, 10),
            CreatedAt);

        Assert.Equal(maximumAmount, plan.TotalAmount);

        PaymentInstallment installment = Assert.Single(plan.Installments);
        Assert.Equal(maximumAmount, installment.Amount);
    }

    [Fact]
    public void Constructor_AmountAboveMaximumSupportedRange_Rejects()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ClientPaymentPlan(
                OrganizationId,
                ClientId,
                10_000_000_000_000_000m,
                1,
                new DateOnly(2026, 9, 10),
                CreatedAt));
    }

    [Fact]
    public void Constructor_ScheduleBeyondDateOnlyRange_Rejects()
    {
        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new ClientPaymentPlan(
                    OrganizationId,
                    ClientId,
                    2m,
                    2,
                    DateOnly.MaxValue,
                    CreatedAt));

        Assert.Equal("firstDueDate", exception.ParamName);
        Assert.Contains(
            FinanceErrors.ScheduleOutOfRange,
            exception.Message);
    }

    [Fact]
    public void Constructor_InstallmentIds_AreUnique()
    {
        var plan = new ClientPaymentPlan(
            OrganizationId,
            ClientId,
            1200m,
            12,
            new DateOnly(2026, 9, 10),
            CreatedAt);

        Assert.Equal(
            plan.Installments.Count,
            plan.Installments
                .Select(installment => installment.Id)
                .Distinct()
                .Count());
    }
}