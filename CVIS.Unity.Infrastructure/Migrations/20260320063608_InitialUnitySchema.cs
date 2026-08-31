using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CVIS.Unity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialUnitySchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "unity");

            migrationBuilder.CreateTable(
                name: "PlatformBaselines",
                schema: "unity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlatformId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PlatformName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    LastSNOWTicket = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastUpdate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Attributes = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AttributesHash = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformBaselines", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PolicyDriftEval",
                schema: "unity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PolicyId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PolicyName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BaselinePolicyID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PolicyDriftEvalDetailsID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RunTimestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExecutionId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Differences = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyDriftEval", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PolicyDriftEvalDetails",
                schema: "unity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PolicyId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DriftVersion = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Attributes = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AttributesHash = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyDriftEvalDetails", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PolicyEvents",
                schema: "unity",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PolicyId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    EventName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Metadata = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyEvents", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlatformBaselines",
                schema: "unity");

            migrationBuilder.DropTable(
                name: "PolicyDriftEval",
                schema: "unity");

            migrationBuilder.DropTable(
                name: "PolicyDriftEvalDetails",
                schema: "unity");

            migrationBuilder.DropTable(
                name: "PolicyEvents",
                schema: "unity");
        }
    }
}
