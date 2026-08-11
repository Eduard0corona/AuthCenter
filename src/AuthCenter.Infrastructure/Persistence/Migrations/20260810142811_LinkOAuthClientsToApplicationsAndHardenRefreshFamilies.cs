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
                -- Generated idempotent scripts place ADD COLUMN and data backfill in one SQL
                -- batch. Dynamic SQL defers name resolution until the new columns exist; direct
                -- statements work through IMigrator but fail when the deployment script is run as
                -- a batch against Azure SQL.
                EXEC(N'
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
                           OR LOWER(clients.ClientId) LIKE LOWER(applications.Code) + ''-%''
                           OR LOWER(clients.ClientId) LIKE LOWER(applications.Code) + ''[_]%''
                        ORDER BY LEN(applications.Code) DESC
                    ) AS matched;

                    IF EXISTS (SELECT 1 FROM dbo.OAuthClients WHERE ApplicationSystemId IS NULL)
                        THROW 51000, ''Every existing OAuth client must be named with its application-code prefix or linked manually before this migration can continue.'', 1;
                ');
                """);

            migrationBuilder.Sql(
                """
                EXEC(N'ALTER TABLE dbo.OAuthClients ALTER COLUMN ApplicationSystemId uniqueidentifier NOT NULL;');
                EXEC(N'CREATE INDEX IX_RefreshTokens_OAuthClientId_TokenFamilyId_RevokedAt
                    ON dbo.RefreshTokens (OAuthClientId, TokenFamilyId, RevokedAt);');
                EXEC(N'CREATE INDEX IX_OAuthClients_ApplicationSystemId
                    ON dbo.OAuthClients (ApplicationSystemId);');
                EXEC(N'ALTER TABLE dbo.OAuthClients
                    ADD CONSTRAINT FK_OAuthClients_ApplicationSystems_ApplicationSystemId
                    FOREIGN KEY (ApplicationSystemId) REFERENCES dbo.ApplicationSystems (Id)
                    ON DELETE NO ACTION;');
                """);
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
