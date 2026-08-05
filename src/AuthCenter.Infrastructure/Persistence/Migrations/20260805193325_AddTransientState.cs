using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthCenter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTransientState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TransientStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Key = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransientStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TransientStates_ExpiresAt",
                table: "TransientStates",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_TransientStates_Purpose_Key",
                table: "TransientStates",
                columns: new[] { "Purpose", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TransientStates");
        }
    }
}
