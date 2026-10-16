using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using ShiftHandover.Web.Models;

namespace ShiftHandover.Web.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
	: IdentityDbContext<ApplicationUser, IdentityRole, string>(options)
{
	public DbSet<Shift> Shifts => Set<Shift>();
	public DbSet<Accident> Accidents => Set<Accident>();
	public DbSet<Incident> Incidents => Set<Incident>();
	public DbSet<ManpowerRecord> ManpowerRecords => Set<ManpowerRecord>();
	public DbSet<ShiftReportDelivery> ShiftReportDeliveries => Set<ShiftReportDelivery>();

	protected override void OnModelCreating(ModelBuilder builder)
	{
		base.OnModelCreating(builder);

		builder.Entity<ApplicationUser>(entity =>
		{
			entity.Property(user => user.FullName).HasMaxLength(160).IsRequired();
		});

		builder.Entity<Shift>(entity =>
		{
			entity.ToTable(table => table.HasCheckConstraint(
				"CK_Shifts_StatusClaimState",
				"([Status] = 'Available' AND [ClaimedBySupervisorId] IS NULL AND [ClaimedAt] IS NULL AND [ClosedAt] IS NULL) OR " +
				"([Status] = 'Claimed' AND [ClaimedBySupervisorId] IS NOT NULL AND [ClaimedAt] IS NOT NULL AND [ClosedAt] IS NULL) OR " +
				"([Status] = 'Closed' AND [ClaimedBySupervisorId] IS NOT NULL AND [ClaimedAt] IS NOT NULL AND [ClosedAt] IS NOT NULL)"));
			entity.Property(shift => shift.Title).HasMaxLength(120).IsRequired();
			entity.Property(shift => shift.Status).HasConversion<string>().HasMaxLength(20);
			entity.Property(shift => shift.RowVersion).IsRowVersion();
			entity.HasIndex(shift => new { shift.Status, shift.ShiftDate });
			entity.HasOne(shift => shift.ClaimedBySupervisor)
				.WithMany()
				.HasForeignKey(shift => shift.ClaimedBySupervisorId)
				.OnDelete(DeleteBehavior.Restrict);
		});

		builder.Entity<Accident>(entity =>
		{
			entity.Property(accident => accident.Location).HasMaxLength(160).IsRequired();
			entity.Property(accident => accident.Description).HasMaxLength(2000).IsRequired();
			entity.Property(accident => accident.Severity).HasConversion<string>().HasMaxLength(20);
			entity.Property(accident => accident.InjuredPerson).HasMaxLength(160);
			entity.Property(accident => accident.ActionTaken).HasMaxLength(2000);
			entity.HasIndex(accident => new { accident.ShiftId, accident.OccurredAt });
			entity.HasOne(accident => accident.Shift)
				.WithMany(shift => shift.Accidents)
				.HasForeignKey(accident => accident.ShiftId)
				.OnDelete(DeleteBehavior.Restrict);
			entity.HasOne(accident => accident.RecordedBySupervisor)
				.WithMany()
				.HasForeignKey(accident => accident.RecordedBySupervisorId)
				.OnDelete(DeleteBehavior.Restrict);
		});

		builder.Entity<Incident>(entity =>
		{
			entity.Property(incident => incident.Category).HasMaxLength(120).IsRequired();
			entity.Property(incident => incident.Description).HasMaxLength(2000).IsRequired();
			entity.HasIndex(incident => new { incident.ShiftId, incident.OccurredAt });
			entity.HasOne(incident => incident.Shift)
				.WithMany(shift => shift.Incidents)
				.HasForeignKey(incident => incident.ShiftId)
				.OnDelete(DeleteBehavior.Restrict);
			entity.HasOne(incident => incident.RecordedBySupervisor)
				.WithMany()
				.HasForeignKey(incident => incident.RecordedBySupervisorId)
				.OnDelete(DeleteBehavior.Restrict);
		});

		builder.Entity<ManpowerRecord>(entity =>
		{
			entity.Property(record => record.Department).HasMaxLength(120).IsRequired();
			entity.Property(record => record.Role).HasMaxLength(120).IsRequired();
			entity.Property(record => record.Notes).HasMaxLength(1000);
			entity.HasIndex(record => record.ShiftId);
			entity.ToTable(table => table.HasCheckConstraint(
				"CK_ManpowerRecords_NonNegativeCounts",
				"[Required] >= 0 AND [Present] >= 0 AND [Absent] >= 0"));
			entity.HasOne(record => record.Shift)
				.WithMany(shift => shift.ManpowerRecords)
				.HasForeignKey(record => record.ShiftId)
				.OnDelete(DeleteBehavior.Restrict);
			entity.HasOne(record => record.RecordedBySupervisor)
				.WithMany()
				.HasForeignKey(record => record.RecordedBySupervisorId)
				.OnDelete(DeleteBehavior.Restrict);
		});

		builder.Entity<ShiftReportDelivery>(entity =>
		{
			entity.Property(delivery => delivery.Status).HasConversion<string>().HasMaxLength(20);
			entity.Property(delivery => delivery.FailureReason).HasMaxLength(1000);
			entity.HasIndex(delivery => delivery.ShiftId).IsUnique();
			entity.HasOne(delivery => delivery.Shift)
				.WithOne(shift => shift.ReportDelivery)
				.HasForeignKey<ShiftReportDelivery>(delivery => delivery.ShiftId)
				.OnDelete(DeleteBehavior.Restrict);
		});
	}
}
