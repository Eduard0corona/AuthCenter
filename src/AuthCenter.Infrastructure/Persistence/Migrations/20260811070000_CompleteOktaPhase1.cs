using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthCenter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompleteOktaPhase1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ApplicationAccessPolicyRules_ApplicationSystems_ApplicationSystemId",
                table: "ApplicationAccessPolicyRules");

            migrationBuilder.DropIndex(
                name: "IX_ApplicationAccessPolicyRules_ApplicationSystemId_IsActive",
                table: "ApplicationAccessPolicyRules");

            migrationBuilder.DropIndex(
                name: "IX_ApplicationAccessPolicyRules_ApplicationSystemId_Priority",
                table: "ApplicationAccessPolicyRules");

            migrationBuilder.AddColumn<string>(
                name: "ActiveDaysUtcJson",
                table: "ApplicationAccessPolicyRules",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ActiveFromUtc",
                table: "ApplicationAccessPolicyRules",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ActiveUntilUtc",
                table: "ApplicationAccessPolicyRules",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "DailyEndTimeUtc",
                table: "ApplicationAccessPolicyRules",
                type: "time",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "DailyStartTimeUtc",
                table: "ApplicationAccessPolicyRules",
                type: "time",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MaximumRiskLevel",
                table: "ApplicationAccessPolicyRules",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MinimumRiskLevel",
                table: "ApplicationAccessPolicyRules",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PolicyVersionId",
                table: "ApplicationAccessPolicyRules",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequiredAssuranceLevel",
                table: "ApplicationAccessPolicyRules",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Password");

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "ApplicationAccessPolicyRules",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ApplicationAccessPolicyVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationSystemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PublishedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationAccessPolicyVersions", x => x.Id);
                    table.UniqueConstraint("AK_ApplicationAccessPolicyVersions_Id_ApplicationSystemId", x => new { x.Id, x.ApplicationSystemId });
                    table.ForeignKey(
                        name: "FK_ApplicationAccessPolicyVersions_ApplicationSystems_ApplicationSystemId",
                        column: x => x.ApplicationSystemId,
                        principalTable: "ApplicationSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ApplicationAccessPolicyVersions_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApplicationAccessPolicyVersions_AspNetUsers_PublishedByUserId",
                        column: x => x.PublishedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserProfileAttributeDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DataType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsRequired = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    DefaultValueJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    MinLength = table.Column<int>(type: "int", nullable: true),
                    MaxLength = table.Column<int>(type: "int", nullable: true),
                    MinimumNumber = table.Column<decimal>(type: "decimal(38,10)", precision: 38, scale: 10, nullable: true),
                    MaximumNumber = table.Column<decimal>(type: "decimal(38,10)", precision: 38, scale: 10, nullable: true),
                    ValidationPattern = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AllowedValuesJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserProfileAttributeDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserProfileAttributeValues",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttributeDefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ValueJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserProfileAttributeValues", x => new { x.UserId, x.AttributeDefinitionId });
                    table.ForeignKey(
                        name: "FK_UserProfileAttributeValues_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserProfileAttributeValues_UserProfileAttributeDefinitions_AttributeDefinitionId",
                        column: x => x.AttributeDefinitionId,
                        principalTable: "UserProfileAttributeDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Preserve the behavior of policies created before versioning. Each application with
            // existing rules receives one immutable published version and every rule is linked to
            // it before PolicyVersionId becomes required.
            migrationBuilder.Sql(
                """
                INSERT INTO [ApplicationAccessPolicyVersions]
                    ([Id], [ApplicationSystemId], [VersionNumber], [Status], [CreatedByUserId],
                     [PublishedByUserId], [CreatedAt], [PublishedAt])
                SELECT NEWID(), [ApplicationSystemId], 1, N'Published', NULL, NULL,
                       MIN([CreatedAt]), SYSUTCDATETIME()
                FROM [ApplicationAccessPolicyRules]
                GROUP BY [ApplicationSystemId];

                UPDATE rules
                SET [PolicyVersionId] = versions.[Id]
                FROM [ApplicationAccessPolicyRules] AS rules
                INNER JOIN [ApplicationAccessPolicyVersions] AS versions
                    ON versions.[ApplicationSystemId] = rules.[ApplicationSystemId]
                   AND versions.[VersionNumber] = 1;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "PolicyVersionId",
                table: "ApplicationAccessPolicyRules",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationAccessPolicyRules_ApplicationSystemId",
                table: "ApplicationAccessPolicyRules",
                column: "ApplicationSystemId");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationAccessPolicyRules_PolicyVersionId_ApplicationSystemId",
                table: "ApplicationAccessPolicyRules",
                columns: new[] { "PolicyVersionId", "ApplicationSystemId" });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationAccessPolicyRules_PolicyVersionId_IsActive",
                table: "ApplicationAccessPolicyRules",
                columns: new[] { "PolicyVersionId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationAccessPolicyRules_PolicyVersionId_Priority",
                table: "ApplicationAccessPolicyRules",
                columns: new[] { "PolicyVersionId", "Priority" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationAccessPolicyRules_UserId",
                table: "ApplicationAccessPolicyRules",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationAccessPolicyVersions_ApplicationSystemId",
                table: "ApplicationAccessPolicyVersions",
                column: "ApplicationSystemId",
                unique: true,
                filter: "[Status] = 'Draft'");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationAccessPolicyVersions_ApplicationSystemId_Status",
                table: "ApplicationAccessPolicyVersions",
                columns: new[] { "ApplicationSystemId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationAccessPolicyVersions_ApplicationSystemId_VersionNumber",
                table: "ApplicationAccessPolicyVersions",
                columns: new[] { "ApplicationSystemId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationAccessPolicyVersions_CreatedByUserId",
                table: "ApplicationAccessPolicyVersions",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationAccessPolicyVersions_PublishedByUserId",
                table: "ApplicationAccessPolicyVersions",
                column: "PublishedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserProfileAttributeDefinitions_IsActive",
                table: "UserProfileAttributeDefinitions",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_UserProfileAttributeDefinitions_Key",
                table: "UserProfileAttributeDefinitions",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserProfileAttributeValues_AttributeDefinitionId",
                table: "UserProfileAttributeValues",
                column: "AttributeDefinitionId");

            migrationBuilder.AddForeignKey(
                name: "FK_ApplicationAccessPolicyRules_ApplicationAccessPolicyVersions_PolicyVersionId_ApplicationSystemId",
                table: "ApplicationAccessPolicyRules",
                columns: new[] { "PolicyVersionId", "ApplicationSystemId" },
                principalTable: "ApplicationAccessPolicyVersions",
                principalColumns: new[] { "Id", "ApplicationSystemId" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ApplicationAccessPolicyRules_ApplicationSystems_ApplicationSystemId",
                table: "ApplicationAccessPolicyRules",
                column: "ApplicationSystemId",
                principalTable: "ApplicationSystems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ApplicationAccessPolicyRules_AspNetUsers_UserId",
                table: "ApplicationAccessPolicyRules",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(
                """
                DECLARE @AuthCenterApplicationId uniqueidentifier =
                    (SELECT TOP (1) Id FROM dbo.ApplicationSystems WHERE Code = 'AUTHCENTER');

                IF @AuthCenterApplicationId IS NOT NULL
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE Code = 'AUTHCENTER_PROFILE_SCHEMAS_READ')
                        INSERT INTO dbo.Permissions (Id, ApplicationSystemId, Code, Name, Description, IsActive, CreatedAt)
                        VALUES (NEWID(), @AuthCenterApplicationId, 'AUTHCENTER_PROFILE_SCHEMAS_READ', 'Read universal directory profile schemas', NULL, 1, SYSUTCDATETIME());

                    IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE Code = 'AUTHCENTER_PROFILE_SCHEMAS_WRITE')
                        INSERT INTO dbo.Permissions (Id, ApplicationSystemId, Code, Name, Description, IsActive, CreatedAt)
                        VALUES (NEWID(), @AuthCenterApplicationId, 'AUTHCENTER_PROFILE_SCHEMAS_WRITE', 'Write universal directory profile schemas', NULL, 1, SYSUTCDATETIME());

                    INSERT INTO dbo.RolePermissions (RoleId, PermissionId, CreatedAt)
                    SELECT roles.Id, permissions.Id, SYSUTCDATETIME()
                    FROM dbo.AspNetRoles AS roles
                    CROSS JOIN dbo.Permissions AS permissions
                    WHERE roles.ApplicationSystemId = @AuthCenterApplicationId
                      AND roles.DisplayName IN ('SuperAdmin', 'Admin')
                      AND permissions.ApplicationSystemId = @AuthCenterApplicationId
                      AND permissions.Code IN ('AUTHCENTER_PROFILE_SCHEMAS_READ', 'AUTHCENTER_PROFILE_SCHEMAS_WRITE')
                      AND NOT EXISTS
                      (
                          SELECT 1 FROM dbo.RolePermissions AS existing
                          WHERE existing.RoleId = roles.Id AND existing.PermissionId = permissions.Id
                      );
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE rolePermissions
                FROM dbo.RolePermissions AS rolePermissions
                INNER JOIN dbo.Permissions AS permissions ON permissions.Id = rolePermissions.PermissionId
                WHERE permissions.Code IN ('AUTHCENTER_PROFILE_SCHEMAS_READ', 'AUTHCENTER_PROFILE_SCHEMAS_WRITE');

                DELETE FROM dbo.Permissions
                WHERE Code IN ('AUTHCENTER_PROFILE_SCHEMAS_READ', 'AUTHCENTER_PROFILE_SCHEMAS_WRITE');
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_ApplicationAccessPolicyRules_ApplicationAccessPolicyVersions_PolicyVersionId_ApplicationSystemId",
                table: "ApplicationAccessPolicyRules");

            migrationBuilder.DropForeignKey(
                name: "FK_ApplicationAccessPolicyRules_ApplicationSystems_ApplicationSystemId",
                table: "ApplicationAccessPolicyRules");

            migrationBuilder.DropForeignKey(
                name: "FK_ApplicationAccessPolicyRules_AspNetUsers_UserId",
                table: "ApplicationAccessPolicyRules");

            migrationBuilder.DropTable(
                name: "ApplicationAccessPolicyVersions");

            migrationBuilder.DropTable(
                name: "UserProfileAttributeValues");

            migrationBuilder.DropTable(
                name: "UserProfileAttributeDefinitions");

            migrationBuilder.DropIndex(
                name: "IX_ApplicationAccessPolicyRules_ApplicationSystemId",
                table: "ApplicationAccessPolicyRules");

            migrationBuilder.DropIndex(
                name: "IX_ApplicationAccessPolicyRules_PolicyVersionId_ApplicationSystemId",
                table: "ApplicationAccessPolicyRules");

            migrationBuilder.DropIndex(
                name: "IX_ApplicationAccessPolicyRules_PolicyVersionId_IsActive",
                table: "ApplicationAccessPolicyRules");

            migrationBuilder.DropIndex(
                name: "IX_ApplicationAccessPolicyRules_PolicyVersionId_Priority",
                table: "ApplicationAccessPolicyRules");

            migrationBuilder.DropIndex(
                name: "IX_ApplicationAccessPolicyRules_UserId",
                table: "ApplicationAccessPolicyRules");

            migrationBuilder.DropColumn(
                name: "ActiveDaysUtcJson",
                table: "ApplicationAccessPolicyRules");

            migrationBuilder.DropColumn(
                name: "ActiveFromUtc",
                table: "ApplicationAccessPolicyRules");

            migrationBuilder.DropColumn(
                name: "ActiveUntilUtc",
                table: "ApplicationAccessPolicyRules");

            migrationBuilder.DropColumn(
                name: "DailyEndTimeUtc",
                table: "ApplicationAccessPolicyRules");

            migrationBuilder.DropColumn(
                name: "DailyStartTimeUtc",
                table: "ApplicationAccessPolicyRules");

            migrationBuilder.DropColumn(
                name: "MaximumRiskLevel",
                table: "ApplicationAccessPolicyRules");

            migrationBuilder.DropColumn(
                name: "MinimumRiskLevel",
                table: "ApplicationAccessPolicyRules");

            migrationBuilder.DropColumn(
                name: "PolicyVersionId",
                table: "ApplicationAccessPolicyRules");

            migrationBuilder.DropColumn(
                name: "RequiredAssuranceLevel",
                table: "ApplicationAccessPolicyRules");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "ApplicationAccessPolicyRules");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationAccessPolicyRules_ApplicationSystemId_IsActive",
                table: "ApplicationAccessPolicyRules",
                columns: new[] { "ApplicationSystemId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationAccessPolicyRules_ApplicationSystemId_Priority",
                table: "ApplicationAccessPolicyRules",
                columns: new[] { "ApplicationSystemId", "Priority" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ApplicationAccessPolicyRules_ApplicationSystems_ApplicationSystemId",
                table: "ApplicationAccessPolicyRules",
                column: "ApplicationSystemId",
                principalTable: "ApplicationSystems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
