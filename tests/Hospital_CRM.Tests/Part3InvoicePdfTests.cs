using System.Security.Claims;
using System.Text;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Api.Services;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Hospital_CRM.Tests;

public class Part3InvoicePdfTests
{
    [Fact]
    public async Task UnsupportedInvoiceTextDoesNotConsumeAnOriginalCopy()
    {
        await using var f = await Fixture.Create();
        Assert.IsType<UnprocessableEntityObjectResult>(await f.Controller.GetInvoicePdf(f.Invoice.Id, new EncodingFailure(), default));
        Assert.Empty(f.Db.InvoicePrintLogs);
        Assert.Equal(0, f.Invoice.PrintCount);
        var result = Assert.IsType<FileContentResult>(await f.Controller.GetInvoicePdf(f.Invoice.Id, new PdfService(), default));
        Assert.Contains("ORIGINAL", result.FileDownloadName);
        Assert.Equal(1, (await f.Db.InvoicePrintLogs.SingleAsync()).CopyNumber);
    }

    [Fact]
    public async Task JsonAndPdfPrintsShareSavedHistoricalCopyNumbers()
    {
        await using var f = await Fixture.Create();
        f.Db.InvoicePrintLogs.Add(new InvoicePrintLog { TenantId = f.Invoice.TenantId, InvoiceId = f.Invoice.Id,
            CopyNumber = 4, Watermark = "DUPLICATE COPY", PrintedByUserId = f.User });
        await f.Db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await f.Controller.PrintInvoice(f.Invoice.Id, default));
        Assert.Equal(5, f.Invoice.PrintCount);
        var result = Assert.IsType<FileContentResult>(await f.Controller.GetInvoicePdf(f.Invoice.Id, new PdfService(), default));
        Assert.Contains("DUPLICATE", result.FileDownloadName);
        Assert.Equal(new[] { 4, 5, 6 }, await f.Db.InvoicePrintLogs.OrderBy(p => p.CopyNumber).Select(p => p.CopyNumber).ToArrayAsync());
    }

    [Fact]
    public async Task ConcurrentInvoiceChangeRejectsGeneratedPdf()
    {
        await using var f = await Fixture.Create();
        await using var competing = new HospitalCrmDbContext(f.Options, TestMaintenanceTenantContext.Instance);
        var changed = await competing.Invoices.SingleAsync(i => i.Id == f.Invoice.Id);
        changed.UpdatedAt = changed.UpdatedAt.AddMinutes(1);
        await competing.SaveChangesAsync();
        Assert.IsType<ConflictObjectResult>(await f.Controller.GetInvoicePdf(f.Invoice.Id, new PdfService(), default));
        // InMemory cannot prove transaction rollback; PostgreSQL acceptance checks the append-only log.
        Assert.Equal(changed.UpdatedAt, (await competing.Invoices.AsNoTracking().SingleAsync()).UpdatedAt);
    }

    [Theory]
    [InlineData("Nurse")]
    [InlineData("PlatformAdmin")]
    [InlineData("Patient")]
    public async Task NonBillingRolesCannotDownloadEvenWithMatchingClaims(string role)
    {
        await using var f = await Fixture.Create();
        f.SetRole(role);
        Assert.IsType<ForbidResult>(await f.Controller.GetInvoicePdf(f.Invoice.Id, new PdfService(), default));
        Assert.IsType<ForbidResult>(await f.Controller.GetCreditNotePdf(f.Note.Id, new PdfService(), default));
        Assert.Empty(f.Db.InvoicePrintLogs);
    }

    [Fact]
    public async Task InactiveMembershipCannotDownload()
    {
        await using var f = await Fixture.Create();
        (await f.Db.TenantMemberships.SingleAsync()).Status = "inactive";
        await f.Db.SaveChangesAsync();
        Assert.IsType<ForbidResult>(await f.Controller.GetInvoicePdf(f.Invoice.Id, new PdfService(), default));
        Assert.IsType<ForbidResult>(await f.Controller.GetCreditNotePdf(f.Note.Id, new PdfService(), default));
    }

    [Fact]
    public async Task ForeignTenantAndCrossLinkedInvoiceAreNotExposed()
    {
        await using var f = await Fixture.Create();
        f.Invoice.TenantId = Guid.NewGuid();
        await f.Db.SaveChangesAsync();
        Assert.IsType<NotFoundObjectResult>(await f.Controller.GetInvoicePdf(f.Invoice.Id, new PdfService(), default));
        Assert.IsType<NotFoundObjectResult>(await f.Controller.GetCreditNotePdf(f.Note.Id, new PdfService(), default));
        Assert.Empty(f.Db.InvoicePrintLogs);
    }

    [Fact]
    public async Task ForeignClinicAppointmentIsNotExposed()
    {
        await using var f = await Fixture.Create();
        f.Invoice.Appointment = new Appointment { Id = Guid.NewGuid(), TenantId = f.Invoice.TenantId, ClinicId = Guid.NewGuid(), TimeSlot = "10:00" };
        f.Db.Appointments.Add(f.Invoice.Appointment);
        await f.Db.SaveChangesAsync();
        Assert.IsType<NotFoundObjectResult>(await f.Controller.GetInvoicePdf(f.Invoice.Id, new PdfService(), default));
        Assert.IsType<NotFoundObjectResult>(await f.Controller.GetCreditNotePdf(f.Note.Id, new PdfService(), default));
    }

    [Fact]
    public async Task PharmacistCanReadOnlyPharmacyDocuments()
    {
        await using var f = await Fixture.Create();
        f.SetRole("Pharmacist");
        (await f.Db.TenantMemberships.SingleAsync()).Role = UserRole.Pharmacist;
        await f.Db.SaveChangesAsync();
        Assert.IsType<ForbidResult>(await f.Controller.GetInvoicePdf(f.Invoice.Id, new PdfService(), default));
        Assert.IsType<ForbidResult>(await f.Controller.GetCreditNotePdf(f.Note.Id, new PdfService(), default));
        f.Invoice.InvoiceType = InvoiceType.Pharmacy;
        await f.Db.SaveChangesAsync();
        Assert.IsType<FileContentResult>(await f.Controller.GetInvoicePdf(f.Invoice.Id, new PdfService(), default));
        Assert.IsType<FileContentResult>(await f.Controller.GetCreditNotePdf(f.Note.Id, new PdfService(), default));
    }

    [Fact]
    public async Task UnsupportedCreditNoteTextReturnsStructuredFailure()
    {
        await using var f = await Fixture.Create();
        var result = Assert.IsType<UnprocessableEntityObjectResult>(await f.Controller.GetCreditNotePdf(f.Note.Id, new EncodingFailure(), default));
        Assert.Contains("credit_note_pdf_font_unsupported", System.Text.Json.JsonSerializer.Serialize(result.Value));
        Assert.Empty(f.Db.InvoicePrintLogs);
    }

    private sealed class EncodingFailure : IPdfService
    {
        public byte[] GeneratePrescriptionPdf(Prescription prescription, Consultation consultation, Clinic? clinic, bool isDuplicate) => throw new EncoderFallbackException();
        public byte[] GenerateInvoicePdf(Invoice invoice, Clinic? clinic, bool isDuplicate, string? supplierGstin = null) => throw new EncoderFallbackException();
        public byte[] GenerateCreditNotePdf(CreditNote creditNote, Invoice invoice, Clinic? clinic, string? supplierGstin = null) => throw new EncoderFallbackException();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly Guid User = Guid.NewGuid();
        internal readonly Guid Clinic = Guid.NewGuid();
        internal readonly DbContextOptions<HospitalCrmDbContext> Options = new DbContextOptionsBuilder<HospitalCrmDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        internal HospitalCrmDbContext Db = null!;
        internal InvoicesController Controller = null!;
        internal Invoice Invoice = null!;
        internal CreditNote Note = null!;

        internal static async Task<Fixture> Create()
        {
            var f = new Fixture();
            f.Db = new HospitalCrmDbContext(f.Options, TestMaintenanceTenantContext.Instance);
            f.Invoice = new Invoice { TenantId = Guid.NewGuid(), InvoiceNumber = 1, Total = 100m, BalanceDue = 100m,
                Status = InvoiceStatus.Finalized, LineItems = [new InvoiceLineItem { Description = "Synthetic service", Amount = 100m, UnitPrice = 100m, Quantity = 1 }] };
            f.Note = new CreditNote { TenantId = f.Invoice.TenantId, Invoice = f.Invoice, Amount = 10m, Status = CreditNoteStatus.Approved, Reason = "Synthetic correction" };
            f.Db.Clinics.Add(new Clinic { Id = f.Clinic, TenantId = f.Invoice.TenantId, Name = "Synthetic clinic", Address = "Test", Phone = "9876543210" });
            f.Db.TenantMemberships.Add(new TenantMembership { TenantId = f.Invoice.TenantId, ClinicId = f.Clinic, UserId = f.User, Role = UserRole.ClinicAdmin, Status = "active" });
            f.Db.CreditNotes.Add(f.Note);
            await f.Db.SaveChangesAsync();
            f.Controller = new InvoicesController(f.Db, new ConfigurationBuilder().Build(), null!);
            f.SetRole("ClinicAdmin");
            return f;
        }

        internal void SetRole(string role) => Controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("sub", User.ToString()), new Claim("tenant_id", Invoice.TenantId.ToString()),
                new Claim("clinicId", Clinic.ToString()), new Claim(ClaimTypes.Role, role)], "Test")) }
        };
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
