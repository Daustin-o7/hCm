using System.Security.Claims;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Utils;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Hospital_CRM.Tests;

public class Section5IndianFyNumberingTests
{
    private static HospitalCrmDbContext CreateInMemoryDb(string? dbName = null)
    {
        var options = new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase(databaseName: dbName ?? Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new HospitalCrmDbContext(options, TestMaintenanceTenantContext.Instance);
    }

    private static InvoicesController CreateController(HospitalCrmDbContext db, Guid tenantId, Guid userId, string role = "ClinicAdmin")
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var controller = new InvoicesController(db, config, null!);

        var identity = new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim("sub", userId.ToString()),
            new Claim(ClaimTypes.Role, role)
        }, "TestAuth");

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };

        return controller;
    }

    [Fact]
    public void IndianFinancialYearUtils_ComputesCorrectFiscalYear_AcrossAprilBoundary()
    {
        // On or after April 1: Start year is current calendar year
        Assert.Equal("2026-27", IndianFinancialYearUtils.GetFinancialYear(new DateOnly(2026, 4, 1)));
        Assert.Equal("2026-27", IndianFinancialYearUtils.GetFinancialYear(new DateOnly(2026, 10, 26)));
        Assert.Equal("2026-27", IndianFinancialYearUtils.GetFinancialYear(new DateOnly(2026, 12, 31)));

        // Before April 1: Belongs to previous fiscal year
        Assert.Equal("2026-27", IndianFinancialYearUtils.GetFinancialYear(new DateOnly(2027, 1, 1)));
        Assert.Equal("2026-27", IndianFinancialYearUtils.GetFinancialYear(new DateOnly(2027, 3, 31)));

        // On April 1: Next fiscal year begins and sequence resets
        Assert.Equal("2027-28", IndianFinancialYearUtils.GetFinancialYear(new DateOnly(2027, 4, 1)));
    }

    [Fact]
    public async Task FinalizeInvoice_AssignsIndianFyAndGaplessSequence_PerTenantAndSeries()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var doctorId = Guid.NewGuid();
        var currentFy = IndianFinancialYearUtils.GetFinancialYear(DateTimeOffset.UtcNow);

        var patient = new Patient { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Asha Sharma", Phone = "9876543210" };
        var appointment1 = new Appointment { Id = Guid.NewGuid(), TenantId = tenantId, PatientId = patient.Id, DoctorId = doctorId, Status = AppointmentStatus.Completed };
        var appointment2 = new Appointment { Id = Guid.NewGuid(), TenantId = tenantId, PatientId = patient.Id, DoctorId = doctorId, Status = AppointmentStatus.Completed };
        db.Patients.Add(patient);
        db.Appointments.AddRange(appointment1, appointment2);

        var inv1 = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AppointmentId = appointment1.Id,
            FinancialYear = currentFy,
            Series = "INV",
            Status = InvoiceStatus.Draft,
            Subtotal = 500m,
            Total = 500m,
            BalanceDue = 500m,
            LineItems = [new() { Description = "Consultation", Quantity = 1, UnitPrice = 500m, Amount = 500m }]
        };
        var inv2 = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AppointmentId = appointment2.Id,
            FinancialYear = currentFy,
            Series = "INV",
            Status = InvoiceStatus.Draft,
            Subtotal = 750m,
            Total = 750m,
            BalanceDue = 750m,
            LineItems = [new() { Description = "Consultation 2", Quantity = 1, UnitPrice = 750m, Amount = 750m }]
        };
        db.Invoices.AddRange(inv1, inv2);
        await db.SaveChangesAsync();

        var controller = CreateController(db, tenantId, doctorId);

        // Finalize invoice 1 -> Sequence 1
        var res1 = await controller.FinalizeInvoice(inv1.Id, CancellationToken.None);
        var ok1 = Assert.IsType<OkObjectResult>(res1);
        Assert.Equal(1, inv1.InvoiceNumber);
        Assert.Equal(currentFy, inv1.FinancialYear);
        Assert.Equal($"INV-{currentFy}-000001", inv1.FormatInvoiceNumber());

        // Finalize invoice 2 -> Sequence 2 (gapless)
        var res2 = await controller.FinalizeInvoice(inv2.Id, CancellationToken.None);
        var ok2 = Assert.IsType<OkObjectResult>(res2);
        Assert.Equal(2, inv2.InvoiceNumber);
        Assert.Equal(currentFy, inv2.FinancialYear);
        Assert.Equal($"INV-{currentFy}-000002", inv2.FormatInvoiceNumber());
    }

    [Fact]
    public async Task CreateCreditNote_AssignsIndianFyAndGaplessSequence_PerTenantAndSeries()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var currentFy = IndianFinancialYearUtils.GetFinancialYear(DateTimeOffset.UtcNow);

        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            FinancialYear = currentFy,
            Series = "INV",
            InvoiceNumber = 1,
            Status = InvoiceStatus.Finalized,
            Total = 1000m,
            PaidAmount = 1000m,
            BalanceDue = 0m
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var controller = CreateController(db, tenantId, adminId);

        // First credit note -> Sequence 1
        var req1 = new CreateCreditNoteRequest(200m, "Partial Refund / Service Adjustment");
        var res1 = await controller.CreateCreditNote(invoice.Id, req1, CancellationToken.None);
        var obj1 = Assert.IsType<ObjectResult>(res1);
        Assert.Equal(201, obj1.StatusCode);

        var cn1 = await db.CreditNotes.FirstOrDefaultAsync(c => c.InvoiceId == invoice.Id && c.SequenceNumber == 1);
        Assert.NotNull(cn1);
        Assert.Equal("CN", cn1.Series);
        Assert.Equal(currentFy, cn1.FinancialYear);
        Assert.Equal(1, cn1.SequenceNumber);
        Assert.Equal($"CN-{currentFy}-000001", cn1.CreditNoteNumber);

        // Second credit note -> Sequence 2
        var req2 = new CreateCreditNoteRequest(300m, "Second adjustment");
        var res2 = await controller.CreateCreditNote(invoice.Id, req2, CancellationToken.None);
        var obj2 = Assert.IsType<ObjectResult>(res2);
        Assert.Equal(201, obj2.StatusCode);

        var cn2 = await db.CreditNotes.FirstOrDefaultAsync(c => c.InvoiceId == invoice.Id && c.SequenceNumber == 2);
        Assert.NotNull(cn2);
        Assert.Equal("CN", cn2.Series);
        Assert.Equal(currentFy, cn2.FinancialYear);
        Assert.Equal(2, cn2.SequenceNumber);
        Assert.Equal($"CN-{currentFy}-000002", cn2.CreditNoteNumber);
    }

    [Fact]
    public async Task FinancialYearTransition_ResetsSequenceNumber_ToStartFromOne()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var doctorId = Guid.NewGuid();

        // Seed previous FY (2025-26) with sequence up to 45
        var pastFy = "2025-26";
        db.Invoices.Add(new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            FinancialYear = pastFy,
            Series = "INV",
            InvoiceNumber = 45,
            Status = InvoiceStatus.Finalized,
            Total = 500m
        });

        // Seed previous FY credit note with sequence up to 10
        db.CreditNotes.Add(new CreditNote
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            InvoiceId = Guid.NewGuid(),
            FinancialYear = pastFy,
            Series = "CN",
            SequenceNumber = 10,
            CreditNoteNumber = $"CN-{pastFy}-000010",
            Amount = 100m
        });
        await db.SaveChangesAsync();

        var currentFy = IndianFinancialYearUtils.GetFinancialYear(DateTimeOffset.UtcNow);

        // In new FY, creating a new invoice should start at 1, not 46
        var patient = new Patient { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Ramesh Kumar" };
        var appointment = new Appointment { Id = Guid.NewGuid(), TenantId = tenantId, PatientId = patient.Id, DoctorId = doctorId, Status = AppointmentStatus.Completed };
        db.Patients.Add(patient);
        db.Appointments.Add(appointment);

        var newInv = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AppointmentId = appointment.Id,
            FinancialYear = currentFy,
            Series = "INV",
            Status = InvoiceStatus.Draft,
            Total = 600m,
            BalanceDue = 600m,
            LineItems = [new() { Description = "Test", Quantity = 1, UnitPrice = 600m, Amount = 600m }]
        };
        db.Invoices.Add(newInv);
        await db.SaveChangesAsync();

        var controller = CreateController(db, tenantId, doctorId);
        var finalizeRes = await controller.FinalizeInvoice(newInv.Id, CancellationToken.None);
        Assert.IsType<OkObjectResult>(finalizeRes);

        // Sequence must reset to 1 in the new FY
        Assert.Equal(1, newInv.InvoiceNumber);
        Assert.Equal($"INV-{currentFy}-000001", newInv.FormatInvoiceNumber());

        // Now issue credit note against newInv in new FY -> Must start at 1, not 11
        var cnRes = await controller.CreateCreditNote(newInv.Id, new CreateCreditNoteRequest(100m, "Test Reset"), CancellationToken.None);
        var cnObj = Assert.IsType<ObjectResult>(cnRes);
        Assert.Equal(201, cnObj.StatusCode);

        var newCn = await db.CreditNotes.FirstOrDefaultAsync(c => c.InvoiceId == newInv.Id);
        Assert.NotNull(newCn);
        Assert.Equal(1, newCn.SequenceNumber);
        Assert.Equal($"CN-{currentFy}-000001", newCn.CreditNoteNumber);
    }
}
