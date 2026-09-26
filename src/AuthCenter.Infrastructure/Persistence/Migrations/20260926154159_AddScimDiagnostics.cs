using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthCenter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddScimDiagnostics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScimRequestLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProvisioningTokenId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Method = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Path = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    StatusCode = table.Column<int>(type: "int", nullable: false),
                    ScimType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    TraceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScimRequestLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScimRequestLogs_ProvisioningTokens_ProvisioningTokenId",
                        column: x => x.ProvisioningTokenId,
                        principalTable: "ProvisioningTokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScimRequestLogs_CreatedAt",
                table: "ScimRequestLogs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ScimRequestLogs_ProvisioningTokenId_CreatedAt",
                table: "ScimRequestLogs",
                columns: new[] { "ProvisioningTokenId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScimRequestLogs");
        }
    }
}
