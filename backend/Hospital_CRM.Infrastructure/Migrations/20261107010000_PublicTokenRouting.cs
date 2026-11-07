using Hospital_CRM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hospital_CRM.Infrastructure.Migrations;

// SQL-only migration: deliberately has no model/snapshot changes.
[DbContext(typeof(HospitalCrmDbContext))]
[Migration("20261107010000_PublicTokenRouting")]
public class PublicTokenRouting : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $roles$
            BEGIN
                IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'public_token_router') THEN
                    CREATE ROLE public_token_router NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
                END IF;
                IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'public_token_router'
                    AND (rolcanlogin OR rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls))
                    OR EXISTS (SELECT FROM pg_auth_members WHERE member = 'public_token_router'::regrole)
                    OR pg_has_role('app_user', 'public_token_router', 'MEMBER') THEN
                    RAISE EXCEPTION 'public_token_router must be an isolated, non-login, non-privileged role';
                END IF;
            END $roles$;

            GRANT USAGE ON SCHEMA public TO public_token_router;
            GRANT SELECT ("Id", "TenantId", "QueueTrackingToken") ON public."Appointments" TO public_token_router;
            GRANT SELECT ("Id", "TenantId", "TokenHash") ON public."PrecheckSubmissions" TO public_token_router;
            GRANT SELECT ("Id", "TenantId", "AppointmentId", "Version", "VerificationQrCode") ON public."Consultations" TO public_token_router;
            GRANT SELECT ("Id", "TenantId", "RazorpayOrderId") ON public."Payments" TO public_token_router;

            CREATE POLICY token_route ON public."Appointments" FOR SELECT TO public_token_router USING (true);
            CREATE POLICY token_route ON public."PrecheckSubmissions" FOR SELECT TO public_token_router USING (true);
            CREATE POLICY token_route ON public."Consultations" FOR SELECT TO public_token_router USING (true);
            CREATE POLICY token_route ON public."Payments" FOR SELECT TO public_token_router USING (true);

            -- Replace, rather than OR another permissive policy with the anonymous PHI exception.
            DROP POLICY tenant_isolation_policy ON public."PrecheckSubmissions";
            CREATE POLICY tenant_isolation_policy ON public."PrecheckSubmissions" FOR ALL TO app_user
                USING (current_setting('app.is_platform_admin', true) = 'true'
                    OR "TenantId" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                WITH CHECK (current_setting('app.is_platform_admin', true) = 'true'
                    OR "TenantId" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);

            CREATE FUNCTION public.resolve_public_token(purpose text, token text)
            RETURNS TABLE ("TenantId" uuid, "ResourceId" uuid)
            LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $route$
                SELECT a."TenantId", a."Id" FROM public."Appointments" a
                    WHERE purpose = 'queue' AND a."QueueTrackingToken" = token
                UNION ALL
                SELECT s."TenantId", s."Id" FROM public."PrecheckSubmissions" s
                    WHERE purpose = 'precheck' AND s."TokenHash" = token
                UNION ALL
                (SELECT c."TenantId", c."Id" FROM public."Consultations" c
                    WHERE purpose = 'prescription' AND c."VerificationQrCode" = token
                    AND NOT EXISTS (SELECT 1 FROM public."Consultations" other
                        WHERE other."VerificationQrCode" = token
                        AND (other."TenantId", other."AppointmentId") IS DISTINCT FROM (c."TenantId", c."AppointmentId"))
                    ORDER BY c."Version" DESC, c."Id" LIMIT 1);
            $route$;
            ALTER FUNCTION public.resolve_public_token(text,text) OWNER TO public_token_router;
            REVOKE ALL ON FUNCTION public.resolve_public_token(text,text) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION public.resolve_public_token(text,text) TO app_user;

            -- Caller verifies the Razorpay signature before routing, then scopes and rechecks the payment/order.
            CREATE FUNCTION public.resolve_razorpay_order(order_id text)
            RETURNS TABLE ("TenantId" uuid, "ResourceId" uuid)
            LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $route$
                SELECT p."TenantId", p."Id" FROM public."Payments" p
                WHERE order_id IS NOT NULL AND order_id <> '' AND p."RazorpayOrderId" = order_id
                    AND NOT EXISTS (SELECT 1 FROM public."Payments" other
                        WHERE other."RazorpayOrderId" = order_id AND other."Id" <> p."Id");
            $route$;
            ALTER FUNCTION public.resolve_razorpay_order(text) OWNER TO public_token_router;
            REVOKE ALL ON FUNCTION public.resolve_razorpay_order(text) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION public.resolve_razorpay_order(text) TO app_user;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP FUNCTION public.resolve_razorpay_order(text);
            DROP FUNCTION public.resolve_public_token(text,text);
            DROP POLICY token_route ON public."Payments";
            DROP POLICY token_route ON public."Consultations";
            DROP POLICY token_route ON public."PrecheckSubmissions";
            DROP POLICY token_route ON public."Appointments";

            REVOKE SELECT ("Id", "TenantId", "RazorpayOrderId") ON public."Payments" FROM public_token_router;
            REVOKE SELECT ("Id", "TenantId", "AppointmentId", "Version", "VerificationQrCode") ON public."Consultations" FROM public_token_router;
            REVOKE SELECT ("Id", "TenantId", "TokenHash") ON public."PrecheckSubmissions" FROM public_token_router;
            REVOKE SELECT ("Id", "TenantId", "QueueTrackingToken") ON public."Appointments" FROM public_token_router;
            REVOKE USAGE ON SCHEMA public FROM public_token_router;
            -- Keep the cluster-wide NOLOGIN role: another database may still use it.

            -- Restore the preceding migration's policy on rollback, including its anonymous exception.
            DROP POLICY tenant_isolation_policy ON public."PrecheckSubmissions";
            CREATE POLICY tenant_isolation_policy ON public."PrecheckSubmissions" FOR ALL TO app_user
                USING (current_setting('app.is_platform_admin', true) = 'true'
                    OR "TenantId" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
                    OR ("SubmittedAt" IS NULL AND "ExpiresAt" > now()))
                WITH CHECK (current_setting('app.is_platform_admin', true) = 'true'
                    OR "TenantId" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
                    OR "SubmittedAt" IS NOT NULL);
            """);
    }
}
