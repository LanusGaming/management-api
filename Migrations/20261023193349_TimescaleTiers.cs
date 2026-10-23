using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Management.Api.Migrations
{
    /// <summary>
    /// Moves metrics to TimescaleDB-managed downsampling: raw 1 s samples in a hypertable kept for a day,
    /// plus 1 min / 5 min / 15 min continuous aggregates (avg/min/max) kept for 30 days / 1 year / forever.
    /// The old table is renamed to ContainerMetricsLegacy instead of being converted, so its data can be
    /// backfilled into the aggregates separately (see README) and dropped afterwards.
    /// </summary>
    public partial class TimescaleTiers : Migration
    {
        // View name, bucket width, how far back each refresh recomputes, how long buckets are kept.
        // The refresh window must stay well inside the raw data's 1 day retention: refreshing a range
        // whose raw samples are already gone would empty those buckets again.
        private static readonly (string View, string Bucket, string RefreshStart, string Retention)[] Tiers =
        [
            ("ContainerMetrics1m", "1 minute", "3 hours", "30 days"),
            ("ContainerMetrics5m", "5 minutes", "6 hours", "365 days"),
            ("ContainerMetrics15m", "15 minutes", "12 hours", null),
        ];

        private static readonly string[] Metrics =
            ["CpuPercent", "MemoryBytes", "NetworkRxBytesPerSecond", "NetworkTxBytesPerSecond"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "ContainerMetrics" RENAME TO "ContainerMetricsLegacy";
                ALTER TABLE "ContainerMetricsLegacy" RENAME CONSTRAINT "PK_ContainerMetrics" TO "PK_ContainerMetricsLegacy";
                ALTER INDEX "IX_ContainerMetrics_RecordedAt" RENAME TO "IX_ContainerMetricsLegacy_RecordedAt";
                """);

            migrationBuilder.CreateTable(
                name: "ContainerMetrics",
                columns: table => new
                {
                    ContainerName = table.Column<string>(type: "text", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ContainerId = table.Column<string>(type: "text", nullable: false),
                    CpuPercent = table.Column<double>(type: "double precision", nullable: false),
                    MemoryBytes = table.Column<long>(type: "bigint", nullable: false),
                    NetworkRxBytesPerSecond = table.Column<double>(type: "double precision", nullable: false),
                    NetworkTxBytesPerSecond = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContainerMetrics", x => new { x.ContainerName, x.RecordedAt });
                });

            // Hourly chunks, so the 1 day retention drops raw samples close to the 1 day mark
            migrationBuilder.Sql("""
                SELECT create_hypertable('"ContainerMetrics"', by_range('RecordedAt', INTERVAL '1 hour'));
                SELECT add_retention_policy('"ContainerMetrics"', INTERVAL '1 day');
                """);

            foreach (var (view, bucket, refreshStart, retention) in Tiers)
            {
                var aggregates = string.Join(",\n    ", Metrics.Select(m =>
                    $"avg(\"{m}\") AS \"{m}Avg\", min(\"{m}\") AS \"{m}Min\", max(\"{m}\") AS \"{m}Max\""));

                // Continuous aggregates can't be created inside a transaction
                migrationBuilder.Sql($"""
                    CREATE MATERIALIZED VIEW "{view}"
                    WITH (timescaledb.continuous, timescaledb.materialized_only = true) AS
                    SELECT time_bucket(INTERVAL '{bucket}', "RecordedAt") AS "Bucket", "ContainerName",
                        {aggregates}
                    FROM "ContainerMetrics"
                    GROUP BY 1, 2
                    WITH NO DATA;
                    """, suppressTransaction: true);

                migrationBuilder.Sql($"""
                    SELECT add_continuous_aggregate_policy('"{view}"',
                        start_offset => INTERVAL '{refreshStart}',
                        end_offset => INTERVAL '{bucket}',
                        schedule_interval => INTERVAL '{bucket}');
                    """);

                if (retention != null)
                    migrationBuilder.Sql($"""SELECT add_retention_policy('"{view}"', INTERVAL '{retention}');""");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (view, _, _, _) in Tiers.Reverse())
                migrationBuilder.Sql($"""DROP MATERIALIZED VIEW IF EXISTS "{view}";""", suppressTransaction: true);

            migrationBuilder.DropTable(name: "ContainerMetrics");

            // Only restorable while the legacy table hasn't been dropped after the backfill
            migrationBuilder.Sql("""
                ALTER TABLE "ContainerMetricsLegacy" RENAME TO "ContainerMetrics";
                ALTER TABLE "ContainerMetrics" RENAME CONSTRAINT "PK_ContainerMetricsLegacy" TO "PK_ContainerMetrics";
                ALTER INDEX "IX_ContainerMetricsLegacy_RecordedAt" RENAME TO "IX_ContainerMetrics_RecordedAt";
                """);
        }
    }
}
