using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthCenter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompleteEnterpriseFederationPhase3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FederationProviders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationSystemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Protocol = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Issuer = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    DiscoveryEndpoint = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ClientId = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    OidcCallbackUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ProtectedClientSecret = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    SamlSingleSignOnUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SamlSigningCertificatePem = table.Column<string>(type: "nvarchar(max)", maxLength: 10000, nullable: true),
                    JitProvisioningEnabled = table.Column<bool>(type: "bit", nullable: false),
                    AccountLinkingMode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FederationProviders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FederationProviders_ApplicationSystems_ApplicationSystemId",
                        column: x => x.ApplicationSystemId,
                        principalTable: "ApplicationSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FederationRoutingRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FederationProviderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    EmailDomain = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    DirectoryGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProfileAttributeDefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExpectedProfileValueJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FederationRoutingRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FederationRoutingRules_DirectoryGroups_DirectoryGroupId",
                        column: x => x.DirectoryGroupId,
                        principalTable: "DirectoryGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FederationRoutingRules_FederationProviders_FederationProviderId",
                        column: x => x.FederationProviderId,
                        principalTable: "FederationProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FederationRoutingRules_UserProfileAttributeDefinitions_ProfileAttributeDefinitionId",
                        column: x => x.ProfileAttributeDefinitionId,
                        principalTable: "UserProfileAttributeDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FederationProviders_ApplicationSystemId_Name",
                table: "FederationProviders",
                columns: new[] { "ApplicationSystemId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FederationRoutingRules_DirectoryGroupId",
                table: "FederationRoutingRules",
                column: "DirectoryGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_FederationRoutingRules_FederationProviderId_Priority",
                table: "FederationRoutingRules",
                columns: new[] { "FederationProviderId", "Priority" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FederationRoutingRules_ProfileAttributeDefinitionId",
                table: "FederationRoutingRules",
                column: "ProfileAttributeDefinitionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FederationRoutingRules");

            migrationBuilder.DropTable(
                name: "FederationProviders");
        }
    }
}
