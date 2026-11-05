using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TransportAnalyzer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "event",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<string>(type: "text", nullable: false),
                    source_key = table.Column<string>(type: "text", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    occurred_on = table.Column<DateOnly>(type: "date", nullable: false),
                    occurred_time = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    latitude = table.Column<double>(type: "double precision", nullable: true),
                    longitude = table.Column<double>(type: "double precision", nullable: true),
                    state = table.Column<string>(type: "text", nullable: true),
                    country = table.Column<string>(type: "text", nullable: true),
                    severity = table.Column<string>(type: "text", nullable: false),
                    fatalities = table.Column<int>(type: "integer", nullable: false),
                    serious_injuries = table.Column<int>(type: "integer", nullable: false),
                    minor_injuries = table.Column<int>(type: "integer", nullable: false),
                    ground_fatalities = table.Column<int>(type: "integer", nullable: true),
                    unattributed_fatalities = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    unattributed_serious_injuries = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    unattributed_minor_injuries = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    operation_class = table.Column<string>(type: "text", nullable: true),
                    phase = table.Column<string>(type: "text", nullable: true),
                    weather_summary = table.Column<string>(type: "text", nullable: true),
                    narrative = table.Column<string>(type: "text", nullable: true),
                    probable_cause = table.Column<string>(type: "text", nullable: true),
                    investigation_status = table.Column<string>(type: "text", nullable: true),
                    source_payload = table.Column<string>(type: "jsonb", nullable: false),
                    ingest_run_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "event_operator",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_operator", x => new { x.event_id, x.operator_id, x.role });
                });

            migrationBuilder.CreateTable(
                name: "exposure",
                columns: table => new
                {
                    exposure_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<string>(type: "text", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    vehicle_class = table.Column<string>(type: "text", nullable: true),
                    regulation_class = table.Column<string>(type: "text", nullable: true),
                    metric = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<double>(type: "double precision", nullable: false),
                    provenance = table.Column<string>(type: "text", nullable: false),
                    standard_error = table.Column<double>(type: "double precision", nullable: true),
                    assumptions = table.Column<string>(type: "jsonb", nullable: true),
                    ingest_run_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_exposure", x => x.exposure_id);
                });

            migrationBuilder.CreateTable(
                name: "ingest_run",
                columns: table => new
                {
                    ingest_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    parameters = table.Column<string>(type: "jsonb", nullable: false),
                    raw_files = table.Column<string>(type: "jsonb", nullable: false),
                    row_counts = table.Column<string>(type: "jsonb", nullable: false),
                    unmapped = table.Column<string>(type: "jsonb", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ingest_run", x => x.ingest_run_id);
                });

            migrationBuilder.CreateTable(
                name: "operator",
                columns: table => new
                {
                    operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    identifiers = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_operator", x => x.operator_id);
                });

            migrationBuilder.CreateTable(
                name: "vocabulary",
                columns: table => new
                {
                    attribute = table.Column<string>(type: "text", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    parent_code = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vocabulary", x => new { x.attribute, x.code });
                });

            migrationBuilder.CreateTable(
                name: "vocabulary_map",
                columns: table => new
                {
                    source_id = table.Column<string>(type: "text", nullable: false),
                    attribute = table.Column<string>(type: "text", nullable: false),
                    source_column = table.Column<string>(type: "text", nullable: false),
                    source_value = table.Column<string>(type: "text", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vocabulary_map", x => new { x.source_id, x.attribute, x.source_column, x.source_value });
                });

            migrationBuilder.CreateTable(
                name: "vehicle",
                columns: table => new
                {
                    vehicle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    vehicle_no = table.Column<int>(type: "integer", nullable: false),
                    registration = table.Column<string>(type: "text", nullable: true),
                    make = table.Column<string>(type: "text", nullable: true),
                    model = table.Column<string>(type: "text", nullable: true),
                    year_built = table.Column<int>(type: "integer", nullable: true),
                    damage = table.Column<string>(type: "text", nullable: true),
                    vehicle_class = table.Column<string>(type: "text", nullable: false),
                    operator_type = table.Column<string>(type: "text", nullable: false),
                    regulation_class = table.Column<string>(type: "text", nullable: false),
                    maintenance_regime = table.Column<string>(type: "text", nullable: false),
                    occupants = table.Column<int>(type: "integer", nullable: true),
                    crew_count = table.Column<int>(type: "integer", nullable: true),
                    vehicle_age_years = table.Column<int>(type: "integer", nullable: true),
                    occupant_fatalities = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    occupant_serious_injuries = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    occupant_minor_injuries = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    nonoccupant_fatalities = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    nonoccupant_serious_injuries = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    nonoccupant_minor_injuries = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    source_payload = table.Column<string>(type: "jsonb", nullable: false),
                    ingest_run_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vehicle", x => x.vehicle_id);
                    table.ForeignKey(
                        name: "fk_vehicle_event_event_id",
                        column: x => x.event_id,
                        principalTable: "event",
                        principalColumn: "event_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_event_source_id_source_key",
                table: "event",
                columns: new[] { "source_id", "source_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vehicle_event_id_vehicle_no",
                table: "vehicle",
                columns: new[] { "event_id", "vehicle_no" },
                unique: true);

            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ux_exposure_key ON exposure (source_id, mode, year, vehicle_class, regulation_class, metric) NULLS NOT DISTINCT;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "event_operator");

            migrationBuilder.DropTable(
                name: "exposure");

            migrationBuilder.DropTable(
                name: "ingest_run");

            migrationBuilder.DropTable(
                name: "operator");

            migrationBuilder.DropTable(
                name: "vehicle");

            migrationBuilder.DropTable(
                name: "vocabulary");

            migrationBuilder.DropTable(
                name: "vocabulary_map");

            migrationBuilder.DropTable(
                name: "event");
        }
    }
}
