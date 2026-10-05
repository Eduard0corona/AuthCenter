using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthCenter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddApplicationAudience : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Audience",
                table: "ApplicationRegistrationSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // Applications with open registration serve consumers (ApplicationAudience.Consumers = 1,
            // ApplicationRegistrationMode.Open = 1); every other one keeps Employees (0).
            migrationBuilder.Sql("UPDATE [ApplicationRegistrationSettings] SET [Audience] = 1 WHERE [RegistrationMode] = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Audience",
                table: "ApplicationRegistrationSettings");
        }
    }
}
