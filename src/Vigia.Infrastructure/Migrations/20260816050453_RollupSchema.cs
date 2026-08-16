using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigia.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RollupSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // count/sum/min/max/last rather than an average, because that set is
            // re-aggregatable: the 1h table is computed from the 1m table without
            // returning to raw data, and averages are derived at read time. Storing
            // an average would make the hourly table uncomputable from the minutely one.
            foreach (var table in new[] { "metric_rollups_1m", "metric_rollups_1h" })
            {
                migrationBuilder.Sql($"""
                    CREATE TABLE {table} (
                        series_id int              NOT NULL REFERENCES metric_series(id) ON DELETE CASCADE,
                        bucket    timestamptz      NOT NULL,
                        count     int              NOT NULL,
                        sum       double precision NOT NULL,
                        min       double precision NOT NULL,
                        max       double precision NOT NULL,
                        last      double precision NOT NULL,
                        PRIMARY KEY (series_id, bucket)
                    ) PARTITION BY RANGE (bucket);
                    """);
            }

            // The watermark is the first bucket NOT yet known to be complete.
            // Persisting it is what makes a cold start and a restart after a long
            // outage the same operation: aggregate from the watermark forward.
            migrationBuilder.Sql("""
                CREATE TABLE rollup_watermarks (
                    granularity text        PRIMARY KEY,
                    watermark   timestamptz NOT NULL,
                    updated_at  timestamptz NOT NULL
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS rollup_watermarks;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS metric_rollups_1h;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS metric_rollups_1m;");
        }
    }
}
