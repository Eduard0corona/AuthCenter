using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthCenter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFederationHostedLogin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GroupsClaim",
                table: "FederationProviders",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            // Existing providers keep requiring email_verified, as before this column existed.
            migrationBuilder.AddColumn<bool>(
                name: "RequireVerifiedEmail",
                table: "FederationProviders",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "TrustUpstreamMfa",
                table: "FederationProviders",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "FederationGroupMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FederationProviderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpstreamValue = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DirectoryGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FederationGroupMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FederationGroupMappings_DirectoryGroups_DirectoryGroupId",
                        column: x => x.DirectoryGroupId,
                        principalTable: "DirectoryGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FederationGroupMappings_FederationProviders_FederationProviderId",
                        column: x => x.FederationProviderId,
                        principalTable: "FederationProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FederationGroupMappings_DirectoryGroupId",
                table: "FederationGroupMappings",
                column: "DirectoryGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_FederationGroupMappings_FederationProviderId_UpstreamValue_DirectoryGroupId",
                table: "FederationGroupMappings",
                columns: new[] { "FederationProviderId", "UpstreamValue", "DirectoryGroupId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FederationGroupMappings");

            migrationBuilder.DropColumn(
                name: "GroupsClaim",
                table: "FederationProviders");

            migrationBuilder.DropColumn(
                name: "RequireVerifiedEmail",
                table: "FederationProviders");

            migrationBuilder.DropColumn(
                name: "TrustUpstreamMfa",
                table: "FederationProviders");
        }
    }
}
