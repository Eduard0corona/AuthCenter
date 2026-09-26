using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthCenter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEventHookSecretRotation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PreviousProtectedSecret",
                table: "EventHooks",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PreviousSecretExpiresAt",
                table: "EventHooks",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "EventHookDeliveries",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "SYSUTCDATETIME()");

            // Deliveries queued before this migration: the best known time of their event. EXEC defers
            // compiling the statement, because an idempotent script runs the whole migration as one batch.
            migrationBuilder.Sql("EXEC(N'UPDATE [EventHookDeliveries] SET [CreatedAt] = COALESCE([DeliveredAt], [DeadLetteredAt], [NextAttemptAt]);');");

            migrationBuilder.CreateIndex(
                name: "IX_EventHookDeliveries_CreatedAt",
                table: "EventHookDeliveries",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EventHookDeliveries_CreatedAt",
                table: "EventHookDeliveries");

            migrationBuilder.DropColumn(
                name: "PreviousProtectedSecret",
                table: "EventHooks");

            migrationBuilder.DropColumn(
                name: "PreviousSecretExpiresAt",
                table: "EventHooks");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "EventHookDeliveries");
        }
    }
}
