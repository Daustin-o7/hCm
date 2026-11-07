using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hospital_CRM.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TenantProvisioningReplayReceipt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantProvisioningReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProtectedResponse = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantProvisioningReceipts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantProvisioningReceipts_ActorId_IdempotencyKey",
                table: "TenantProvisioningReceipts",
                columns: new[] { "ActorId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.Sql("""
                ALTER TABLE public."TenantProvisioningReceipts" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."TenantProvisioningReceipts" FORCE ROW LEVEL SECURITY;
                CREATE POLICY provisioning_admin_policy ON public."TenantProvisioningReceipts" FOR ALL TO app_user
                    USING (current_setting('app.is_platform_admin', true) = 'true')
                    WITH CHECK (current_setting('app.is_platform_admin', true) = 'true');
                REVOKE ALL ON TABLE public."TenantProvisioningReceipts" FROM PUBLIC;
                REVOKE UPDATE, DELETE, TRUNCATE ON TABLE public."TenantProvisioningReceipts" FROM app_user;
                GRANT SELECT, INSERT ON TABLE public."TenantProvisioningReceipts" TO app_user;
                GRANT UPDATE ("ProtectedResponse") ON TABLE public."TenantProvisioningReceipts" TO app_user;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantProvisioningReceipts");
        }
    }
}
