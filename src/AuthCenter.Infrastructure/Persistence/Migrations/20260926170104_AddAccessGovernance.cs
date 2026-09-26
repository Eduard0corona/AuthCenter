using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthCenter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAccessGovernance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccessRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationSystemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedRoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Source = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Justification = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DecidedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecisionComment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessRequests_ApplicationSystems_ApplicationSystemId",
                        column: x => x.ApplicationSystemId,
                        principalTable: "ApplicationSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccessRequests_AspNetRoles_RequestedRoleId",
                        column: x => x.RequestedRoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AccessRequests_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AccessReviewCampaigns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    ApplicationSystemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DueAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokeUnreviewed = table.Column<bool>(type: "bit", nullable: false),
                    RecurrenceMonths = table.Column<int>(type: "int", nullable: true),
                    PreviousCampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LockedUntil = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NextStarted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessReviewCampaigns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessReviewCampaigns_ApplicationSystems_ApplicationSystemId",
                        column: x => x.ApplicationSystemId,
                        principalTable: "ApplicationSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ApplicationGovernance",
                columns: table => new
                {
                    ApplicationSystemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    AccessRequestsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationGovernance", x => x.ApplicationSystemId);
                    table.ForeignKey(
                        name: "FK_ApplicationGovernance_ApplicationSystems_ApplicationSystemId",
                        column: x => x.ApplicationSystemId,
                        principalTable: "ApplicationSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ApplicationOwners",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationSystemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationOwners", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApplicationOwners_ApplicationSystems_ApplicationSystemId",
                        column: x => x.ApplicationSystemId,
                        principalTable: "ApplicationSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ApplicationOwners_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SeparationOfDutiesRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    FirstRoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SecondRoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeparationOfDutiesRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SeparationOfDutiesRules_AspNetRoles_FirstRoleId",
                        column: x => x.FirstRoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SeparationOfDutiesRules_AspNetRoles_SecondRoleId",
                        column: x => x.SecondRoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "AccessReviewItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HasDirectAccess = table.Column<bool>(type: "bit", nullable: false),
                    GroupNames = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RoleNames = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Decision = table.Column<int>(type: "int", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DecidedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecidedAutomatically = table.Column<bool>(type: "bit", nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RemediationRequired = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessReviewItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessReviewItems_AccessReviewCampaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "AccessReviewCampaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccessReviewItems_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessRequests_ApplicationSystemId_Status",
                table: "AccessRequests",
                columns: new[] { "ApplicationSystemId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AccessRequests_RequestedRoleId",
                table: "AccessRequests",
                column: "RequestedRoleId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessRequests_Status_ExpiresAt",
                table: "AccessRequests",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AccessRequests_UserId_Status",
                table: "AccessRequests",
                columns: new[] { "UserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AccessReviewCampaigns_ApplicationSystemId",
                table: "AccessReviewCampaigns",
                column: "ApplicationSystemId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessReviewCampaigns_Status_DueAt",
                table: "AccessReviewCampaigns",
                columns: new[] { "Status", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AccessReviewItems_CampaignId_Decision",
                table: "AccessReviewItems",
                columns: new[] { "CampaignId", "Decision" });

            migrationBuilder.CreateIndex(
                name: "IX_AccessReviewItems_CampaignId_UserId",
                table: "AccessReviewItems",
                columns: new[] { "CampaignId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessReviewItems_UserId",
                table: "AccessReviewItems",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationOwners_ApplicationSystemId_UserId",
                table: "ApplicationOwners",
                columns: new[] { "ApplicationSystemId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationOwners_UserId",
                table: "ApplicationOwners",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_SeparationOfDutiesRules_FirstRoleId",
                table: "SeparationOfDutiesRules",
                column: "FirstRoleId");

            migrationBuilder.CreateIndex(
                name: "IX_SeparationOfDutiesRules_Name",
                table: "SeparationOfDutiesRules",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SeparationOfDutiesRules_SecondRoleId",
                table: "SeparationOfDutiesRules",
                column: "SecondRoleId");

            // Every role that manages users manages access governance too: approving pending access
            // used to be a users permission.
            migrationBuilder.Sql("""
                DECLARE @applicationId uniqueidentifier;
                SELECT TOP (1) @applicationId = Id FROM ApplicationSystems WHERE Code = 'AUTHCENTER';

                IF @applicationId IS NOT NULL
                BEGIN
                    DECLARE @map TABLE (Code nvarchar(100) NOT NULL, Name nvarchar(200) NOT NULL, LegacyCode nvarchar(100) NOT NULL);
                    INSERT INTO @map (Code, Name, LegacyCode) VALUES
                        ('AUTHCENTER_GOVERNANCE_READ', 'Read application owners, access requests, access reviews and separation of duties', 'AUTHCENTER_USERS_READ'),
                        ('AUTHCENTER_GOVERNANCE_WRITE', 'Manage application owners, decide access requests, run access reviews and separation of duties rules', 'AUTHCENTER_USERS_WRITE');

                    INSERT INTO Permissions (Id, ApplicationSystemId, Code, Name, Description, IsActive, CreatedAt)
                    SELECT NEWID(), @applicationId, m.Code, m.Name, NULL, 1, SYSUTCDATETIME()
                    FROM @map m
                    WHERE NOT EXISTS (SELECT 1 FROM Permissions p WHERE p.ApplicationSystemId = @applicationId AND p.Code = m.Code);

                    INSERT INTO RolePermissions (RoleId, PermissionId, CreatedAt)
                    SELECT DISTINCT legacyGrant.RoleId, granted.Id, SYSUTCDATETIME()
                    FROM @map m
                    JOIN Permissions legacy ON legacy.ApplicationSystemId = @applicationId AND legacy.Code = m.LegacyCode
                    JOIN RolePermissions legacyGrant ON legacyGrant.PermissionId = legacy.Id
                    JOIN Permissions granted ON granted.ApplicationSystemId = @applicationId AND granted.Code = m.Code
                    WHERE NOT EXISTS (SELECT 1 FROM RolePermissions existing WHERE existing.RoleId = legacyGrant.RoleId AND existing.PermissionId = granted.Id);
                END;
                """);

            // SCIM used to leave a deprovisioned user's access looking pending approval; it is revoked.
            migrationBuilder.Sql("""
                UPDATE access SET RevokedAt = SYSUTCDATETIME()
                FROM UserApplicationAccesses access
                JOIN AspNetUsers users ON users.Id = access.UserId
                WHERE access.IsActive = 0 AND access.RevokedAt IS NULL AND users.IsActive = 0
                  AND EXISTS (SELECT 1 FROM ScimResourceLinks link
                              WHERE link.ResourceType = 'User' AND link.ResourceId = access.UserId AND link.ApplicationSystemId = access.ApplicationSystemId);
                """);

            // Access already waiting for approval gets the request its owners will decide.
            migrationBuilder.Sql("""
                INSERT INTO AccessRequests (Id, UserId, ApplicationSystemId, RequestedRoleId, Source, Status, Justification, CreatedAt, ExpiresAt, DecidedAt, DecidedByUserId, DecisionComment, Version)
                SELECT NEWID(), access.UserId, access.ApplicationSystemId, NULL, 1, 0, NULL, access.CreatedAt, NULL, NULL, NULL, NULL, 0
                FROM UserApplicationAccesses access
                JOIN AspNetUsers users ON users.Id = access.UserId
                WHERE access.IsActive = 0 AND access.RevokedAt IS NULL AND users.DeletedAt IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE rp FROM RolePermissions rp
                JOIN Permissions p ON p.Id = rp.PermissionId
                JOIN ApplicationSystems a ON a.Id = p.ApplicationSystemId AND a.Code = 'AUTHCENTER'
                WHERE p.Code IN ('AUTHCENTER_GOVERNANCE_READ', 'AUTHCENTER_GOVERNANCE_WRITE');
                DELETE p FROM Permissions p
                JOIN ApplicationSystems a ON a.Id = p.ApplicationSystemId AND a.Code = 'AUTHCENTER'
                WHERE p.Code IN ('AUTHCENTER_GOVERNANCE_READ', 'AUTHCENTER_GOVERNANCE_WRITE');
                """);

            migrationBuilder.DropTable(
                name: "AccessRequests");

            migrationBuilder.DropTable(
                name: "AccessReviewItems");

            migrationBuilder.DropTable(
                name: "ApplicationGovernance");

            migrationBuilder.DropTable(
                name: "ApplicationOwners");

            migrationBuilder.DropTable(
                name: "SeparationOfDutiesRules");

            migrationBuilder.DropTable(
                name: "AccessReviewCampaigns");
        }
    }
}
