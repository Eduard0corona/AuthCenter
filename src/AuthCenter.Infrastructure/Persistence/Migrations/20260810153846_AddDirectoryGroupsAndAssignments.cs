using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthCenter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDirectoryGroupsAndAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DirectoryGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectoryGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GroupApplicationAssignments",
                columns: table => new
                {
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationSystemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupApplicationAssignments", x => new { x.GroupId, x.ApplicationSystemId });
                    table.ForeignKey(
                        name: "FK_GroupApplicationAssignments_ApplicationSystems_ApplicationSystemId",
                        column: x => x.ApplicationSystemId,
                        principalTable: "ApplicationSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GroupApplicationAssignments_DirectoryGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "DirectoryGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GroupRoleAssignments",
                columns: table => new
                {
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupRoleAssignments", x => new { x.GroupId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_GroupRoleAssignments_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GroupRoleAssignments_DirectoryGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "DirectoryGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserGroupMemberships",
                columns: table => new
                {
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserGroupMemberships", x => new { x.GroupId, x.UserId });
                    table.ForeignKey(
                        name: "FK_UserGroupMemberships_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserGroupMemberships_DirectoryGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "DirectoryGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryGroups_IsActive_Name",
                table: "DirectoryGroups",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryGroups_NormalizedName",
                table: "DirectoryGroups",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupApplicationAssignments_ApplicationSystemId",
                table: "GroupApplicationAssignments",
                column: "ApplicationSystemId");

            migrationBuilder.CreateIndex(
                name: "IX_GroupRoleAssignments_RoleId",
                table: "GroupRoleAssignments",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_UserGroupMemberships_UserId",
                table: "UserGroupMemberships",
                column: "UserId");

            migrationBuilder.Sql(
                """
                DECLARE @AuthCenterApplicationId uniqueidentifier =
                    (SELECT TOP (1) Id FROM dbo.ApplicationSystems WHERE Code = 'AUTHCENTER');

                IF @AuthCenterApplicationId IS NOT NULL
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE Code = 'AUTHCENTER_GROUPS_READ')
                        INSERT INTO dbo.Permissions (Id, ApplicationSystemId, Code, Name, Description, IsActive, CreatedAt)
                        VALUES (NEWID(), @AuthCenterApplicationId, 'AUTHCENTER_GROUPS_READ', 'Read directory groups', NULL, 1, SYSUTCDATETIME());

                    IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE Code = 'AUTHCENTER_GROUPS_WRITE')
                        INSERT INTO dbo.Permissions (Id, ApplicationSystemId, Code, Name, Description, IsActive, CreatedAt)
                        VALUES (NEWID(), @AuthCenterApplicationId, 'AUTHCENTER_GROUPS_WRITE', 'Write directory groups', NULL, 1, SYSUTCDATETIME());

                    INSERT INTO dbo.RolePermissions (RoleId, PermissionId, CreatedAt)
                    SELECT roles.Id, permissions.Id, SYSUTCDATETIME()
                    FROM dbo.AspNetRoles AS roles
                    CROSS JOIN dbo.Permissions AS permissions
                    WHERE roles.ApplicationSystemId = @AuthCenterApplicationId
                      AND roles.DisplayName IN ('SuperAdmin', 'Admin')
                      AND permissions.ApplicationSystemId = @AuthCenterApplicationId
                      AND permissions.Code IN ('AUTHCENTER_GROUPS_READ', 'AUTHCENTER_GROUPS_WRITE')
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
                WHERE permissions.Code IN ('AUTHCENTER_GROUPS_READ', 'AUTHCENTER_GROUPS_WRITE');

                DELETE FROM dbo.Permissions
                WHERE Code IN ('AUTHCENTER_GROUPS_READ', 'AUTHCENTER_GROUPS_WRITE');
                """);

            migrationBuilder.DropTable(
                name: "GroupApplicationAssignments");

            migrationBuilder.DropTable(
                name: "GroupRoleAssignments");

            migrationBuilder.DropTable(
                name: "UserGroupMemberships");

            migrationBuilder.DropTable(
                name: "DirectoryGroups");
        }
    }
}
