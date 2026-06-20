using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Npgsql;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class PostgresDefenseEventStore : RelationalDefenseEventStore
{
    public PostgresDefenseEventStore(IOptions<DefenseEngineOptions> options)
        : base(options, RelationalAuditDialect.Postgres)
    {
    }
}

public sealed class SqlServerDefenseEventStore : RelationalDefenseEventStore
{
    public SqlServerDefenseEventStore(IOptions<DefenseEngineOptions> options)
        : base(options, RelationalAuditDialect.SqlServer)
    {
    }
}

public sealed class PostgresIntakeDeliveryStore : RelationalIntakeDeliveryStore
{
    public PostgresIntakeDeliveryStore(IOptions<DefenseEngineOptions> options)
        : base(options, RelationalAuditDialect.Postgres)
    {
    }
}

public sealed class SqlServerIntakeDeliveryStore : RelationalIntakeDeliveryStore
{
    public SqlServerIntakeDeliveryStore(IOptions<DefenseEngineOptions> options)
        : base(options, RelationalAuditDialect.SqlServer)
    {
    }
}

public sealed class PostgresWebhookEventInbox : RelationalWebhookEventInbox
{
    public PostgresWebhookEventInbox(IOptions<DefenseEngineOptions> options)
        : base(options, RelationalAuditDialect.Postgres)
    {
    }
}

public sealed class SqlServerWebhookEventInbox : RelationalWebhookEventInbox
{
    public SqlServerWebhookEventInbox(IOptions<DefenseEngineOptions> options)
        : base(options, RelationalAuditDialect.SqlServer)
    {
    }
}

public abstract class RelationalDefenseEventStore : IDefenseEventStore
{
    private readonly string _connectionString;
    private readonly RelationalAuditDialect _dialect;
    private readonly int _maxRecentEvents;

    protected RelationalDefenseEventStore(IOptions<DefenseEngineOptions> options, RelationalAuditDialect dialect)
    {
        _connectionString = options.Value.Audit.ConnectionString;
        _dialect = dialect;
        _maxRecentEvents = options.Value.Audit.MaxRecentEvents;
        EnsureSchema();
    }

