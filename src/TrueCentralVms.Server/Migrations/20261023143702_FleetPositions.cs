using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrueCentralVms.Server.Migrations
{
    /// <summary>
    /// Posiciones GPS de los vehículos (módulo Flota). Una tabla PARTICIONADA por día (UTC): una flota de 300 vehículos con una
    /// posición cada 10 s son ~2,6 millones de filas al día, y la retención borra particiones enteras en vez de filas. No es una
    /// entidad de EF: la escribe <c>FleetPositionService</c> con COPY y crea las particiones a medida que las necesita.
    /// </summary>
    public partial class FleetPositions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE "VehiclePositions" (
                    "VehicleId" integer NOT NULL,
                    "At" timestamp with time zone NOT NULL,
                    "ReceivedAt" timestamp with time zone NOT NULL,
                    "Latitude" double precision NOT NULL,
                    "Longitude" double precision NOT NULL,
                    "SpeedKmh" real NOT NULL,
                    "HeadingDeg" real NOT NULL,
                    "AltitudeM" real NOT NULL,
                    "Satellites" smallint NOT NULL,
                    "Hdop" real NOT NULL,
                    "Retransmitted" boolean NOT NULL,
                    "DailyMileageM" integer NOT NULL,
                    PRIMARY KEY ("VehicleId", "At")
                ) PARTITION BY RANGE ("At");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP TABLE IF EXISTS "VehiclePositions";""");
        }
    }
}
