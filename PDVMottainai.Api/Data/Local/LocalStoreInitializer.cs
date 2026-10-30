namespace PDVMottainai.Api.Data.Local;

public sealed class LocalStoreInitializer(LocalStore store, ILogger<LocalStoreInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await store.InitializeAsync(cancellationToken);
        logger.LogInformation("Local POS cache and outbox are ready.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
