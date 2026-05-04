using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthCenter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAppleMagicLinksEmailOtp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Method",
                table: "UserMfaCredentials",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "AllowAppleLogin",
                table: "ApplicationRegistrationSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AllowMagicLink",
                table: "ApplicationRegistrationSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Method",
                table: "UserMfaCredentials");

            migrationBuilder.DropColumn(
                name: "AllowAppleLogin",
                table: "ApplicationRegistrationSettings");

            migrationBuilder.DropColumn(
                name: "AllowMagicLink",
                table: "ApplicationRegistrationSettings");
        }
    }
}
