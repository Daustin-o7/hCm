using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hospital_CRM.Api.Services;

public sealed record PaymentEvent(string EventId, decimal? Amount, string? Utr, string? Reference,
    string? ProviderPaymentId = null, string? ProviderOrderId = null);

/// <summary>
/// B2 webhook verification + payload normalisation, extracted from the controller so the
/// two properties that matter are unit-testable: a payload signed with tenant B's secret
/// must not verify under tenant A's, and the amount/UTR aliases must parse.
/// </summary>
public static class PaymentWebhookCodec
{
    public static string SignHmac(string secret, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(body)));
    }

    public static string SignSha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static bool Verify(string provider, string secret, string body, string signature)
    {
        var provided = (signature ?? string.Empty).Trim();
        var hashPart = provided.Contains("###") ? provided.Split("###")[0] : provided;

        if (FixedTimeEquals(SignHmac(secret, body), hashPart))
            return true;

        // PhonePe also documents sha256(body + salt) with no HMAC key.
        if (provider == "phonepe" && FixedTimeEquals(SignSha256(body + secret), hashPart))
            return true;

        return false;
    }

    public static PaymentEvent? TryParse(string provider, string body)
    {
        try
        {
            return Parse(provider, body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static PaymentEvent? Parse(string provider, string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        if (provider == "razorpay")
        {
            if (ReadString(root, "event") is not ("payment.captured" or "order.paid")
                || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("payment", out var payment) || payment.ValueKind != JsonValueKind.Object
                || !payment.TryGetProperty("entity", out var entity) || entity.ValueKind != JsonValueKind.Object
                || ReadString(entity, "status") != "captured" || ReadString(entity, "currency") != "INR")
                return null;

            // Notes are caller-controlled metadata, never evidence of capture, currency, amount or payment identity.
            var paymentId = ReadString(entity, "id");
            var minorAmount = entity.TryGetProperty("amount", out var amountValue) ? ToDecimal(amountValue) : null;
            if (string.IsNullOrWhiteSpace(paymentId) || paymentId.Length > 64
                || minorAmount is null or <= 0 || decimal.Truncate(minorAmount.Value) != minorAmount)
                return null;
            var razorpayReference = ReadString(entity, "receipt") ?? ReadString(entity, "reference");
            if (razorpayReference is null && entity.TryGetProperty("notes", out var notes))
                razorpayReference = ReadString(notes, "receipt") ?? ReadString(notes, "reference");
            return new PaymentEvent(paymentId, minorAmount / 100m,
                ReadString(entity, "utr") ?? ReadString(entity, "bank_transaction_id"),
                razorpayReference, paymentId, ReadString(entity, "order_id"));
        }

        var status = FindString(root, "status", "state", "paymentStatus", "txnStatus");
        if (!string.IsNullOrWhiteSpace(status) &&
            !new[] { "success", "succeeded", "paid", "captured", "completed", "txn_success" }
                .Contains(status.ToLowerInvariant()))
            return null;

        var eventId = FindString(root, "txnId", "transactionId", "eventId", "event_id",
            "merchantTransactionId", "orderId", "razorpay_payment_id");
        eventId ??= "sha256:" + SignSha256(body);

        var amount = FindAmount(root, provider);
        var utr = FindString(root, "utr", "bankTxnId", "rrn", "bankReference", "referenceId");
        var reference = FindString(root, "merchantTransactionId", "orderId", "receipt", "reference");

        return new PaymentEvent(eventId, amount, utr, reference);
    }

    private static bool FixedTimeEquals(string expected, string provided) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(provided.Trim().ToLowerInvariant()));

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? FindString(JsonElement element, params string[] names)
    {
        // Alias priority beats document order: a txn id is more precise than the order id
        // even when the order id appears first in the payload.
        foreach (var name in names)
        {
            var found = FindStringByName(element, name);
            if (found is not null)
                return found;
        }
        return null;
    }

    private static string? FindStringByName(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                    return property.Value.GetString();
            }
            foreach (var property in element.EnumerateObject())
            {
                var found = FindStringByName(property.Value, name);
                if (found is not null) return found;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var found = FindStringByName(item, name);
                if (found is not null) return found;
            }
        }
        return null;
    }

    private static decimal? FindAmount(JsonElement element, string provider)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals("txnAmount", StringComparison.OrdinalIgnoreCase))
                {
                    var nested = FindAmount(property.Value, provider);
                    if (nested.HasValue) return nested;
                }

                if (property.Name.Equals("amount", StringComparison.OrdinalIgnoreCase))
                {
                    var parsed = ToDecimal(property.Value);
                    // PhonePe documents amount in paise; Paytm sends txnAmount.value in rupees.
                    if (parsed.HasValue)
                        return provider == "phonepe" ? parsed.Value / 100m : parsed.Value;
                }

                if (property.Name.Equals("value", StringComparison.OrdinalIgnoreCase))
                {
                    var parsed = ToDecimal(property.Value);
                    if (parsed.HasValue) return parsed;
                }
            }
            foreach (var property in element.EnumerateObject())
            {
                var found = FindAmount(property.Value, provider);
                if (found is not null) return found;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var found = FindAmount(item, provider);
                if (found is not null) return found;
            }
        }
        return null;
    }

    private static decimal? ToDecimal(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number when element.TryGetDecimal(out var d) => d,
        JsonValueKind.String when decimal.TryParse(element.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
        _ => null
    };
}
