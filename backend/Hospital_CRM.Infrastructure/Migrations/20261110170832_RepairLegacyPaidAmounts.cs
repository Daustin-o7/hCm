using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hospital_CRM.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RepairLegacyPaidAmounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Legacy rows were marked Paid with BalanceDue = 0 but never had PaidAmount written.
            // Guarded so it only touches rows a completed payment ledger fully explains.
            migrationBuilder.Sql("""
                UPDATE "Invoices" AS i
                SET "PaidAmount" = i."Total",
                    "UpdatedAt" = now()
                WHERE i."Status" = 2
                  AND i."PaidAmount" = 0
                  AND i."Total" > 0
                  AND i."BalanceDue" = 0
                  AND EXISTS (
                      SELECT 1 FROM "Payments" p
                      WHERE p."InvoiceId" = i."Id" AND p."Status" = 1)
                  AND (
                      SELECT COALESCE(SUM(p."Amount"), 0) FROM "Payments" p
                      WHERE p."InvoiceId" = i."Id" AND p."Status" = 1) = i."Total";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No rollback: the pre-repair state was the defect (Paid invoices reporting zero collected).
        }
    }
}
