using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthCenter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompleteAdminBackendContracts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "ProfileMappings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "FederationRoutingRules",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "FederationProviders",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "EventHooks",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "LastReplayIdempotencyKey",
                table: "EventHookDeliveries",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "DynamicGroupRules",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Version",
                table: "ProfileMappings");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "FederationRoutingRules");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "FederationProviders");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "EventHooks");

            migrationBuilder.DropColumn(
                name: "LastReplayIdempotencyKey",
                table: "EventHookDeliveries");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "DynamicGroupRules");
        }
    }
}
