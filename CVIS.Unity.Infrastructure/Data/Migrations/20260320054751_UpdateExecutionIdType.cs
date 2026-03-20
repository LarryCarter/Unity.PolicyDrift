using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CVIS.Unity.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class UpdateExecutionIdType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "MetadataJson",
                schema: "unity",
                table: "PolicyEvents",
                newName: "Metadata");

            migrationBuilder.RenameColumn(
                name: "LastUpdated",
                schema: "unity",
                table: "PlatformBaselines",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "AttributesJson",
                schema: "unity",
                table: "PlatformBaselines",
                newName: "PlatformId");

            migrationBuilder.RenameColumn(
                name: "Active",
                schema: "unity",
                table: "PlatformBaselines",
                newName: "IsActive");

            migrationBuilder.AlterColumn<string>(
                name: "LastSNOWTicket",
                schema: "unity",
                table: "PlatformBaselines",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                schema: "unity",
                table: "PlatformBaselines",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");

            migrationBuilder.AddColumn<string>(
                name: "Attributes",
                schema: "unity",
                table: "PlatformBaselines",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AttributesHash",
                schema: "unity",
                table: "PlatformBaselines",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastUpdate",
                schema: "unity",
                table: "PlatformBaselines",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlatformName",
                schema: "unity",
                table: "PlatformBaselines",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                schema: "unity",
                table: "PlatformBaselines",
                type: "int",
                nullable: false,
                defaultValue: 0);

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PolicyDriftEval",
                schema: "unity");

            migrationBuilder.DropTable(
                name: "PolicyDriftEvalDetails",
                schema: "unity");

            migrationBuilder.DropColumn(
                name: "Attributes",
                schema: "unity",
                table: "PlatformBaselines");

            migrationBuilder.DropColumn(
                name: "AttributesHash",
                schema: "unity",
                table: "PlatformBaselines");

            migrationBuilder.DropColumn(
                name: "LastUpdate",
                schema: "unity",
                table: "PlatformBaselines");

            migrationBuilder.DropColumn(
                name: "PlatformName",
                schema: "unity",
                table: "PlatformBaselines");

            migrationBuilder.DropColumn(
                name: "Version",
                schema: "unity",
                table: "PlatformBaselines");

            migrationBuilder.RenameColumn(
                name: "Metadata",
                schema: "unity",
                table: "PolicyEvents",
                newName: "MetadataJson");

            migrationBuilder.RenameColumn(
                name: "PlatformId",
                schema: "unity",
                table: "PlatformBaselines",
                newName: "AttributesJson");

            migrationBuilder.RenameColumn(
                name: "IsActive",
                schema: "unity",
                table: "PlatformBaselines",
                newName: "Active");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "unity",
                table: "PlatformBaselines",
                newName: "LastUpdated");

            migrationBuilder.AlterColumn<string>(
                name: "LastSNOWTicket",
                schema: "unity",
                table: "PlatformBaselines",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Id",
                schema: "unity",
                table: "PlatformBaselines",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");
        }
    }
}
