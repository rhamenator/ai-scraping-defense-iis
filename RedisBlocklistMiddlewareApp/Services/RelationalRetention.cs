using System.Data.Common;

namespace RedisBlocklistMiddlewareApp.Services;

/// <summary>
/// Row-count and age retention for the PostgreSQL and SQL Server audit tables (#197), with the same rules as the SQLite store:
/// the oldest rows beyond a cap are removed, optionally rows older than a number of days, and decisions an operator gave
/// feedback on are always kept (the feedback table refers to them). Written in SQL both servers accept, and run in one
/// transaction. The dashboard totals live in <c>defense_event_summary</c> and are not reduced.
/// </summary>
internal static class RelationalRetention
{
    /// <summary>How often (in inserts) a store prunes; pruning also runs once when the store starts.</summary>
    public const int PruneEveryInserts = 500;

    public static void Prune(
        DbConnection connection,
        string table,
        string timeColumn,
        bool protectFeedback,
        int maxRows,
        int retentionDays,
        DateTimeOffset now)
    {
        if (maxRows <= 0 && retentionDays <= 0)
        {
            return;
        }

        // Table and column names come from constants in this assembly, never from input.
        var keep = protectFeedback ? " AND id NOT IN (SELECT decision_id FROM defense_feedback)" : string.Empty;
        using var transaction = connection.BeginTransaction();
        if (maxRows > 0)
        {
            Execute(
                connection,
                transaction,
                $"DELETE FROM {table} WHERE id <= (SELECT MAX(id) FROM {table}) - @cap{keep}",
                ("@cap", (long)maxRows));
        }

        if (retentionDays > 0)
        {
            Execute(
                connection,
                transaction,
                $"DELETE FROM {table} WHERE {timeColumn} < @cutoff{keep}",
                ("@cutoff", now.UtcDateTime.AddDays(-retentionDays).ToString("O")));
        }

        transaction.Commit();
    }

    private static void Execute(DbConnection connection, DbTransaction transaction, string sql, (string Name, object Value) parameter)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var dbParameter = command.CreateParameter();
        dbParameter.ParameterName = parameter.Name;
        dbParameter.Value = parameter.Value;
        command.Parameters.Add(dbParameter);
        command.ExecuteNonQuery();
    }
}
