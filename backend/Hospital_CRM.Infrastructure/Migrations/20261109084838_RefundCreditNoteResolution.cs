using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hospital_CRM.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RefundCreditNoteResolution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RefundCreditNoteId",
                table: "Payments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RefundNonConsiderationReason",
                table: "Payments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_RefundCreditNoteId",
                table: "Payments",
                column: "RefundCreditNoteId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Payments_RefundResolution",
                table: "Payments",
                sql: "NOT (\"RefundCreditNoteId\" IS NOT NULL AND \"RefundNonConsiderationReason\" IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_Payments_CreditNotes_RefundCreditNoteId",
                table: "Payments",
                column: "RefundCreditNoteId",
                principalTable: "CreditNotes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Payments_CreditNotes_RefundCreditNoteId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_RefundCreditNoteId",
                table: "Payments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Payments_RefundResolution",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "RefundCreditNoteId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "RefundNonConsiderationReason",
                table: "Payments");
        }
    }
}
