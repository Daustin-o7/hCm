using System.Text.Json;
using Hospital_CRM.Api.Services;
using Xunit;

namespace Hospital_CRM.Tests;

public class PaymentWebhookCodecTests
{
    private const string Body = "{\"txnId\":\"T1\",\"amount\":100}";

    [Fact]
    public void Matching_Secret_Verifies_Signature()
    {
        var signature = PaymentWebhookCodec.SignHmac("tenant-a-secret", Body);
        Assert.True(PaymentWebhookCodec.Verify("paytm", "tenant-a-secret", Body, signature));
    }

    [Fact]
    public void Tenant_B_Secret_Cannot_Sign_For_Tenant_A()
    {
        // B2 done-when: tenant A's webhook cannot be processed under tenant B's secret.
        var signature = PaymentWebhookCodec.SignHmac("tenant-b-secret", Body);
        Assert.False(PaymentWebhookCodec.Verify("paytm", "tenant-a-secret", Body, signature));
    }

    [Fact]
    public void Tampered_Body_Fails_Verification()
    {
        var signature = PaymentWebhookCodec.SignHmac("s", Body);
        Assert.False(PaymentWebhookCodec.Verify("paytm", "s", Body.Replace("100", "999"), signature));
    }

    [Fact]
    public void PhonePe_XVerify_Hash_With_Salt_Index_Verifies()
    {
        var hash = PaymentWebhookCodec.SignHmac("phonepe-secret", Body);
        Assert.True(PaymentWebhookCodec.Verify("phonepe", "phonepe-secret", Body, $"{hash}###1"));
    }

    [Fact]
    public void Paytm_Payload_Parses_Rupee_Amount_And_Bank_Reference()
    {
        const string body = """
        {
          "body": {
            "orderId": "ORD-1",
            "txnAmount": { "value": "1500.00", "currency": "INR" },
            "txnId": "PAY-1",
            "bankTxnId": "UTR12345",
            "status": "TXN_SUCCESS"
          }
        }
        """;

        var evt = PaymentWebhookCodec.TryParse("paytm", body);

        Assert.NotNull(evt);
        Assert.Equal(1500.00m, evt!.Amount);
        Assert.Equal("UTR12345", evt.Utr);
        Assert.Equal("PAY-1", evt.EventId);
    }

    [Fact]
    public void PhonePe_Payload_Converts_Paise_To_Rupees()
    {
        const string body = """
        {
          "event": "pg.order.completed",
          "payload": {
            "transactionId": "TX-9",
            "amount": 25000,
            "state": "COMPLETED",
            "merchantTransactionId": "M-42"
          }
        }
        """;

        var evt = PaymentWebhookCodec.TryParse("phonepe", body);

        Assert.NotNull(evt);
        Assert.Equal(250.00m, evt!.Amount);
        Assert.Equal("M-42", evt.Reference);
    }

    [Fact]
    public void Failed_Status_Returns_No_Payment_Event()
    {
        const string body = """{ "txnId": "T1", "amount": 100, "status": "TXN_FAILURE" }""";
        Assert.Null(PaymentWebhookCodec.TryParse("paytm", body));
    }

    [Fact]
    public void Malformed_Json_Returns_Null()
    {
        Assert.Null(PaymentWebhookCodec.TryParse("paytm", "not-json"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"event\":42}")]
    [InlineData("{\"event\":null}")]
    [InlineData("{\"event\":{},\"payload\":{}}")]
    public void Malformed_Razorpay_Envelope_Does_Not_Throw(string body) =>
        Assert.Null(PaymentWebhookCodec.TryParse("razorpay", body));

    [Theory]
    [InlineData("authorized", "INR", "10000")]
    [InlineData("captured", "USD", "10000")]
    [InlineData("captured", "INR", "0")]
    [InlineData("captured", "INR", "-1")]
    [InlineData("captured", "INR", "100.5")]
    [InlineData("captured", "INR", "null")]
    [InlineData("captured", "INR", "\"not-money\"")]
    public void Razorpay_Requires_Positive_Whole_Paise_Captured_Inr(string status, string currency, string amount)
    {
        using var amountJson = JsonDocument.Parse(amount);
        var body = JsonSerializer.Serialize(new { @event = "payment.captured", payload = new { payment = new { entity = new { id = "pay_test", status, currency, amount = amountJson.RootElement } } } });
        Assert.Null(PaymentWebhookCodec.TryParse("razorpay", body));
    }

    [Theory]
    [InlineData("\"id\":null,")]
    [InlineData("\"status\":null,")]
    [InlineData("\"currency\":null,")]
    [InlineData("\"amount\":null,")]
    public void Razorpay_Notes_Cannot_Supply_Missing_Authoritative_Fields(string missing)
    {
        var fields = "\"id\":\"pay_test\",\"status\":\"captured\",\"currency\":\"INR\",\"amount\":10000,";
        var name = missing.Split(':')[0];
        fields = string.Join(',', fields.Split(',').Where(f => f.Length > 0 && !f.StartsWith(name, StringComparison.Ordinal))) + ',';
        var body = "{\"event\":\"payment.captured\",\"payload\":{\"payment\":{\"entity\":{" + fields + missing + "\"notes\":{\"id\":\"pay_notes\",\"status\":\"captured\",\"currency\":\"INR\",\"amount\":10000}}}}}";
        Assert.Null(PaymentWebhookCodec.TryParse("razorpay", body));
    }
}
