using Hospital_CRM.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hospital_CRM.Infrastructure.Data;

public sealed class NotificationIntentConfiguration : IEntityTypeConfiguration<NotificationIntent>
{
    public void Configure(EntityTypeBuilder<NotificationIntent> builder)
    {
        builder.ToTable("NotificationIntents");
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => new { x.TenantId, x.AppointmentId }).IsUnique();
        builder.HasIndex(x => new { x.TenantId, x.CreatedAt }).HasFilter("\"HandedOffAt\" IS NULL");
        builder.HasOne(x => x.Appointment).WithMany()
            .HasForeignKey(x => new { x.TenantId, x.AppointmentId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
