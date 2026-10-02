using Npgsql;

namespace Mair.OperationsService.Database;

/// <summary>
/// The <c>operations</c> roles of migration <c>0003</c> (OD-023). Each data source sets its role on
/// connect, so the API's connections can only read and the projector's cannot change an audit row,
/// whatever the login itself may do.
/// </summary>
public static class OperationsRoles
{
    public const string Projector = "mair_ops_projector";
    public const string Reader = "mair_ops_reader";
    public const string Retention = "mair_ops_retention";

    /// <summary>A data source on <paramref name="connectionString"/> whose sessions run as <paramref name="role"/>.</summary>
    public static NpgsqlDataSource DataSource(string connectionString, string role)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            // §12: 3 s, whatever the connection string says.
            CommandTimeout = 3,

            // SET ROLE at connection start: a server parameter, not a statement the code could skip.
            Options = $"-c role={role}",
        };
        return NpgsqlDataSource.Create(builder.ConnectionString);
    }
}
