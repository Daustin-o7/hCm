using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hospital_CRM.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SaleReadinessIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProcessedWebhookEvents_Provider_EventId",
                table: "ProcessedWebhookEvents");

            migrationBuilder.DropIndex(
                name: "IX_Payments_IdempotencyKey",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Patients_IdempotencyKey",
                table: "Patients");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_IdempotencyKey",
                table: "Invoices");

            migrationBuilder.AddColumn<bool>(
                name: "IsPharmacyReturn",
                table: "Payments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "RefundSourcePaymentId",
                table: "Payments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PaymentId",
                table: "PatientDepositAllocations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckoutRequestHash",
                table: "Invoices",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckoutResultJson",
                table: "Invoices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "InvoiceId",
                table: "DispenseRecords",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestHash",
                table: "DispenseRecords",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResultJson",
                table: "DispenseRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "DraftLabTests",
                table: "Consultations",
                type: "text[]",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Appointments_TenantId_Id",
                table: "Appointments",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateTable(
                name: "NotificationIntents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppointmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppointmentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ProtectedPrecheckLink = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    HandedOffAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationIntents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationIntents_Appointments_TenantId_AppointmentId",
                        columns: x => new { x.TenantId, x.AppointmentId },
                        principalTable: "Appointments",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedWebhookEvents_TenantId_Provider_EventId",
                table: "ProcessedWebhookEvents",
                columns: new[] { "TenantId", "Provider", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_RefundSourcePaymentId",
                table: "Payments",
                column: "RefundSourcePaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_TenantId_IdempotencyKey",
                table: "Payments",
                columns: new[] { "TenantId", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_TenantId_Method_RazorpayPaymentId",
                table: "Payments",
                columns: new[] { "TenantId", "Method", "RazorpayPaymentId" },
                unique: true,
                filter: "\"RazorpayPaymentId\" IS NOT NULL AND \"Amount\" > 0 AND \"Method\" = 5");

            migrationBuilder.CreateIndex(
                name: "IX_Patients_TenantId_IdempotencyKey",
                table: "Patients",
                columns: new[] { "TenantId", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PatientDeposits_TenantId_IdempotencyKey",
                table: "PatientDeposits",
                columns: new[] { "TenantId", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PatientDepositAllocations_PaymentId",
                table: "PatientDepositAllocations",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_TenantId_IdempotencyKey",
                table: "Invoices",
                columns: new[] { "TenantId", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DispenseRecords_InvoiceId",
                table: "DispenseRecords",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_DispenseRecords_TenantId_InvoiceId",
                table: "DispenseRecords",
                columns: new[] { "TenantId", "InvoiceId" },
                unique: true,
                filter: "\"InvoiceId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationIntents_TenantId_AppointmentId",
                table: "NotificationIntents",
                columns: new[] { "TenantId", "AppointmentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationIntents_TenantId_CreatedAt",
                table: "NotificationIntents",
                columns: new[] { "TenantId", "CreatedAt" },
                filter: "\"HandedOffAt\" IS NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_DispenseRecords_Invoices_InvoiceId",
                table: "DispenseRecords",
                column: "InvoiceId",
                principalTable: "Invoices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PatientDepositAllocations_Payments_PaymentId",
                table: "PatientDepositAllocations",
                column: "PaymentId",
                principalTable: "Payments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Payments_Payments_RefundSourcePaymentId",
                table: "Payments",
                column: "RefundSourcePaymentId",
                principalTable: "Payments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                ALTER TABLE public."NotificationIntents" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."NotificationIntents" FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation_policy ON public."NotificationIntents" FOR ALL TO app_user
                    USING (current_setting('app.is_platform_admin', true) = 'true'
                        OR "TenantId" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                    WITH CHECK (current_setting('app.is_platform_admin', true) = 'true'
                        OR "TenantId" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
                REVOKE ALL ON TABLE public."NotificationIntents" FROM PUBLIC;
                REVOKE UPDATE, DELETE, TRUNCATE ON TABLE public."NotificationIntents" FROM app_user;
                GRANT SELECT, INSERT ON TABLE public."NotificationIntents" TO app_user;
                GRANT UPDATE ("HandedOffAt") ON TABLE public."NotificationIntents" TO app_user;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DispenseRecords_Invoices_InvoiceId",
                table: "DispenseRecords");

            migrationBuilder.DropForeignKey(
                name: "FK_PatientDepositAllocations_Payments_PaymentId",
                table: "PatientDepositAllocations");

            migrationBuilder.DropForeignKey(
                name: "FK_Payments_Payments_RefundSourcePaymentId",
                table: "Payments");

            migrationBuilder.DropTable(
                name: "NotificationIntents");

            migrationBuilder.DropIndex(
                name: "IX_ProcessedWebhookEvents_TenantId_Provider_EventId",
                table: "ProcessedWebhookEvents");

            migrationBuilder.DropIndex(
                name: "IX_Payments_RefundSourcePaymentId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_TenantId_IdempotencyKey",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_TenantId_Method_RazorpayPaymentId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Patients_TenantId_IdempotencyKey",
                table: "Patients");

            migrationBuilder.DropIndex(
                name: "IX_PatientDeposits_TenantId_IdempotencyKey",
                table: "PatientDeposits");

            migrationBuilder.DropIndex(
                name: "IX_PatientDepositAllocations_PaymentId",
                table: "PatientDepositAllocations");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_TenantId_IdempotencyKey",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_DispenseRecords_InvoiceId",
                table: "DispenseRecords");

            migrationBuilder.DropIndex(
                name: "IX_DispenseRecords_TenantId_InvoiceId",
                table: "DispenseRecords");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Appointments_TenantId_Id",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "IsPharmacyReturn",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "RefundSourcePaymentId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "PaymentId",
                table: "PatientDepositAllocations");

            migrationBuilder.DropColumn(
                name: "CheckoutRequestHash",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "CheckoutResultJson",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "InvoiceId",
                table: "DispenseRecords");

            migrationBuilder.DropColumn(
                name: "RequestHash",
                table: "DispenseRecords");

            migrationBuilder.DropColumn(
                name: "ResultJson",
                table: "DispenseRecords");

            migrationBuilder.DropColumn(
                name: "DraftLabTests",
                table: "Consultations");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedWebhookEvents_Provider_EventId",
                table: "ProcessedWebhookEvents",
                columns: new[] { "Provider", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_IdempotencyKey",
                table: "Payments",
                column: "IdempotencyKey",
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Patients_IdempotencyKey",
                table: "Patients",
                column: "IdempotencyKey",
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_IdempotencyKey",
                table: "Invoices",
                column: "IdempotencyKey",
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");
        }
    }
}
