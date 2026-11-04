using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kasper.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProjectsSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    FolderPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    NotionQuery = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SlackChannel = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    GitHubRepo = table.Column<string>(type: "TEXT", maxLength: 140, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: false),
                    LastBriefing = table.Column<string>(type: "TEXT", nullable: true),
                    LastBriefingAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Schedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Request = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    TimeOfDay = table.Column<string>(type: "TEXT", maxLength: 5, nullable: false),
                    Days = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastRunUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastWorkflowId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Schedules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Projects_Name",
                table: "Projects",
                column: "Name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Projects");

            migrationBuilder.DropTable(
                name: "Schedules");
        }
    }
}
