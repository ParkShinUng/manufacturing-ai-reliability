using Npgsql;

namespace Mair.OperationsService.Database;

/// <summary>
/// Retention by whole partition (OD-023, <c>OPERATIONAL_DATA.md</c> §15): at start and hourly, as
/// <c>mair_ops_retention</c>, through the one function that role may call. No row is deleted.
/// </summary>
public sealed class RetentionJob(NpgsqlDataSource retention, TimeProvider time)
{
    public static readonly TimeSpan Every = TimeSpan.FromHours(1);

    /// <returns>The partitions dropped.</returns>
    public async Task<IReadOnlyList<string>> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await using var cmd = retention.CreateCommand("SELECT operations.drop_expired_partitions($1)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = time.GetUtcNow().UtcDateTime });
        var dropped = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            dropped.Add(reader.GetString(0));
        }

        return dropped;
    }
}

/// <summary>The retention job for the life of the host. A failed run is retried at the next hour.</summary>
public sealed class RetentionService(RetentionJob job) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RetentionJob.Every);
        do
        {
            try
            {
                await job.RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (StoreFailures.IsUnavailable(ex))
            {
                // The store is down: nothing can be dropped, and the next hour tries again.
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
