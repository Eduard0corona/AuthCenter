using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthCenter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdministrativeVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "UserProfileAttributeDefinitions",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "Permissions",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "OAuthClients",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "DirectoryGroups",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "AspNetUsers",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "AspNetRoles",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "ApplicationSystems",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "ApplicationAccessPolicyRules",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "ApiResources",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Version",
                table: "UserProfileAttributeDefinitions");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Permissions");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "OAuthClients");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "DirectoryGroups");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "AspNetRoles");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "ApplicationSystems");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "ApplicationAccessPolicyRules");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "ApiResources");
        }
    }
}
