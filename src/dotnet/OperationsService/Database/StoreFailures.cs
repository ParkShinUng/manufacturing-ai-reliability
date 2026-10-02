using Npgsql;

namespace Mair.OperationsService.Database;

/// <summary>
/// The one answer to "is the operational store unavailable?", shared by the projector and the API
/// (<c>OD-022</c>). Unavailable means the store did not answer — not that it answered with an error.
/// </summary>
public static class StoreFailures
{
    /// <summary>
    /// A client-side failure — refused or reset connection, network or command timeout, pool
    /// exhausted — or one of these SQLSTATEs: class <c>08</c> (connection), <c>57P01</c>–<c>57P03</c>
    /// (shutting down, cannot connect now), <c>53300</c> (too many connections), class <c>28</c>
    /// (authentication: a wrong password stalls visibly rather than dead-lettering every record).
    /// Every other SQL error is a defect.
    /// </summary>
    public static bool IsUnavailable(Exception ex) => ex switch
    {
        PostgresException pg => pg.SqlState.StartsWith("08", StringComparison.Ordinal)
            || pg.SqlState is "57P01" or "57P02" or "57P03" or "53300"
            || pg.SqlState.StartsWith("28", StringComparison.Ordinal),
        NpgsqlException or TimeoutException => true,
        _ => false,
    };
}
