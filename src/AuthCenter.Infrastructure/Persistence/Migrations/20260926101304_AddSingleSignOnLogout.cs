using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthCenter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSingleSignOnLogout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing clients get the safe value; the model itself has no database default so an
            // explicit false is never replaced by it.
            migrationBuilder.AddColumn<bool>(
                name: "BackchannelLogoutSessionRequired",
                table: "OAuthClients",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "BackchannelLogoutUri",
                table: "OAuthClients",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PostLogoutRedirectUrisJson",
                table: "OAuthClients",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.CreateTable(
                name: "SingleSignOnSessionClients",
                columns: table => new
                {
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OAuthClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastIssuedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SingleSignOnSessionClients", x => new { x.SessionId, x.OAuthClientId });
                    table.ForeignKey(
                        name: "FK_SingleSignOnSessionClients_OAuthClients_OAuthClientId",
                        column: x => x.OAuthClientId,
                        principalTable: "OAuthClients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SingleSignOnSessionClients_OAuthClientId",
                table: "SingleSignOnSessionClients",
                column: "OAuthClientId");

            migrationBuilder.CreateIndex(
                name: "IX_SingleSignOnSessionClients_UserId",
                table: "SingleSignOnSessionClients",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SingleSignOnSessionClients");

            migrationBuilder.DropColumn(
                name: "BackchannelLogoutSessionRequired",
                table: "OAuthClients");

            migrationBuilder.DropColumn(
                name: "BackchannelLogoutUri",
                table: "OAuthClients");

            migrationBuilder.DropColumn(
                name: "PostLogoutRedirectUrisJson",
                table: "OAuthClients");
        }
    }
}
