namespace Hospital_CRM.Domain.Constants;

/// <summary>
/// Timezone used for accounting day boundaries and statutory reports.
/// Kept in one place so a ledger day and a GST day can never disagree.
/// </summary>
public static class AccountingTimezones
{
    public const string Kolkata = "Asia/Kolkata";
}
