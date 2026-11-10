using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kasper.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WowFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoApproved",
                table: "Payments",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "InvoiceCheckedAtUtc",
                table: "Payments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceState",
                table: "Payments",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastReminderAtUtc",
                table: "Payments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrevReceiptHash",
                table: "Payments",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReceiptHash",
                table: "Payments",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ReceiptSeq",
                table: "Payments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Quotes",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SourceProductId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Brand = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Merchant = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ImageUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Price = table.Column<decimal>(type: "TEXT", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Quotes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ReceiptSeq",
                table: "Payments",
                column: "ReceiptSeq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_CreatedAtUtc",
                table: "Quotes",
                column: "CreatedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Quotes");

            migrationBuilder.DropIndex(
                name: "IX_Payments_ReceiptSeq",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "AutoApproved",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "InvoiceCheckedAtUtc",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "InvoiceState",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "LastReminderAtUtc",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "PrevReceiptHash",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ReceiptHash",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ReceiptSeq",
                table: "Payments");
        }
    }
}
