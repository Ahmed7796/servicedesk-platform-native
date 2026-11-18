using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ServiceDesk.Domain.Entities;

namespace ServiceDesk.Infrastructure.Persistence.Configurations;

public sealed class TriageSuggestionConfiguration : IEntityTypeConfiguration<TriageSuggestion>
{
    public void Configure(EntityTypeBuilder<TriageSuggestion> builder)
    {
        builder.ToTable("TriageSuggestions");
        builder.HasKey(t => t.RequestId);
        builder.Property(t => t.RequestId).ValueGeneratedNever();
        builder.Property(t => t.Category).HasConversion<string>().HasMaxLength(30);
        builder.Property(t => t.Priority).HasConversion<string>().HasMaxLength(30);
        // An ordered list of position names, stored as a PostgreSQL text[] column.
        builder.PrimitiveCollection(t => t.SuggestedRoute).ElementType().HasConversion<string>();
        builder.Property(t => t.Summary).HasMaxLength(TriageSuggestion.SummaryMaxLength);

        // Same key as the request: one suggestion per request, removed together with it.
        builder.HasOne<ServiceRequest>().WithOne().HasForeignKey<TriageSuggestion>(t => t.RequestId).OnDelete(DeleteBehavior.Cascade);
    }
}
