using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kasper.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PaymentsInvoicing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Payments",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PayPalInvoiceId",
                table: "Payments",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Recipient",
                table: "Payments",
                type: "TEXT",
                maxLength: 320,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "PayPalInvoiceId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "Recipient",
                table: "Payments");
        }
    }
}