    public void Add(DefenseDecision decision)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO defense_events
            (
                ip_address,
                action,
                score,
                frequency,
                path,
                signals_json,
                breakdown_json,
                summary,
                observed_at_utc,
                decided_at_utc
            )
            VALUES
            (
                @ipAddress,
                @action,
                @score,
                @frequency,
                @path,
                @signalsJson,
                @breakdownJson,
                @summary,
                @observedAtUtc,
                @decidedAtUtc
            );
            """;
        AddParameter(command, "@ipAddress", decision.IpAddress);
        AddParameter(command, "@action", decision.Action);
        AddParameter(command, "@score", decision.Score);
        AddParameter(command, "@frequency", decision.Frequency);
        AddParameter(command, "@path", decision.Path);
        AddParameter(command, "@signalsJson", JsonSerializer.Serialize(decision.Signals));
        AddParameter(
            command,
            "@breakdownJson",
            decision.Breakdown is null ? DBNull.Value : JsonSerializer.Serialize(decision.Breakdown));
        AddParameter(command, "@summary", decision.Summary);
        AddParameter(command, "@observedAtUtc", decision.ObservedAtUtc.ToString("O"));
        AddParameter(command, "@decidedAtUtc", decision.DecidedAtUtc.ToString("O"));
        command.ExecuteNonQuery();

        using var summaryCommand = connection.CreateCommand();
        summaryCommand.CommandText =
            """
            UPDATE defense_event_summary
            SET
                total_decisions = total_decisions + 1,
                blocked_count = blocked_count + CASE WHEN @action = 'blocked' THEN 1 ELSE 0 END,
                observed_count = observed_count + CASE WHEN @action = 'observed' THEN 1 ELSE 0 END,
                latest_decision_at_utc = CASE
                    WHEN latest_decision_at_utc IS NULL OR latest_decision_at_utc < @decidedAtUtc
                        THEN @decidedAtUtc
                    ELSE latest_decision_at_utc
                END
            WHERE summary_key = 1;
            """;
        AddParameter(summaryCommand, "@action", decision.Action);
        AddParameter(summaryCommand, "@decidedAtUtc", decision.DecidedAtUtc.ToString("O"));
        summaryCommand.ExecuteNonQuery();
    }

    public IReadOnlyList<DefenseDecision> GetRecent(int count)
    {
        var safeCount = Math.Clamp(count, 1, _maxRecentEvents);
        var results = new List<DefenseDecision>(safeCount);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT
                id,
                ip_address,
                action,
                score,
                frequency,
                path,
                signals_json,
                breakdown_json,
                summary,
                observed_at_utc,
                decided_at_utc
            FROM defense_events
            ORDER BY decided_at_utc DESC, id DESC
            {_dialect.LimitClause("@limit")};
            """;
        AddParameter(command, "@limit", safeCount);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadDecision(reader));
        }

        return results;
    }

    public DefenseDecision? GetById(long id)
    {
        if (id <= 0)
        {
            return null;
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                ip_address,
                action,
                score,
                frequency,
                path,
                signals_json,
                breakdown_json,
                summary,
                observed_at_utc,
                decided_at_utc
            FROM defense_events
            WHERE id = @id;
            """;
        AddParameter(command, "@id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadDecision(reader) : null;
    }

    public DefenseDecisionFeedback AddFeedback(DefenseDecisionFeedback feedback)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = _dialect.InsertFeedbackSql;
        AddParameter(command, "@decisionId", feedback.DecisionId);
        AddParameter(command, "@ipAddress", feedback.IpAddress);
        AddParameter(command, "@originalAction", feedback.OriginalAction);
        AddParameter(command, "@updatedAction", feedback.UpdatedAction);
        AddParameter(command, "@reason", feedback.Reason);
        AddParameter(command, "@actor", feedback.Actor);
        AddParameter(command, "@createdAtUtc", feedback.CreatedAtUtc.ToString("O"));

        var id = Convert.ToInt64(command.ExecuteScalar());
        return feedback with { Id = id };
    }

    public IReadOnlyList<DefenseDecisionFeedback> GetRecentFeedback(int count)
    {
        var safeCount = Math.Clamp(count, 1, _maxRecentEvents);
        var results = new List<DefenseDecisionFeedback>(safeCount);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT
                id,
                decision_id,
                ip_address,
                original_action,
                updated_action,
                reason,
                actor,
                created_at_utc
            FROM defense_feedback
            ORDER BY created_at_utc DESC, id DESC
            {_dialect.LimitClause("@limit")};
            """;
        AddParameter(command, "@limit", safeCount);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new DefenseDecisionFeedback(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                DateTimeOffset.Parse(reader.GetString(7))));
        }

        return results;
    }

    public DefenseEventMetrics GetMetrics()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                total_decisions,
                blocked_count,
                observed_count,
                latest_decision_at_utc
            FROM defense_event_summary
            WHERE summary_key = 1;
            """;

        using var reader = command.ExecuteReader();
        reader.Read();

        return new DefenseEventMetrics(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3)));
    }

    private void EnsureSchema()
    {
        using var connection = OpenConnection();
        ExecuteNonQuery(connection, _dialect.DefenseSchemaSql);
        ExecuteNonQuery(connection, _dialect.SeedDefenseSummarySql);
    }

    private DbConnection OpenConnection()
    {
        var connection = _dialect.CreateConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static DefenseDecision ReadDecision(DbDataReader reader)
    {
        return new DefenseDecision(
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetInt64(4),
            reader.GetString(5),
            JsonSerializer.Deserialize<string[]>(reader.GetString(6)) ?? [],
            reader.GetString(8),
            DateTimeOffset.Parse(reader.GetString(9)),
            DateTimeOffset.Parse(reader.GetString(10)),
            reader.IsDBNull(7)
                ? null
                : JsonSerializer.Deserialize<DefenseScoreBreakdown>(reader.GetString(7)),
            reader.GetInt64(0));
    }

    private static void ExecuteNonQuery(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

public abstract class RelationalIntakeDeliveryStore : IIntakeDeliveryStore
{
    private readonly string _connectionString;
    private readonly RelationalAuditDialect _dialect;
    private readonly int _maxRecentEvents;

    protected RelationalIntakeDeliveryStore(IOptions<DefenseEngineOptions> options, RelationalAuditDialect dialect)
    {
        _connectionString = options.Value.Audit.ConnectionString;
        _dialect = dialect;
        _maxRecentEvents = options.Value.Audit.MaxRecentEvents;
        EnsureSchema();
    }

    public void Add(IntakeDeliveryRecord record)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO intake_delivery_events
            (
                delivery_type,
                channel,
                ip_address,
                reason,
                target,
                status,
                detail,
                attempted_at_utc
            )
            VALUES
            (
                @deliveryType,
                @channel,
                @ipAddress,
                @reason,
                @target,
                @status,
                @detail,
                @attemptedAtUtc
            );
            """;
        AddParameter(command, "@deliveryType", record.DeliveryType);
        AddParameter(command, "@channel", record.Channel);
        AddParameter(command, "@ipAddress", record.IpAddress);
        AddParameter(command, "@reason", record.Reason);
        AddParameter(command, "@target", record.Target);
        AddParameter(command, "@status", record.Status);
        AddParameter(command, "@detail", record.Detail);
        AddParameter(command, "@attemptedAtUtc", record.AttemptedAtUtc.UtcDateTime.ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<IntakeDeliveryRecord> GetRecent(int count)
    {
        var safeCount = Math.Clamp(count, 1, _maxRecentEvents);
        var results = new List<IntakeDeliveryRecord>(safeCount);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT
                delivery_type,
                channel,
                ip_address,
                reason,
                target,
                status,
                detail,
                attempted_at_utc
            FROM intake_delivery_events
            ORDER BY attempted_at_utc DESC, id DESC
            {_dialect.LimitClause("@limit")};
            """;
        AddParameter(command, "@limit", safeCount);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new IntakeDeliveryRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                DateTimeOffset.Parse(reader.GetString(7))));
        }

        return results;
    }

    public IntakeDeliveryMetrics GetMetrics()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                COUNT(*),
                COALESCE(SUM(CASE WHEN status = 'succeeded' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status = 'failed' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status = 'skipped' THEN 1 ELSE 0 END), 0),
                MAX(attempted_at_utc)
            FROM intake_delivery_events;
            """;

        using var reader = command.ExecuteReader();
        reader.Read();

        return new IntakeDeliveryMetrics(
            Convert.ToInt64(reader.GetValue(0)),
            Convert.ToInt64(reader.GetValue(1)),
            Convert.ToInt64(reader.GetValue(2)),
            Convert.ToInt64(reader.GetValue(3)),
            reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4)));
    }

    private void EnsureSchema()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = _dialect.IntakeDeliverySchemaSql;
        command.ExecuteNonQuery();
    }

    private DbConnection OpenConnection()
    {
        var connection = _dialect.CreateConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

public abstract class RelationalWebhookEventInbox : IWebhookEventInbox
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private readonly string _connectionString;
    private readonly RelationalAuditDialect _dialect;
    private readonly Channel<bool> _signalChannel = Channel.CreateUnbounded<bool>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    protected RelationalWebhookEventInbox(IOptions<DefenseEngineOptions> options, RelationalAuditDialect dialect)
    {
        _connectionString = options.Value.Audit.ConnectionString;
        _dialect = dialect;
        EnsureSchema();
        ResetLeases();
    }

    public Task<long> EnqueueAsync(IntakeWebhookEvent webhookEvent, CancellationToken cancellationToken)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = _dialect.InsertWebhookSql;
        AddParameter(command, "@eventType", webhookEvent.EventType);
        AddParameter(command, "@reason", webhookEvent.Reason);
        AddParameter(command, "@timestampUtc", webhookEvent.TimestampUtc.UtcDateTime.ToString("O"));
        AddParameter(command, "@payloadJson", JsonSerializer.Serialize(webhookEvent));
        var id = Convert.ToInt64(command.ExecuteScalar());
        _signalChannel.Writer.TryWrite(true);
        return Task.FromResult(id);
    }

    public async IAsyncEnumerable<WebhookInboxItem> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var next = TryClaimNext();
            if (next is not null)
            {
                yield return next;
                continue;
            }

            var signalTask = _signalChannel.Reader.WaitToReadAsync(cancellationToken).AsTask();
            var delayTask = Task.Delay(PollInterval, cancellationToken);
            var completedTask = await Task.WhenAny(signalTask, delayTask);
            if (completedTask == signalTask && await signalTask)
            {
                while (_signalChannel.Reader.TryRead(out _))
                {
                }
            }
        }
    }

    public Task CompleteAsync(long id, CancellationToken cancellationToken)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM webhook_intake_events
            WHERE id = @id;
            """;
        AddParameter(command, "@id", id);
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task AbandonAsync(long id, CancellationToken cancellationToken)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE webhook_intake_events
            SET
                status = 'pending',
                leased_at_utc = NULL
            WHERE id = @id;
            """;
        AddParameter(command, "@id", id);
        command.ExecuteNonQuery();
        _signalChannel.Writer.TryWrite(true);
        return Task.CompletedTask;
    }

    private WebhookInboxItem? TryClaimNext()
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = _dialect.ClaimWebhookSql;
        AddParameter(command, "@leasedAtUtc", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            transaction.Commit();
            return null;
        }

        var id = reader.GetInt64(0);
        var payloadJson = reader.GetString(1);
        reader.Close();
        transaction.Commit();

        var webhookEvent = JsonSerializer.Deserialize<IntakeWebhookEvent>(payloadJson)
            ?? throw new InvalidOperationException($"Failed to deserialize webhook inbox payload {id}.");

        return new WebhookInboxItem(id, webhookEvent);
    }

    private void EnsureSchema()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = _dialect.WebhookSchemaSql;
        command.ExecuteNonQuery();
    }

    private void ResetLeases()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE webhook_intake_events
            SET
                status = 'pending',
                leased_at_utc = NULL
            WHERE status = 'processing';
            """;
        command.ExecuteNonQuery();

        using var countCommand = connection.CreateCommand();
        countCommand.CommandText =
            """
            SELECT COUNT(*)
            FROM webhook_intake_events
            WHERE status = 'pending';
            """;
        var pendingCount = Convert.ToInt32(countCommand.ExecuteScalar());
        for (var i = 0; i < pendingCount; i++)
        {
            _signalChannel.Writer.TryWrite(true);
        }
    }

    private DbConnection OpenConnection()
    {
        var connection = _dialect.CreateConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

public sealed class RelationalAuditDialect
{
    public static readonly RelationalAuditDialect Postgres = new(
        connectionString => new NpgsqlConnection(connectionString),
        "LIMIT {0}",
        PostgresDefenseSchemaSql,
        PostgresSeedDefenseSummarySql,
        PostgresIntakeDeliverySchemaSql,
        PostgresWebhookSchemaSql,
        PostgresInsertFeedbackSql,
        PostgresInsertWebhookSql,
        PostgresClaimWebhookSql);

    public static readonly RelationalAuditDialect SqlServer = new(
        connectionString => new SqlConnection(connectionString),
        "OFFSET 0 ROWS FETCH NEXT {0} ROWS ONLY",
        SqlServerDefenseSchemaSql,
        SqlServerSeedDefenseSummarySql,
        SqlServerIntakeDeliverySchemaSql,
        SqlServerWebhookSchemaSql,
        SqlServerInsertFeedbackSql,
        SqlServerInsertWebhookSql,
        SqlServerClaimWebhookSql);

    private readonly Func<string, DbConnection> _connectionFactory;
    private readonly string _limitClauseFormat;

    private RelationalAuditDialect(
        Func<string, DbConnection> connectionFactory,
        string limitClauseFormat,
        string defenseSchemaSql,
        string seedDefenseSummarySql,
        string intakeDeliverySchemaSql,
        string webhookSchemaSql,
        string insertFeedbackSql,
        string insertWebhookSql,
        string claimWebhookSql)
    {
        _connectionFactory = connectionFactory;
        _limitClauseFormat = limitClauseFormat;
        DefenseSchemaSql = defenseSchemaSql;
        SeedDefenseSummarySql = seedDefenseSummarySql;
        IntakeDeliverySchemaSql = intakeDeliverySchemaSql;
        WebhookSchemaSql = webhookSchemaSql;
        InsertFeedbackSql = insertFeedbackSql;
        InsertWebhookSql = insertWebhookSql;
        ClaimWebhookSql = claimWebhookSql;
    }

    public string DefenseSchemaSql { get; }

    public string SeedDefenseSummarySql { get; }

    public string IntakeDeliverySchemaSql { get; }

    public string WebhookSchemaSql { get; }

    public string InsertFeedbackSql { get; }

    public string InsertWebhookSql { get; }

    public string ClaimWebhookSql { get; }

    public DbConnection CreateConnection(string connectionString)
    {
        return _connectionFactory(connectionString);
    }

    public string LimitClause(string parameterName)
    {
        return string.Format(_limitClauseFormat, parameterName);
    }

    private const string PostgresDefenseSchemaSql =
        """
        CREATE TABLE IF NOT EXISTS defense_events
        (
            id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            ip_address TEXT NOT NULL,
            action TEXT NOT NULL,
            score INTEGER NOT NULL,
            frequency BIGINT NOT NULL,
            path TEXT NOT NULL,
            signals_json TEXT NOT NULL,
            breakdown_json TEXT NULL,
            summary TEXT NOT NULL,
            observed_at_utc TEXT NOT NULL,
            decided_at_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_defense_events_decided_at
            ON defense_events (decided_at_utc DESC, id DESC);

        CREATE TABLE IF NOT EXISTS defense_feedback
        (
            id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            decision_id BIGINT NOT NULL REFERENCES defense_events (id),
            ip_address TEXT NOT NULL,
            original_action TEXT NOT NULL,
            updated_action TEXT NOT NULL,
            reason TEXT NOT NULL,
            actor TEXT NOT NULL,
            created_at_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_defense_feedback_created_at
            ON defense_feedback (created_at_utc DESC, id DESC);

        CREATE TABLE IF NOT EXISTS defense_event_summary
        (
            summary_key INTEGER PRIMARY KEY CHECK (summary_key = 1),
            total_decisions BIGINT NOT NULL,
            blocked_count BIGINT NOT NULL,
            observed_count BIGINT NOT NULL,
            latest_decision_at_utc TEXT NULL
        );

        ALTER TABLE defense_events
            ADD COLUMN IF NOT EXISTS breakdown_json TEXT NULL;
        """;

    private const string PostgresSeedDefenseSummarySql =
        """
        INSERT INTO defense_event_summary
        (
            summary_key,
            total_decisions,
            blocked_count,
            observed_count,
            latest_decision_at_utc
        )
        VALUES
        (
            1,
            0,
            0,
            0,
            NULL
        )
        ON CONFLICT (summary_key) DO NOTHING;

        UPDATE defense_event_summary
        SET
            total_decisions = (SELECT COUNT(*) FROM defense_events),
            blocked_count = (
                SELECT COALESCE(SUM(CASE WHEN action = 'blocked' THEN 1 ELSE 0 END), 0)
                FROM defense_events
            ),
            observed_count = (
                SELECT COALESCE(SUM(CASE WHEN action = 'observed' THEN 1 ELSE 0 END), 0)
                FROM defense_events
            ),
            latest_decision_at_utc = (SELECT MAX(decided_at_utc) FROM defense_events)
        WHERE summary_key = 1;
        """;

    private const string PostgresIntakeDeliverySchemaSql =
        """
        CREATE TABLE IF NOT EXISTS intake_delivery_events
        (
            id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            delivery_type TEXT NOT NULL,
            channel TEXT NOT NULL,
            ip_address TEXT NOT NULL,
            reason TEXT NOT NULL,
            target TEXT NOT NULL,
            status TEXT NOT NULL,
            detail TEXT NOT NULL,
            attempted_at_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_intake_delivery_events_attempted_at
            ON intake_delivery_events (attempted_at_utc DESC, id DESC);
        """;

    private const string PostgresWebhookSchemaSql =
        """
        CREATE TABLE IF NOT EXISTS webhook_intake_events
        (
            id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            event_type TEXT NOT NULL,
            reason TEXT NOT NULL,
            timestamp_utc TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            status TEXT NOT NULL,
            leased_at_utc TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_webhook_intake_events_status_id
            ON webhook_intake_events (status, id);
        """;

    private const string PostgresInsertFeedbackSql =
        """
        INSERT INTO defense_feedback
        (
            decision_id,
            ip_address,
            original_action,
            updated_action,
            reason,
            actor,
            created_at_utc
        )
        VALUES
        (
            @decisionId,
            @ipAddress,
            @originalAction,
            @updatedAction,
            @reason,
            @actor,
            @createdAtUtc
        )
        RETURNING id;
        """;

    private const string PostgresInsertWebhookSql =
        """
        INSERT INTO webhook_intake_events
        (
            event_type,
            reason,
            timestamp_utc,
            payload_json,
            status,
            leased_at_utc
        )
        VALUES
        (
            @eventType,
            @reason,
            @timestampUtc,
            @payloadJson,
            'pending',
            NULL
        )
        RETURNING id;
        """;

    private const string PostgresClaimWebhookSql =
        """
        UPDATE webhook_intake_events
        SET
            status = 'processing',
            leased_at_utc = @leasedAtUtc
        WHERE id = (
            SELECT id
            FROM webhook_intake_events
            WHERE status = 'pending'
            ORDER BY id
            FOR UPDATE SKIP LOCKED
            LIMIT 1
        )
        RETURNING id, payload_json;
        """;

    private const string SqlServerDefenseSchemaSql =
        """
        IF OBJECT_ID(N'defense_events', N'U') IS NULL
        BEGIN
            CREATE TABLE defense_events
            (
                id BIGINT IDENTITY(1,1) PRIMARY KEY,
                ip_address NVARCHAR(128) NOT NULL,
                action NVARCHAR(64) NOT NULL,
                score INT NOT NULL,
                frequency BIGINT NOT NULL,
                path NVARCHAR(2048) NOT NULL,
                signals_json NVARCHAR(MAX) NOT NULL,
                breakdown_json NVARCHAR(MAX) NULL,
                summary NVARCHAR(MAX) NOT NULL,
                observed_at_utc VARCHAR(64) NOT NULL,
                decided_at_utc VARCHAR(64) NOT NULL
            );
        END;

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_defense_events_decided_at' AND object_id = OBJECT_ID(N'defense_events'))
        BEGIN
            CREATE INDEX idx_defense_events_decided_at
                ON defense_events (decided_at_utc DESC, id DESC);
        END;

        IF OBJECT_ID(N'defense_feedback', N'U') IS NULL
        BEGIN
            CREATE TABLE defense_feedback
            (
                id BIGINT IDENTITY(1,1) PRIMARY KEY,
                decision_id BIGINT NOT NULL,
                ip_address NVARCHAR(128) NOT NULL,
                original_action NVARCHAR(64) NOT NULL,
                updated_action NVARCHAR(64) NOT NULL,
                reason NVARCHAR(MAX) NOT NULL,
                actor NVARCHAR(256) NOT NULL,
                created_at_utc VARCHAR(64) NOT NULL,
                CONSTRAINT fk_defense_feedback_decision
                    FOREIGN KEY (decision_id) REFERENCES defense_events (id)
            );
        END;

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_defense_feedback_created_at' AND object_id = OBJECT_ID(N'defense_feedback'))
        BEGIN
            CREATE INDEX idx_defense_feedback_created_at
                ON defense_feedback (created_at_utc DESC, id DESC);
        END;

        IF OBJECT_ID(N'defense_event_summary', N'U') IS NULL
        BEGIN
            CREATE TABLE defense_event_summary
            (
                summary_key INT PRIMARY KEY CHECK (summary_key = 1),
                total_decisions BIGINT NOT NULL,
                blocked_count BIGINT NOT NULL,
                observed_count BIGINT NOT NULL,
                latest_decision_at_utc VARCHAR(64) NULL
            );
        END;

        IF COL_LENGTH(N'defense_events', N'breakdown_json') IS NULL
        BEGIN
            ALTER TABLE defense_events
            ADD breakdown_json NVARCHAR(MAX) NULL;
        END;
        """;

    private const string SqlServerSeedDefenseSummarySql =
        """
        IF NOT EXISTS (SELECT 1 FROM defense_event_summary WHERE summary_key = 1)
        BEGIN
            INSERT INTO defense_event_summary
            (
                summary_key,
                total_decisions,
                blocked_count,
                observed_count,
                latest_decision_at_utc
            )
            VALUES
            (
                1,
                0,
                0,
                0,
                NULL
            );
        END;

        UPDATE defense_event_summary
        SET
            total_decisions = (SELECT COUNT(*) FROM defense_events),
            blocked_count = (
                SELECT COALESCE(SUM(CASE WHEN action = 'blocked' THEN 1 ELSE 0 END), 0)
                FROM defense_events
            ),
            observed_count = (
                SELECT COALESCE(SUM(CASE WHEN action = 'observed' THEN 1 ELSE 0 END), 0)
                FROM defense_events
            ),
            latest_decision_at_utc = (SELECT MAX(decided_at_utc) FROM defense_events)
        WHERE summary_key = 1;
        """;

    private const string SqlServerIntakeDeliverySchemaSql =
        """
        IF OBJECT_ID(N'intake_delivery_events', N'U') IS NULL
        BEGIN
            CREATE TABLE intake_delivery_events
            (
                id BIGINT IDENTITY(1,1) PRIMARY KEY,
                delivery_type NVARCHAR(64) NOT NULL,
                channel NVARCHAR(64) NOT NULL,
                ip_address NVARCHAR(128) NOT NULL,
                reason NVARCHAR(MAX) NOT NULL,
                target NVARCHAR(2048) NOT NULL,
                status NVARCHAR(64) NOT NULL,
                detail NVARCHAR(MAX) NOT NULL,
                attempted_at_utc VARCHAR(64) NOT NULL
            );
        END;

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_intake_delivery_events_attempted_at' AND object_id = OBJECT_ID(N'intake_delivery_events'))
        BEGIN
            CREATE INDEX idx_intake_delivery_events_attempted_at
                ON intake_delivery_events (attempted_at_utc DESC, id DESC);
        END;
        """;

    private const string SqlServerWebhookSchemaSql =
        """
        IF OBJECT_ID(N'webhook_intake_events', N'U') IS NULL
        BEGIN
            CREATE TABLE webhook_intake_events
            (
                id BIGINT IDENTITY(1,1) PRIMARY KEY,
                event_type NVARCHAR(128) NOT NULL,
                reason NVARCHAR(MAX) NOT NULL,
                timestamp_utc VARCHAR(64) NOT NULL,
                payload_json NVARCHAR(MAX) NOT NULL,
                status NVARCHAR(64) NOT NULL,
                leased_at_utc VARCHAR(64) NULL
            );
        END;

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'idx_webhook_intake_events_status_id' AND object_id = OBJECT_ID(N'webhook_intake_events'))
        BEGIN
            CREATE INDEX idx_webhook_intake_events_status_id
                ON webhook_intake_events (status, id);
        END;
        """;

    private const string SqlServerInsertFeedbackSql =
        """
        INSERT INTO defense_feedback
        (
            decision_id,
            ip_address,
            original_action,
            updated_action,
            reason,
            actor,
            created_at_utc
        )
        VALUES
        (
            @decisionId,
            @ipAddress,
            @originalAction,
            @updatedAction,
            @reason,
            @actor,
            @createdAtUtc
        );

        SELECT CONVERT(BIGINT, SCOPE_IDENTITY());
        """;

    private const string SqlServerInsertWebhookSql =
        """
        INSERT INTO webhook_intake_events
        (
            event_type,
            reason,
            timestamp_utc,
            payload_json,
            status,
            leased_at_utc
        )
        VALUES
        (
            @eventType,
            @reason,
            @timestampUtc,
            @payloadJson,
            'pending',
            NULL
        );

        SELECT CONVERT(BIGINT, SCOPE_IDENTITY());
        """;

    private const string SqlServerClaimWebhookSql =
        """
        ;WITH next_event AS
        (
            SELECT TOP (1)
                id,
                payload_json
            FROM webhook_intake_events WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE status = 'pending'
            ORDER BY id
        )
        UPDATE next_event
        SET
            status = 'processing',
            leased_at_utc = @leasedAtUtc
        OUTPUT INSERTED.id, INSERTED.payload_json;
        """;
}
