using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthCenter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScopeRefreshTokensByApplication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue "AUTHCENTER" backfills all existing tokens to the AuthCenter system application.
            // This is intentional: all tokens issued before this migration were for AuthCenter itself.
            // If this service is ever redeployed for a different root application, this value must be updated.
            migrationBuilder.AddColumn<string>(
                name: "ApplicationCode",
                table: "RefreshTokens",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "AUTHCENTER");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApplicationCode",
                table: "RefreshTokens");
        }
    }
}
