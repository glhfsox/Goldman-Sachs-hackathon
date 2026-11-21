using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Notifier.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PartialDueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_notifications_status_send_at",
                table: "notifications");

            migrationBuilder.CreateIndex(
                name: "IX_notifications_due",
                table: "notifications",
                column: "send_at",
                filter: "status IN ('pending', 'failed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_notifications_due",
                table: "notifications");

            migrationBuilder.CreateIndex(
                name: "IX_notifications_status_send_at",
                table: "notifications",
                columns: new[] { "status", "send_at" });
        }
    }
}
