using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Hospital_CRM.Api.Services;

/// <summary>
/// B2/F6: creates a Razorpay order so the webhook has an order id to match on.
/// Optional by decision D7 — when <c>Razorpay:KeyId</c>/<c>KeySecret</c> are unset the
/// caller returns 503 instead of handing the client a fake checkout URL.
/// </summary>
public class RazorpayOrderClient
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;

    public RazorpayOrderClient(HttpClient http, IConfiguration config)
    {
        _http = http;
        _config = config;
        _http.BaseAddress ??= new Uri("https://api.razorpay.com/v1/");
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_config["Razorpay:KeyId"]) &&
        !string.IsNullOrWhiteSpace(_config["Razorpay:KeySecret"]);

    public string? KeyId => _config["Razorpay:KeyId"];

    public async Task<string?> CreateOrderAsync(decimal amountRupees, string receipt, CancellationToken ct)
    {
        var keyId = _config["Razorpay:KeyId"];
        var keySecret = _config["Razorpay:KeySecret"];
        if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(keySecret))
            return null;

        using var request = new HttpRequestMessage(HttpMethod.Post, "orders")
        {
            Content = JsonContent.Create(new
            {
                amount = (int)Math.Round(amountRupees * 100m),
                currency = "INR",
                receipt,
                payment_capture = 1
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{keyId}:{keySecret}")));

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
    }

    public Task<RazorpayRefund?> CreateRefundAsync(string paymentId, decimal amountRupees, Guid refundId, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"payments/{Uri.EscapeDataString(paymentId)}/refund")
        {
            Content = JsonContent.Create(new { amount = checked((int)(amountRupees * 100m)), receipt = refundId.ToString("N") })
        };
        // The durable local refund id fixes both key and body across ambiguous network retries.
        request.Headers.Add("X-Refund-Idempotency", $"refund-{refundId:N}");
        return SendRefundAsync(request, paymentId, amountRupees, null, ct);
    }

    public Task<RazorpayRefund?> GetRefundAsync(string refundId, string paymentId, decimal amountRupees, CancellationToken ct) =>
        SendRefundAsync(new(HttpMethod.Get, $"refunds/{Uri.EscapeDataString(refundId)}"), paymentId, amountRupees, refundId, ct);

    private async Task<RazorpayRefund?> SendRefundAsync(HttpRequestMessage request, string paymentId, decimal amountRupees,
        string? expectedRefundId, CancellationToken ct)
    {
        using (request)
        {
            if (!IsConfigured || amountRupees <= 0 || decimal.Round(amountRupees, 2) != amountRupees) return null;
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{_config["Razorpay:KeyId"]}:{_config["Razorpay:KeySecret"]}")));
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            try
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                    || id.GetString() is not { Length: > 5 and <= 100 } value || !value.StartsWith("rfnd_", StringComparison.Ordinal)
                    || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')
                    || expectedRefundId != null && value != expectedRefundId
                    || !root.TryGetProperty("payment_id", out var payment) || payment.ValueKind != JsonValueKind.String || payment.GetString() != paymentId
                    || !root.TryGetProperty("currency", out var currency) || currency.ValueKind != JsonValueKind.String || currency.GetString() != "INR"
                    || !root.TryGetProperty("amount", out var amount) || amount.ValueKind != JsonValueKind.Number
                    || !amount.TryGetInt64(out var minorUnits) || minorUnits != amountRupees * 100m
                    || !root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String
                    || status.GetString() is not ("pending" or "processed" or "failed")) return null;
                return new(value, status.GetString()!);
            }
            catch (JsonException) { return null; }
        }
    }
}

public record RazorpayRefund(string Id, string Status);
