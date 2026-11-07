namespace Hospital_CRM.Api.Services;

// No configuration switch may reactivate the former unverified collection writer.
public sealed class RazorpayReconciliationWorker : BackgroundService
{
    private readonly ILogger<RazorpayReconciliationWorker> _logger;

    public RazorpayReconciliationWorker(ILogger<RazorpayReconciliationWorker> logger,
        IConfiguration config, IServiceScopeFactory scopeFactory) => _logger = logger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogWarning("Razorpay reconciliation disabled: captured-only tenant-merchant accounting parity, replay and provider acceptance are pending.");
        return Task.CompletedTask;
    }
}
