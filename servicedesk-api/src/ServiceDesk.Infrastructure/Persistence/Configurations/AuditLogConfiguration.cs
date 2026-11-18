using ServiceDesk.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ServiceDesk.Infrastructure.Persistence.Configurations;

public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLogs");
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.Action).HasConversion<string>().HasMaxLength(30);
        builder.Property(l => l.StatusBefore).HasConversion<string>().HasMaxLength(30);
        builder.Property(l => l.StatusAfter).HasConversion<string>().HasMaxLength(30);
        builder.Property(l => l.Details).HasMaxLength(AuditLog.DetailsMaxLength);
        builder.HasIndex(l => new { l.ServiceRequestId, l.Sequence }).IsUnique(); // Also serves lookups by ServiceRequestId alone.

        builder.HasOne<User>().WithMany().HasForeignKey(l => l.ActorId).OnDelete(DeleteBehavior.Restrict);
    }
}
