using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enma.Infrastructure.Persistence.Configurations;

public sealed class EmailVerificationSendBudgetConfiguration
    : IEntityTypeConfiguration<EmailVerificationSendBudget>
{
    public void Configure(EntityTypeBuilder<EmailVerificationSendBudget> builder)
    {
        builder.ToTable(
            "email_verification_send_budgets",
            tableBuilder =>
            {
                tableBuilder.HasCheckConstraint(
                    "ck_email_verification_send_budgets_scope",
                    "scope IN (1, 2)");
                tableBuilder.HasCheckConstraint(
                    "ck_email_verification_send_budgets_key_hash_length",
                    "octet_length(key_hash) = 32");
                tableBuilder.HasCheckConstraint(
                    "ck_email_verification_send_budgets_used",
                    "used > 0");
                tableBuilder.HasCheckConstraint(
                    "ck_email_verification_send_budgets_window_start",
                    "isfinite(window_start)");
            });

        builder.HasKey(budget => new { budget.Scope, budget.KeyHash })
            .HasName("pk_email_verification_send_budgets");

        builder.Property(budget => budget.Scope)
            .HasColumnName("scope")
            .HasColumnType("smallint")
            .IsRequired()
            .ValueGeneratedNever();

        builder.Property(budget => budget.KeyHash)
            .HasColumnName("key_hash")
            .HasColumnType("bytea")
            .IsRequired()
            .ValueGeneratedNever();

        builder.Property(budget => budget.WindowStart)
            .HasColumnName("window_start")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(budget => budget.Used)
            .HasColumnName("used")
            .HasColumnType("integer")
            .IsRequired();
    }
}
