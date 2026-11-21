using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlertApi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProposalIdempotencyKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "idempotency_key",
                table: "proposals",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UQ_proposals_idempotency_key",
                table: "proposals",
                column: "idempotency_key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UQ_proposals_idempotency_key",
                table: "proposals");

            migrationBuilder.DropColumn(
                name: "idempotency_key",
                table: "proposals");
        }
    }
}
