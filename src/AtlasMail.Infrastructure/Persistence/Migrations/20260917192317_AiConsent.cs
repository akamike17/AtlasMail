using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AtlasMail.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AiConsent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AiConsent",
                table: "Mailboxes",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AiConsent",
                table: "Mailboxes");
        }
    }
}
