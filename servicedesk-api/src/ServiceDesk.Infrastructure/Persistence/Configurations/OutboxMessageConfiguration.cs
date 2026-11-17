using ServiceDesk.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ServiceDesk.Infrastructure.Persistence.Configurations;

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages");
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.Type).IsRequired().HasMaxLength(OutboxMessage.TypeMaxLength);
        builder.Property(m => m.Body).IsRequired(); // text: the whole envelope JSON.

        // The publisher polls for unsent rows oldest first.
        builder.HasIndex(m => m.OccurredAt).HasFilter("\"SentAt\" IS NULL");
    }
}
