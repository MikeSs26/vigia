using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigia.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TimeSeriesSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE metric_series (
                    id          int GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    tenant_id   int  NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
                    source_id   int  NOT NULL REFERENCES sources(id) ON DELETE CASCADE,
                    name        text NOT NULL,
                    unit        text NOT NULL,
                    labels      jsonb NOT NULL DEFAULT '{}',
                    created_at  timestamptz NOT NULL DEFAULT now(),
                    CONSTRAINT metric_series_identity
                        UNIQUE (tenant_id, source_id, name, labels)
                );
                """);

            // Range partitioning by time makes expiry a DROP TABLE on a partition rather
            // than a bulk DELETE, which would bloat the heap and trigger autovacuum.
            migrationBuilder.Sql("""
                CREATE TABLE metric_points (
                    series_id int              NOT NULL REFERENCES metric_series(id) ON DELETE CASCADE,
                    ts        timestamptz      NOT NULL,
                    value     double precision NOT NULL
                ) PARTITION BY RANGE (ts);
                """);

            // BRIN suits append-mostly, time-ordered data: a kilobyte-scale index where
            // a btree over the same column would be gigabyte-scale.
            migrationBuilder.Sql("CREATE INDEX metric_points_ts_brin ON metric_points USING brin (ts);");
            migrationBuilder.Sql("CREATE INDEX metric_points_series_ts ON metric_points (series_id, ts DESC);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS metric_points;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS metric_series;");
        }
    }
}
