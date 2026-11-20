using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace GatewayService.Resilience;

public sealed class RetryQueueOptions
{
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(10);
}

public sealed class RetryQueue(IServiceScopeFactory scopeFactory, ILogger<RetryQueue> logger)
{
    private sealed record Item(string Description, Func<IServiceProvider, Task> Action);

    private readonly ConcurrentQueue<Item> _queue = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public int Count => _queue.Count;

    public void Enqueue(string description, Func<IServiceProvider, Task> action)
    {
        _queue.Enqueue(new Item(description, action));
        logger.LogWarning("Queued for retry: {Description}", description);
    }

    public async Task ProcessAsync(CancellationToken ct = default)
    {
        if (_queue.IsEmpty)
            return;

        await _gate.WaitAsync(ct);
        try
        {
            while (_queue.TryPeek(out var item))
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    await item.Action(scope.ServiceProvider);
                    _queue.TryDequeue(out _);
                    logger.LogInformation("Retry succeeded: {Description}", item.Description);
                }
                catch (Exception e)
                {
                    logger.LogWarning("Retry failed: {Description}: {Error}", item.Description, e.Message);
                    return;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed class RetryQueueWorker(RetryQueue queue, IOptions<RetryQueueOptions> options)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.RetryInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await queue.ProcessAsync(stoppingToken);
    }
}
