using System.Security.Claims;
using System.Text.Json;
using Hospital_CRM.Api.Controllers;
using Hospital_CRM.Domain.Entities;
using Hospital_CRM.Domain.Enums;
using Hospital_CRM.Domain.Utils;
using Hospital_CRM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hospital_CRM.Tests;

public class Section5PharmacyBillingLinkTests
{
    private static JsonDocument ToJsonDoc(object? value) => JsonDocument.Parse(JsonSerializer.Serialize(value));

    private static HospitalCrmDbContext CreateInMemoryDb(string? dbName = null)
    {
        var options = new DbContextOptionsBuilder<HospitalCrmDbContext>()
            .UseInMemoryDatabase(databaseName: dbName ?? Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new HospitalCrmDbContext(options, TestMaintenanceTenantContext.Instance);
    }

    private static ClaimsPrincipal CreatePrincipal(Guid tenantId, Guid userId, Guid clinicId, string role)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim("sub", userId.ToString()),
            new Claim("clinicId", clinicId.ToString()),
            new Claim(ClaimTypes.Role, role)
        }, "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    private static InvoicesController CreateInvoicesController(HospitalCrmDbContext db, Guid tenantId, Guid userId, Guid clinicId, string role)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var controller = new InvoicesController(db, config, null!);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = CreatePrincipal(tenantId, userId, clinicId, role) }
        };
        return controller;
    }

    private static (Clinic clinic, User doctor, User staff, Patient patient, Appointment appointment, Consultation consultation, Prescription rx, Drug drug, DrugBatch batch)
        SeedFullClinicalPharmacySetup(HospitalCrmDbContext db, Guid tenantId, Guid doctorId, Guid staffId, UserRole staffRole)
    {
        var clinic = new Clinic
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "City Health Clinic",
            PharmacyLicenceNumber20 = "DL-20-TEST",
            PharmacyLicenceNumber21 = "DL-21-TEST"
        };
        db.Clinics.Add(clinic);

        var doctor = new User
        {
            Id = doctorId,
            TenantId = tenantId,
            Name = "Dr. Ananya Sharma",
            Role = UserRole.Doctor,
            ClinicId = clinic.Id
        };
        db.Users.Add(doctor);

        var staff = new User
        {
            Id = staffId,
            TenantId = tenantId,
            Name = "Ramesh Kumar",
            Role = staffRole,
            ClinicId = clinic.Id
        };
        db.Users.Add(staff);
        db.TenantMemberships.AddRange(
            new TenantMembership { UserId = doctor.Id, TenantId = tenantId, ClinicId = clinic.Id, Role = UserRole.Doctor },
            new TenantMembership { UserId = staff.Id, TenantId = tenantId, ClinicId = clinic.Id, Role = staffRole });

        var patient = new Patient
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "Aakash Patel",
            Phone = "9876543210"
        };
        db.Patients.Add(patient);

        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClinicId = clinic.Id,
            PatientId = patient.Id,
            DoctorId = doctor.Id,
            Status = AppointmentStatus.Completed
        };
        db.Appointments.Add(appointment);

        var consultation = new Consultation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AppointmentId = appointment.Id,
            DoctorId = doctor.Id,
            DoctorRegistrationNumber = "MCI-45892",
            Status = ConsultationStatus.Finalized,
            SignedAt = DateTimeOffset.UtcNow
        };
        db.Consultations.Add(consultation);

        var rx = new Prescription
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConsultationId = consultation.Id
        };
        db.Prescriptions.Add(rx);

        var drug = new Drug
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "Amoxicillin 500mg",
            GenericName = "Amoxicillin",
            Strength = "500mg",
            DosageForm = "Capsule",
            ScheduleClass = ScheduleClass.General,
            GstRate = 12.0m,
            HsnCode = "30041010"
        };
        db.Drugs.Add(drug);

        var batch = new DrugBatch
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DrugId = drug.Id,
            BatchNumber = "AMX-2026-01",
            ExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(12)),
            Mrp = 100.0m,
            PurchaseRate = 60.0m,
            QuantityReceived = 100,
            QuantityRemaining = 100,
            IsRecalled = false
        };
        db.DrugBatches.Add(batch);

        var rxItem = new PrescriptionItem
        {
            Id = Guid.NewGuid(),
            PrescriptionId = rx.Id,
            DrugId = drug.Id,
            MedicineText = "Amoxicillin 500mg",
            GenericName = "Amoxicillin",
            DosageForm = "Capsule",
            Strength = "500mg",
            Route = "Oral",
            Frequency = "1-1-1",
            Duration = 5,
            DurationUnit = "Days",
            Timing = "After food",
            TotalQuantity = 10,
            DispensedQuantity = 0,
            IsDispensed = false
        };
        rx.Items.Add(rxItem);

        db.SaveChanges();

        return (clinic, doctor, staff, patient, appointment, consultation, rx, drug, batch);
    }

    [Fact]
    public async Task DispensePrescription_Creates_Finalized_Pharm_Invoice_And_AutoLinks_To_Consultation_Invoice()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var doctorId = Guid.NewGuid();
        var pharmacistId = Guid.NewGuid();

        var setup = SeedFullClinicalPharmacySetup(db, tenantId, doctorId, pharmacistId, UserRole.Pharmacist);

        // Pre-create OPD Consultation invoice for appointment
        var consultationInvoice = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AppointmentId = setup.appointment.Id,
            PatientId = setup.patient.Id,
            Series = "INV",
            FinancialYear = IndianFinancialYearUtils.GetFinancialYear(DateTime.UtcNow),
            InvoiceNumber = 1,
            Subtotal = 500m,
            GstAmount = 0m,
            Total = 500m,
            BalanceDue = 500m,
            Status = InvoiceStatus.Finalized
        };
        db.Invoices.Add(consultationInvoice);
        await db.SaveChangesAsync();

        var controller = new PharmacyComplianceController(db, NullLogger<PharmacyComplianceController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = CreatePrincipal(tenantId, pharmacistId, setup.clinic.Id, "Pharmacist") }
        };

        var dispenseReq = new DispensePrescriptionRequest(
            setup.rx.Id,
            PatientName: setup.patient.Name,
            PatientAddress: "Mumbai",
            IdempotencyKey: "dispense-key-001");

        var result = await controller.DispensePrescription(dispenseReq, CancellationToken.None);

        var created = Assert.IsType<ObjectResult>(result);
        Assert.Equal(201, created.StatusCode);

        var doc = ToJsonDoc(created.Value);
        var invoiceId = doc.RootElement.GetProperty("invoiceId").GetGuid();
        var invoiceNumber = doc.RootElement.GetProperty("invoiceNumber").GetString();
        var totalAmount = doc.RootElement.GetProperty("totalAmount").GetDecimal();
        var balanceDue = doc.RootElement.GetProperty("balanceDue").GetDecimal();

        Assert.StartsWith("PHARM-", invoiceNumber);
        Assert.Equal(1000m, totalAmount); // 10 capsules @ MRP 100
        Assert.Equal(1000m, balanceDue);

        // Verify PHARM invoice created in DB
        var pharmInvoice = await db.Invoices.Include(i => i.LineItems).FirstOrDefaultAsync(i => i.Id == invoiceId);
        Assert.NotNull(pharmInvoice);
        Assert.Equal("PHARM", pharmInvoice.Series);
        Assert.Equal(InvoiceType.Pharmacy, pharmInvoice.InvoiceType);
        Assert.Equal(InvoiceStatus.Finalized, pharmInvoice.Status);
        Assert.Equal(setup.appointment.Id, pharmInvoice.AppointmentId);
        Assert.Equal(setup.patient.Id, pharmInvoice.PatientId);
        Assert.Single(pharmInvoice.LineItems);

        // Verify consultation invoice is automatically linked
        var updatedConsultationInv = await db.Invoices.FindAsync(consultationInvoice.Id);
        Assert.Equal(pharmInvoice.Id, updatedConsultationInv!.LinkedPharmacyInvoiceId);

        // Verify append-only FinanceLedgerEntries
        var receivableEntry = await db.FinanceLedgerEntries.FirstOrDefaultAsync(
            e => e.ReferenceId == pharmInvoice.Id && e.AccountCategory == "AccountsReceivable");
        Assert.NotNull(receivableEntry);
        Assert.Equal(1000m, receivableEntry.DebitAmount);

        var salesEntry = await db.FinanceLedgerEntries.FirstOrDefaultAsync(
            e => e.ReferenceId == pharmInvoice.Id && e.AccountCategory == "PharmacySales");
        Assert.NotNull(salesEntry);
        Assert.True(salesEntry.CreditAmount > 0);
    }

    [Fact]
    public async Task Linked_Pharmacy_Invoice_Appears_In_GetInvoice_And_GetLinkedDocuments()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var doctorId = Guid.NewGuid();
        var staffId = Guid.NewGuid();

        var setup = SeedFullClinicalPharmacySetup(db, tenantId, doctorId, staffId, UserRole.Receptionist);

        var currentFy = IndianFinancialYearUtils.GetFinancialYear(DateTime.UtcNow);

        // Pharmacy invoice
        var pharmInvoice = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AppointmentId = setup.appointment.Id,
            PatientId = setup.patient.Id,
            Series = "PHARM",
            FinancialYear = currentFy,
            InvoiceNumber = 10,
            Subtotal = 892.86m,
            GstAmount = 107.14m,
            Total = 1000m,
            BalanceDue = 1000m,
            Status = InvoiceStatus.Finalized
        };
        db.Invoices.Add(pharmInvoice);

        // Consultation invoice linking to pharmacy invoice
        var consultationInvoice = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AppointmentId = setup.appointment.Id,
            PatientId = setup.patient.Id,
            Series = "INV",
            FinancialYear = currentFy,
            InvoiceNumber = 25,
            LinkedPharmacyInvoiceId = pharmInvoice.Id,
            Subtotal = 600m,
            GstAmount = 0m,
            Total = 600m,
            BalanceDue = 600m,
            Status = InvoiceStatus.Finalized
        };
        db.Invoices.Add(consultationInvoice);
        await db.SaveChangesAsync();

        var invoicesController = CreateInvoicesController(db, tenantId, staffId, setup.clinic.Id, "Receptionist");

        // 1. GetInvoice on consultation invoice includes linkedPharmacyInvoice summary
        var getResult = await invoicesController.GetInvoice(consultationInvoice.Id, CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(getResult);
        var doc = ToJsonDoc(okResult.Value);

        Assert.Equal(pharmInvoice.Id, doc.RootElement.GetProperty("linkedPharmacyInvoiceId").GetGuid());
        var linkedPharm = doc.RootElement.GetProperty("linkedPharmacyInvoice");
        Assert.Equal(pharmInvoice.Id, linkedPharm.GetProperty("id").GetGuid());
        Assert.Equal($"PHARM-{currentFy}-000010", linkedPharm.GetProperty("invoiceNumber").GetString());
        Assert.Equal(1000m, linkedPharm.GetProperty("total").GetDecimal());

        // 2. GetLinkedDocuments on consultation invoice returns linked pharmacy invoice
        var linkedDocsResult = await invoicesController.GetLinkedDocuments(consultationInvoice.Id, CancellationToken.None);
        var okLinkedDocs = Assert.IsType<OkObjectResult>(linkedDocsResult);
        var docLinked = ToJsonDoc(okLinkedDocs.Value);

        var pharmObj = docLinked.RootElement.GetProperty("linkedPharmacyInvoice");
        Assert.Equal(pharmInvoice.Id, pharmObj.GetProperty("id").GetGuid());

        // 3. GetLinkedDocuments on pharmacy invoice returns linked consultation invoice
        var linkedFromPharm = await invoicesController.GetLinkedDocuments(pharmInvoice.Id, CancellationToken.None);
        var okFromPharm = Assert.IsType<OkObjectResult>(linkedFromPharm);
        var docFromPharm = ToJsonDoc(okFromPharm.Value);

        var consultArray = docFromPharm.RootElement.GetProperty("linkedConsultationInvoices");
        Assert.Equal(1, consultArray.GetArrayLength());
        Assert.Equal(consultationInvoice.Id, consultArray[0].GetProperty("id").GetGuid());
        Assert.Equal($"INV-{currentFy}-000025", consultArray[0].GetProperty("invoiceNumber").GetString());
    }

    [Fact]
    public async Task Linked_Pharmacy_Invoice_Is_Payable_Through_Same_Confirm_Flow()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var pharmacistId = Guid.NewGuid();

        var currentFy = IndianFinancialYearUtils.GetFinancialYear(DateTime.UtcNow);
        var clinic = new Clinic { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Pharmacy clinic" };
        db.Clinics.Add(clinic);
        db.Users.Add(new User { Id = pharmacistId, TenantId = tenantId, ClinicId = clinic.Id, Name = "Pharmacist", Role = UserRole.Pharmacist });
        db.TenantMemberships.Add(new TenantMembership { UserId = pharmacistId, TenantId = tenantId, ClinicId = clinic.Id, Role = UserRole.Pharmacist });

        var pharmInvoice = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Series = "PHARM",
            FinancialYear = currentFy,
            InvoiceNumber = 42,
            Subtotal = 400m,
            GstAmount = 48m,
            Total = 448m,
            BalanceDue = 448m,
            Status = InvoiceStatus.Finalized
        };
        db.Invoices.Add(pharmInvoice);
        await db.SaveChangesAsync();

        var paymentsController = new PaymentsController(db);
        paymentsController.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = CreatePrincipal(tenantId, pharmacistId, clinic.Id, "Pharmacist") }
        };

        var confirmReq = new ConfirmPaymentRequest(
            Method: "Cash",
            Amount: 448m,
            IdempotencyKey: "pharm-pay-001");

        var result = await paymentsController.ConfirmPayment(pharmInvoice.Id, confirmReq, CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(result);

        var doc = ToJsonDoc(okResult.Value);
        Assert.Equal(448m, doc.RootElement.GetProperty("paidAmount").GetDecimal());
        Assert.Equal(0m, doc.RootElement.GetProperty("balanceDue").GetDecimal());
        Assert.Equal("paid", doc.RootElement.GetProperty("status").GetString());

        var updatedInvoice = await db.Invoices.FindAsync(pharmInvoice.Id);
        Assert.Equal(InvoiceStatus.Paid, updatedInvoice!.Status);
        Assert.Equal(0m, updatedInvoice.BalanceDue);
        Assert.Equal(448m, updatedInvoice.PaidAmount);

        // Verify ledger entries for pharmacy payment
        var cashEntry = await db.FinanceLedgerEntries.FirstOrDefaultAsync(
            e => e.AccountCategory == "Cash" && e.DebitAmount == 448m);
        Assert.NotNull(cashEntry);
        Assert.Equal(448m, cashEntry.DebitAmount);

        var arEntry = await db.FinanceLedgerEntries.FirstOrDefaultAsync(
            e => e.ReferenceId == pharmInvoice.Id && e.AccountCategory == "AccountsReceivable" && e.CreditAmount == 448m);
        Assert.NotNull(arEntry);
    }

    [Fact]
    public async Task Explicit_Link_Pharmacy_Invoice_Endpoint_Validates_Series_And_Links()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var staffId = Guid.NewGuid();

        var currentFy = IndianFinancialYearUtils.GetFinancialYear(DateTime.UtcNow);
        var clinic = new Clinic { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Billing clinic" };
        db.Clinics.Add(clinic);
        db.Users.Add(new User { Id = staffId, TenantId = tenantId, ClinicId = clinic.Id, Name = "Receptionist", Role = UserRole.Receptionist });
        db.TenantMemberships.Add(new TenantMembership { UserId = staffId, TenantId = tenantId, ClinicId = clinic.Id, Role = UserRole.Receptionist });

        var consultationInvoice = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Series = "INV",
            FinancialYear = currentFy,
            InvoiceNumber = 1,
            Total = 500m,
            Status = InvoiceStatus.Finalized
        };
        db.Invoices.Add(consultationInvoice);

        var regularInvoice = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Series = "INV",
            FinancialYear = currentFy,
            InvoiceNumber = 2,
            Total = 750m,
            Status = InvoiceStatus.Finalized
        };
        db.Invoices.Add(regularInvoice);

        var pharmInvoice = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Series = "PHARM",
            FinancialYear = currentFy,
            InvoiceNumber = 1,
            InvoiceType = InvoiceType.Pharmacy,
            Total = 320m,
            Status = InvoiceStatus.Finalized
        };
        db.Invoices.Add(pharmInvoice);
        await db.SaveChangesAsync();

        var controller = CreateInvoicesController(db, tenantId, staffId, clinic.Id, "Receptionist");

        // 1. Linking a non-pharmacy invoice returns BadRequest
        var invalidResult = await controller.LinkPharmacyInvoice(
            consultationInvoice.Id, new LinkPharmacyInvoiceRequest(regularInvoice.Id), CancellationToken.None);
        var badRequest = Assert.IsType<BadRequestObjectResult>(invalidResult);
        var badDoc = ToJsonDoc(badRequest.Value);
        Assert.Equal("not_a_pharmacy_invoice", badDoc.RootElement.GetProperty("error").GetString());

        // 2. Linking a genuine PHARM invoice returns 200 and links
        var validResult = await controller.LinkPharmacyInvoice(
            consultationInvoice.Id, new LinkPharmacyInvoiceRequest(pharmInvoice.Id), CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(validResult);
        var okDoc = ToJsonDoc(okResult.Value);
        Assert.Equal("pharmacy_invoice_linked", okDoc.RootElement.GetProperty("message").GetString());
        Assert.Equal(pharmInvoice.Id, okDoc.RootElement.GetProperty("linkedPharmacyInvoiceId").GetGuid());

        var updated = await db.Invoices.FindAsync(consultationInvoice.Id);
        Assert.Equal(pharmInvoice.Id, updated!.LinkedPharmacyInvoiceId);
    }

    [Fact]
    public async Task CreateInvoice_AutoLinks_Unlinked_Pharm_Invoice_For_Same_Appointment()
    {
        var db = CreateInMemoryDb();
        var tenantId = Guid.NewGuid();
        var doctorId = Guid.NewGuid();
        var staffId = Guid.NewGuid();

        var setup = SeedFullClinicalPharmacySetup(db, tenantId, doctorId, staffId, UserRole.Receptionist);

        var currentFy = IndianFinancialYearUtils.GetFinancialYear(DateTime.UtcNow);

        // Pharmacy invoice dispensed first for this appointment
        var pharmInvoice = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AppointmentId = setup.appointment.Id,
            PatientId = setup.patient.Id,
            Series = "PHARM",
            FinancialYear = currentFy,
            InvoiceNumber = 8,
            Total = 650m,
            BalanceDue = 650m,
            Status = InvoiceStatus.Finalized
        };
        db.Invoices.Add(pharmInvoice);
        await db.SaveChangesAsync();

        var controller = CreateInvoicesController(db, tenantId, staffId, setup.clinic.Id, "Receptionist");

        // Receptionist generates consultation invoice afterwards without explicitly passing LinkedPharmacyInvoiceId
        var generateReq = new GenerateInvoiceRequest(
            setup.appointment.Id,
            new List<InvoiceLineItemRequest>
            {
                new("Doctor Consultation", 500m, 1, 500m, "Exempt", "999312", 0m)
            },
            AutoFinalize: true);

        var result = await controller.GenerateInvoice(generateReq, CancellationToken.None);
        var created = Assert.IsType<ObjectResult>(result);
        Assert.Equal(201, created.StatusCode);
        var doc = ToJsonDoc(created.Value);

        var createdInvoiceId = doc.RootElement.GetProperty("invoiceId").GetGuid();
        var createdInvoice = await db.Invoices.FindAsync(createdInvoiceId);
        Assert.NotNull(createdInvoice);
        Assert.Equal(pharmInvoice.Id, createdInvoice.LinkedPharmacyInvoiceId);
    }
}
