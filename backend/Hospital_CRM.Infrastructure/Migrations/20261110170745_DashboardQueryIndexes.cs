using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hospital_CRM.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DashboardQueryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Appointments_ClinicId",
                table: "Appointments");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_TenantId_PaidAt",
                table: "Payments",
                columns: new[] { "TenantId", "PaidAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_TenantId_CreatedAt",
                table: "Invoices",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_ClinicId_Date",
                table: "Appointments",
                columns: new[] { "ClinicId", "Date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_TenantId_PaidAt",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_TenantId_CreatedAt",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_Appointments_ClinicId_Date",
                table: "Appointments");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_ClinicId",
                table: "Appointments",
                column: "ClinicId");
        }
    }
}
