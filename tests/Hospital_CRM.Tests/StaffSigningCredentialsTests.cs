using System.Security.Claims;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hospital_CRM.Tests;

public class StaffSigningCredentialsTests
{
    private HospitalCrmDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new HospitalCrmDbContext(options, TestMaintenanceTenantContext.Instance);
    }

    private StaffController CreateController(HospitalCrmDbContext db, Guid adminId, Guid tenantId, Guid clinicId)
    {
        var controller = new StaffController(db, TestMaintenanceTenantContext.Instance,
            NullLogger<StaffController>.Instance);
        var claims = new[]
        {
            new Claim("sub", adminId.ToString()),
            new Claim("role", "ClinicAdmin"),
            new Claim("tenant_id", tenantId.ToString()),
            new Claim("clinicId", clinicId.ToString())
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
            }
        };
        return controller;
    }

    private (HospitalCrmDbContext Db, Guid TenantId, Guid ClinicId, Guid AdminId, Guid DoctorId) Seed()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var clinicId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var doctorId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Synthetic clinic tenant" });
        db.Clinics.Add(new Clinic { Id = clinicId, TenantId = tenantId, Name = "Synthetic clinic" });
        db.Users.AddRange(
            new User { Id = adminId, Name = "Admin", Role = UserRole.ClinicAdmin, TenantId = tenantId, ClinicId = clinicId, Email = "admin@test.ai", PasswordHash = "h" },
            new User { Id = doctorId, Name = "Dr. Test", Role = UserRole.Doctor, TenantId = tenantId, ClinicId = clinicId, Email = "dr@test.ai", PasswordHash = "h" });
        db.TenantMemberships.AddRange(
            new TenantMembership { UserId = adminId, TenantId = tenantId, ClinicId = clinicId, Role = UserRole.ClinicAdmin, Status = "active" },
            new TenantMembership { UserId = doctorId, TenantId = tenantId, ClinicId = clinicId, Role = UserRole.Doctor, Status = "active" });
        db.SaveChanges();
        return (db, tenantId, clinicId, adminId, doctorId);
    }

    [Fact]
    public async Task ClinicAdmin_SetsSigningCredentials()
    {
        var (db, tenantId, clinicId, adminId, doctorId) = Seed();
        var controller = CreateController(db, adminId, tenantId, clinicId);

        var result = await controller.UpdateSigningCredentials(doctorId,
            new SigningCredentialsRequest("KMC-123456", "MBBS, MD"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var membership = await db.TenantMemberships.SingleAsync(m => m.UserId == doctorId);
        Assert.Equal("KMC-123456", membership.RegistrationNumber);
        Assert.Equal("MBBS, MD", membership.Qualifications);
        var audit = await db.AuthAuditLogs.SingleAsync();
        Assert.Equal("signing_credentials_updated", audit.EventType);
        Assert.Equal(adminId, audit.UserId);
        Assert.Equal(tenantId, audit.TenantId);
    }

    [Fact]
    public async Task BlankCredentials_AreRejected()
    {
        var (db, tenantId, clinicId, adminId, doctorId) = Seed();
        var controller = CreateController(db, adminId, tenantId, clinicId);

        var result = await controller.UpdateSigningCredentials(doctorId,
            new SigningCredentialsRequest("   ", "MBBS"), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        var membership = await db.TenantMemberships.SingleAsync(m => m.UserId == doctorId);
        Assert.Null(membership.RegistrationNumber);
    }

    [Fact]
    public async Task NonDoctorMembership_IsNotWritable()
    {
        var (db, tenantId, clinicId, adminId, _) = Seed();
        var nurseId = Guid.NewGuid();
        db.Users.Add(new User { Id = nurseId, Name = "Nurse", Role = UserRole.Nurse, TenantId = tenantId, ClinicId = clinicId, Email = "nurse@test.ai", PasswordHash = "h" });
        db.TenantMemberships.Add(new TenantMembership { UserId = nurseId, TenantId = tenantId, ClinicId = clinicId, Role = UserRole.Nurse, Status = "active" });
        await db.SaveChangesAsync();
        var controller = CreateController(db, adminId, tenantId, clinicId);

        var result = await controller.UpdateSigningCredentials(nurseId,
            new SigningCredentialsRequest("KMC-123456", "MBBS"), CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task AuditSaveFailure_DoesNotReportCredentialsAsSaved()
    {
        var (db, tenantId, clinicId, adminId, doctorId) = Seed();
        var controller = CreateController(db, adminId, tenantId, clinicId);
        db.SavingChanges += (_, _) => throw new DbUpdateException("Simulated audit persistence failure");

        await Assert.ThrowsAsync<DbUpdateException>(() => controller.UpdateSigningCredentials(doctorId,
            new SigningCredentialsRequest("KMC-123456", "MBBS"), CancellationToken.None));
        db.ChangeTracker.Clear();
        Assert.Null((await db.TenantMemberships.SingleAsync(m => m.UserId == doctorId)).RegistrationNumber);
        Assert.Empty(db.AuthAuditLogs);
    }
}
