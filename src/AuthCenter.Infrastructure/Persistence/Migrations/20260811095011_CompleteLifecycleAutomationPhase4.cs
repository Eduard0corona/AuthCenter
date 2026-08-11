using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthCenter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompleteLifecycleAutomationPhase4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DynamicGroupRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DirectoryGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProfileAttributeDefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operator = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ExpectedValueJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DynamicGroupRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DynamicGroupRules_DirectoryGroups_DirectoryGroupId",
                        column: x => x.DirectoryGroupId,
                        principalTable: "DirectoryGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DynamicGroupRules_UserProfileAttributeDefinitions_ProfileAttributeDefinitionId",
                        column: x => x.ProfileAttributeDefinitionId,
                        principalTable: "UserProfileAttributeDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EventHooks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationSystemId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Url = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ProtectedSecret = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    EventTypesJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    IsVerified = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    VerifiedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventHooks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventHooks_ApplicationSystems_ApplicationSystemId",
                        column: x => x.ApplicationSystemId,
                        principalTable: "ApplicationSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProfileMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationSystemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceSystem = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SourcePath = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    TargetAttributeDefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsAuthoritative = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfileMappings_ApplicationSystems_ApplicationSystemId",
                        column: x => x.ApplicationSystemId,
                        principalTable: "ApplicationSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProfileMappings_UserProfileAttributeDefinitions_TargetAttributeDefinitionId",
                        column: x => x.TargetAttributeDefinitionId,
                        principalTable: "UserProfileAttributeDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProvisioningTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationSystemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ScopesJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProvisioningTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProvisioningTokens_ApplicationSystems_ApplicationSystemId",
                        column: x => x.ApplicationSystemId,
                        principalTable: "ApplicationSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScimResourceLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationSystemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourceType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScimResourceLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScimResourceLinks_ApplicationSystems_ApplicationSystemId",
                        column: x => x.ApplicationSystemId,
                        principalTable: "ApplicationSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EventHookDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventHookId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", maxLength: 20000, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LockedUntil = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeliveredAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeadLetteredAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventHookDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventHookDeliveries_EventHooks_EventHookId",
                        column: x => x.EventHookId,
                        principalTable: "EventHooks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DynamicGroupRules_DirectoryGroupId_ProfileAttributeDefinitionId",
                table: "DynamicGroupRules",
                columns: new[] { "DirectoryGroupId", "ProfileAttributeDefinitionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DynamicGroupRules_ProfileAttributeDefinitionId",
                table: "DynamicGroupRules",
                column: "ProfileAttributeDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_EventHookDeliveries_DeliveredAt_DeadLetteredAt_NextAttemptAt",
                table: "EventHookDeliveries",
                columns: new[] { "DeliveredAt", "DeadLetteredAt", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EventHookDeliveries_EventHookId_EventId",
                table: "EventHookDeliveries",
                columns: new[] { "EventHookId", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventHooks_ApplicationSystemId_Name",
                table: "EventHooks",
                columns: new[] { "ApplicationSystemId", "Name" },
                unique: true,
                filter: "[ApplicationSystemId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProfileMappings_ApplicationSystemId_SourceSystem_SourcePath",
                table: "ProfileMappings",
                columns: new[] { "ApplicationSystemId", "SourceSystem", "SourcePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProfileMappings_TargetAttributeDefinitionId",
                table: "ProfileMappings",
                column: "TargetAttributeDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_ProvisioningTokens_ApplicationSystemId_ExpiresAt",
                table: "ProvisioningTokens",
                columns: new[] { "ApplicationSystemId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProvisioningTokens_TokenHash",
                table: "ProvisioningTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScimResourceLinks_ApplicationSystemId_ResourceType_ExternalId",
                table: "ScimResourceLinks",
                columns: new[] { "ApplicationSystemId", "ResourceType", "ExternalId" },
                unique: true,
                filter: "[ExternalId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ScimResourceLinks_ApplicationSystemId_ResourceType_ResourceId",
                table: "ScimResourceLinks",
                columns: new[] { "ApplicationSystemId", "ResourceType", "ResourceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DynamicGroupRules");

            migrationBuilder.DropTable(
                name: "EventHookDeliveries");

            migrationBuilder.DropTable(
                name: "ProfileMappings");

            migrationBuilder.DropTable(
                name: "ProvisioningTokens");

            migrationBuilder.DropTable(
                name: "ScimResourceLinks");

            migrationBuilder.DropTable(
                name: "EventHooks");
        }
    }
}
