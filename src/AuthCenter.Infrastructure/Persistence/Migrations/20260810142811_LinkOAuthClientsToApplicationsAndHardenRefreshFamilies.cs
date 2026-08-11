using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthCenter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LinkOAuthClientsToApplicationsAndHardenRefreshFamilies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AbsoluteExpiresAt",
                table: "RefreshTokens",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TokenFamilyId",
                table: "RefreshTokens",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ApplicationSystemId",
                table: "OAuthClients",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE tokens
                SET AbsoluteExpiresAt = tokens.ExpiresAt,
                    TokenFamilyId = NEWID()
                FROM dbo.RefreshTokens AS tokens
                WHERE tokens.OAuthClientId IS NOT NULL;

                UPDATE clients
                SET ApplicationSystemId = matched.Id
                FROM dbo.OAuthClients AS clients
                CROSS APPLY
                (
                    SELECT TOP (1) applications.Id
                    FROM dbo.ApplicationSystems AS applications
                    WHERE LOWER(clients.ClientId) = LOWER(applications.Code)
                       OR LOWER(clients.ClientId) LIKE LOWER(applications.Code) + '-%'
                       OR LOWER(clients.ClientId) LIKE LOWER(applications.Code) + '[_]%'
                    ORDER BY LEN(applications.Code) DESC
                ) AS matched;

                IF EXISTS (SELECT 1 FROM dbo.OAuthClients WHERE ApplicationSystemId IS NULL)
                    THROW 51000, 'Every existing OAuth client must be named with its application-code prefix or linked manually before this migration can continue.', 1;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "ApplicationSystemId",
                table: "OAuthClients",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_OAuthClientId_TokenFamilyId_RevokedAt",
                table: "RefreshTokens",
                columns: new[] { "OAuthClientId", "TokenFamilyId", "RevokedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OAuthClients_ApplicationSystemId",
                table: "OAuthClients",
                column: "ApplicationSystemId");

            migrationBuilder.AddForeignKey(
                name: "FK_OAuthClients_ApplicationSystems_ApplicationSystemId",
                table: "OAuthClients",
                column: "ApplicationSystemId",
                principalTable: "ApplicationSystems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OAuthClients_ApplicationSystems_ApplicationSystemId",
                table: "OAuthClients");

            migrationBuilder.DropIndex(
                name: "IX_RefreshTokens_OAuthClientId_TokenFamilyId_RevokedAt",
                table: "RefreshTokens");

            migrationBuilder.DropIndex(
                name: "IX_OAuthClients_ApplicationSystemId",
                table: "OAuthClients");

            migrationBuilder.DropColumn(
                name: "AbsoluteExpiresAt",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "TokenFamilyId",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "ApplicationSystemId",
                table: "OAuthClients");
        }
    }
}
