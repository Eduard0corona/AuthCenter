using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthCenter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSingleSignOnSessionContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AssuranceLevel",
                table: "RefreshTokens",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AuthenticatedAt",
                table: "RefreshTokens",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AuthenticationMethods",
                table: "RefreshTokens",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SessionId",
                table: "RefreshTokens",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AssuranceLevel",
                table: "OAuthAuthorizationCodes",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AuthenticatedAt",
                table: "OAuthAuthorizationCodes",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AuthenticationMethods",
                table: "OAuthAuthorizationCodes",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SessionId",
                table: "OAuthAuthorizationCodes",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_SessionId_RevokedAt",
                table: "RefreshTokens",
                columns: new[] { "SessionId", "RevokedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RefreshTokens_SessionId_RevokedAt",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "AssuranceLevel",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "AuthenticatedAt",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "AuthenticationMethods",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "AssuranceLevel",
                table: "OAuthAuthorizationCodes");

            migrationBuilder.DropColumn(
                name: "AuthenticatedAt",
                table: "OAuthAuthorizationCodes");

            migrationBuilder.DropColumn(
                name: "AuthenticationMethods",
                table: "OAuthAuthorizationCodes");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "OAuthAuthorizationCodes");
        }
    }
}
