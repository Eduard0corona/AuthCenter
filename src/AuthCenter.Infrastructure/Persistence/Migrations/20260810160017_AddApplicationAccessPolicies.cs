using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthCenter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddApplicationAccessPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApplicationAccessPolicyRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationSystemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DirectoryGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    MfaRequirement = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AllowTrustedDeviceBypass = table.Column<bool>(type: "bit", nullable: false),
                    IncludedIpCidrsJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ExcludedIpCidrsJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationAccessPolicyRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApplicationAccessPolicyRules_ApplicationSystems_ApplicationSystemId",
                        column: x => x.ApplicationSystemId,
                        principalTable: "ApplicationSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ApplicationAccessPolicyRules_DirectoryGroups_DirectoryGroupId",
                        column: x => x.DirectoryGroupId,
                        principalTable: "DirectoryGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationAccessPolicyRules_ApplicationSystemId_IsActive",
                table: "ApplicationAccessPolicyRules",
                columns: new[] { "ApplicationSystemId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationAccessPolicyRules_ApplicationSystemId_Priority",
                table: "ApplicationAccessPolicyRules",
                columns: new[] { "ApplicationSystemId", "Priority" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationAccessPolicyRules_DirectoryGroupId",
                table: "ApplicationAccessPolicyRules",
                column: "DirectoryGroupId");

            migrationBuilder.Sql(
                """
                DECLARE @AuthCenterApplicationId uniqueidentifier =
                    (SELECT TOP (1) Id FROM dbo.ApplicationSystems WHERE Code = 'AUTHCENTER');

                IF @AuthCenterApplicationId IS NOT NULL
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE Code = 'AUTHCENTER_ACCESS_POLICIES_READ')
                        INSERT INTO dbo.Permissions (Id, ApplicationSystemId, Code, Name, Description, IsActive, CreatedAt)
                        VALUES (NEWID(), @AuthCenterApplicationId, 'AUTHCENTER_ACCESS_POLICIES_READ', 'Read access policies', NULL, 1, SYSUTCDATETIME());

                    IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE Code = 'AUTHCENTER_ACCESS_POLICIES_WRITE')
                        INSERT INTO dbo.Permissions (Id, ApplicationSystemId, Code, Name, Description, IsActive, CreatedAt)
                        VALUES (NEWID(), @AuthCenterApplicationId, 'AUTHCENTER_ACCESS_POLICIES_WRITE', 'Write access policies', NULL, 1, SYSUTCDATETIME());

                    INSERT INTO dbo.RolePermissions (RoleId, PermissionId, CreatedAt)
                    SELECT roles.Id, permissions.Id, SYSUTCDATETIME()
                    FROM dbo.AspNetRoles AS roles
                    CROSS JOIN dbo.Permissions AS permissions
                    WHERE roles.ApplicationSystemId = @AuthCenterApplicationId
                      AND roles.DisplayName IN ('SuperAdmin', 'Admin')
                      AND permissions.ApplicationSystemId = @AuthCenterApplicationId
                      AND permissions.Code IN ('AUTHCENTER_ACCESS_POLICIES_READ', 'AUTHCENTER_ACCESS_POLICIES_WRITE')
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
                WHERE permissions.Code IN ('AUTHCENTER_ACCESS_POLICIES_READ', 'AUTHCENTER_ACCESS_POLICIES_WRITE');

                DELETE FROM dbo.Permissions
                WHERE Code IN ('AUTHCENTER_ACCESS_POLICIES_READ', 'AUTHCENTER_ACCESS_POLICIES_WRITE');
                """);

            migrationBuilder.DropTable(
                name: "ApplicationAccessPolicyRules");
        }
    }
}
