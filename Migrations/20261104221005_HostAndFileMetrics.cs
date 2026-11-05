using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Management.Api.Migrations
{
    /// <summary>
    /// Adds per-container file read/write rates and the HostMetrics table, and swaps the continuous aggregates
    /// for plain rollup hypertables filled by TimescaleDB jobs. A continuous aggregate can't get new columns:
    /// it would have to be recreated, which only rebuilds what the 1 day of raw samples still covers and loses
    /// all older history. The rollup tables take ALTER TABLE ADD COLUMN, and this migration copies every
    /// existing bucket over, so the history is kept. Rows in older buckets have NULL for the new columns.
    /// </summary>
    public partial class HostAndFileMetrics : Migration
    {
        // Table suffix, bucket width, how far back each run recomputes, chunk size, how long buckets are kept.
        // Same widths, windows and retention as the TimescaleTiers aggregates. The window must stay inside the
        // raw data's 1 day retention: recomputing a range whose raw samples are gone would write empty buckets.
        private static readonly (string Suffix, string Bucket, string Lookback, string Chunk, string Retention)[] Tiers =
        [
            ("1m", "1 minute", "3 hours", "1 day", "30 days"),
            ("5m", "5 minutes", "6 hours", "7 days", "365 days"),
            ("15m", "15 minutes", "12 hours", "30 days", null),
        ];

        private static readonly string[] AggregatedMetrics =
            ["CpuPercent", "MemoryBytes", "NetworkRxBytesPerSecond", "NetworkTxBytesPerSecond"];

        private static readonly string[] ContainerMetrics =
            [.. AggregatedMetrics, "FileReadBytesPerSecond", "FileWriteBytesPerSecond"];

        private static IEnumerable<string> RollupColumns(IEnumerable<string> metrics) =>
            metrics.SelectMany(m => new[] { $"\"{m}Avg\"", $"\"{m}Min\"", $"\"{m}Max\"" });

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The aggregates' own refresh/retention jobs must not run while they're copied and dropped: a refresh
            // working on the invalidation log at the same time makes the DROP fail ("tuple concurrently
            // deleted"). So they're paused first, committed on their own (outside the migration transaction)
            // so the scheduler sees it, and then any run already in progress is waited for. If the migration
            // fails after this, the old jobs stay paused until it's retried (alter_job(id, scheduled => true)).
            var oldAggregates = string.Join(", ", Tiers.Select(t => $"'ContainerMetrics{t.Suffix}'"));
            migrationBuilder.Sql($"""
                SELECT alter_job(job_id, scheduled => false)
                FROM timescaledb_information.jobs WHERE hypertable_name IN ({oldAggregates});
                """, suppressTransaction: true);
            migrationBuilder.Sql($$"""
                DO $$
                BEGIN
                    PERFORM pg_sleep(2);
                    FOR i IN 1..300 LOOP
                        IF NOT EXISTS (SELECT 1 FROM timescaledb_information.job_stats
                                       WHERE hypertable_name IN ({{oldAggregates}}) AND job_status = 'Running') THEN
                            RETURN;
                        END IF;
                        PERFORM pg_sleep(1);
                    END LOOP;
                    RAISE EXCEPTION 'continuous aggregate jobs still running after 5 minutes';
                END $$;
                """, suppressTransaction: true);

            migrationBuilder.AddColumn<double>(
                name: "FileReadBytesPerSecond",
                table: "ContainerMetrics",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "FileWriteBytesPerSecond",
                table: "ContainerMetrics",
                type: "double precision",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HostMetrics",
                columns: table => new
                {
                    Metric = table.Column<string>(type: "text", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Value = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostMetrics", x => new { x.Metric, x.RecordedAt });
                });

            migrationBuilder.Sql("""
                SELECT create_hypertable('"HostMetrics"', by_range('RecordedAt', INTERVAL '1 hour'));
                SELECT add_retention_policy('"HostMetrics"', INTERVAL '1 day');
                """);

            var containerColumns = string.Join(", ", RollupColumns(ContainerMetrics).Select(c => $"{c} double precision"));
            var copiedColumns = string.Join(", ", RollupColumns(AggregatedMetrics));

            foreach (var (suffix, _, _, chunk, retention) in Tiers)
            {
                var table = $"ContainerMetrics{suffix}";

                // Same name and columns as the aggregate it replaces, so the history query doesn't change
                migrationBuilder.Sql($"""
                    ALTER MATERIALIZED VIEW "{table}" RENAME TO "{table}Aggregate";

                    CREATE TABLE "{table}" (
                        "Bucket" timestamp with time zone NOT NULL,
                        "ContainerName" text NOT NULL,
                        {containerColumns},
                        CONSTRAINT "PK_{table}" PRIMARY KEY ("ContainerName", "Bucket"));
                    SELECT create_hypertable('"{table}"', by_range('Bucket', INTERVAL '{chunk}'));

                    INSERT INTO "{table}" ("Bucket", "ContainerName", {copiedColumns})
                    SELECT "Bucket", "ContainerName", {copiedColumns} FROM "{table}Aggregate";

                    DROP MATERIALIZED VIEW "{table}Aggregate";
                    """);

                migrationBuilder.Sql($"""
                    CREATE TABLE "HostMetrics{suffix}" (
                        "Bucket" timestamp with time zone NOT NULL,
                        "Metric" text NOT NULL,
                        "ValueAvg" double precision NOT NULL,
                        "ValueMin" double precision NOT NULL,
                        "ValueMax" double precision NOT NULL,
                        CONSTRAINT "PK_HostMetrics{suffix}" PRIMARY KEY ("Metric", "Bucket"));
                    SELECT create_hypertable('"HostMetrics{suffix}"', by_range('Bucket', INTERVAL '{chunk}'));
                    """);

                if (retention != null)
                    migrationBuilder.Sql($"""
                        SELECT add_retention_policy('"{table}"', INTERVAL '{retention}');
                        SELECT add_retention_policy('"HostMetrics{suffix}"', INTERVAL '{retention}');
                        """);
            }

            // One procedure per raw table; each job passes its tier in config. Only finished buckets are written
            // (up to the start of the current one), and recomputed buckets are overwritten, so a run that fails
            // or overlaps the previous one does no harm.
            var aggregates = string.Join(",\n        ", ContainerMetrics.Select(m =>
                $"avg(\"{m}\"), min(\"{m}\"), max(\"{m}\")"));
            var updates = string.Join(", ", RollupColumns(ContainerMetrics).Select(c => $"{c} = EXCLUDED.{c}"));
            var rollupColumns = string.Join(", ", RollupColumns(ContainerMetrics));

            migrationBuilder.Sql($$"""
                CREATE PROCEDURE rollup_container_metrics(job_id int, config jsonb) LANGUAGE plpgsql AS $proc$
                DECLARE
                    width interval := (config->>'bucket')::interval;
                    since timestamptz := time_bucket(width, now() - (config->>'lookback')::interval);
                    upto timestamptz := time_bucket(width, now());
                BEGIN
                    EXECUTE format($sql$
                        INSERT INTO %I ("Bucket", "ContainerName", {{rollupColumns}})
                        SELECT time_bucket($1, "RecordedAt"), "ContainerName",
                            {{aggregates}}
                        FROM "ContainerMetrics"
                        WHERE "RecordedAt" >= $2 AND "RecordedAt" < $3
                        GROUP BY 1, 2
                        ON CONFLICT ("ContainerName", "Bucket") DO UPDATE SET {{updates}}
                        $sql$, config->>'table')
                    USING width, since, upto;
                END
                $proc$;

                CREATE PROCEDURE rollup_host_metrics(job_id int, config jsonb) LANGUAGE plpgsql AS $proc$
                DECLARE
                    width interval := (config->>'bucket')::interval;
                    since timestamptz := time_bucket(width, now() - (config->>'lookback')::interval);
                    upto timestamptz := time_bucket(width, now());
                BEGIN
                    EXECUTE format($sql$
                        INSERT INTO %I ("Bucket", "Metric", "ValueAvg", "ValueMin", "ValueMax")
                        SELECT time_bucket($1, "RecordedAt"), "Metric", avg("Value"), min("Value"), max("Value")
                        FROM "HostMetrics"
                        WHERE "RecordedAt" >= $2 AND "RecordedAt" < $3
                        GROUP BY 1, 2
                        ON CONFLICT ("Metric", "Bucket") DO UPDATE SET
                            "ValueAvg" = EXCLUDED."ValueAvg", "ValueMin" = EXCLUDED."ValueMin", "ValueMax" = EXCLUDED."ValueMax"
                        $sql$, config->>'table')
                    USING width, since, upto;
                END
                $proc$;
                """);

            // Every bucket width, like the aggregates' refresh policies. The first runs also fill the gap between
            // the aggregates' last refresh and now, since their lookback reaches further back than that.
            foreach (var (suffix, bucket, lookback, _, _) in Tiers)
            {
                foreach (var (procedure, table) in new[] {
                    ("rollup_container_metrics", $"ContainerMetrics{suffix}"),
                    ("rollup_host_metrics", $"HostMetrics{suffix}") })
                {
                    migrationBuilder.Sql($$"""
                        SELECT add_job('{{procedure}}', INTERVAL '{{bucket}}',
                            config => '{"table": "{{table}}", "bucket": "{{bucket}}", "lookback": "{{lookback}}"}');
                        """);
                }
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Going back to continuous aggregates would rebuild them from the last day of raw samples only, so
            // reverting means restoring the database from a backup taken before this migration
            throw new NotSupportedException("HostAndFileMetrics can't be reverted without losing history; restore a backup instead.");
        }
    }
}
