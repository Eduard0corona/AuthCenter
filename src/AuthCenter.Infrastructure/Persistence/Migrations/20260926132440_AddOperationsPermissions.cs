using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthCenter.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Event hooks, federation and provisioning get their own AuthCenter permissions. Every role that
    /// held the applications permission they rode on receives the new one, so no operator loses access
    /// (new permissions appear in tokens issued after the migration).
    /// </summary>
    public partial class AddOperationsPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DECLARE @applicationId uniqueidentifier;
                SELECT TOP (1) @applicationId = Id FROM ApplicationSystems WHERE Code = 'AUTHCENTER';

                IF @applicationId IS NOT NULL
                BEGIN
                    DECLARE @map TABLE (Code nvarchar(100) NOT NULL, Name nvarchar(200) NOT NULL, LegacyCode nvarchar(100) NOT NULL);
                    INSERT INTO @map (Code, Name, LegacyCode) VALUES
                        ('AUTHCENTER_EVENT_HOOKS_READ', 'Read event hooks and their deliveries', 'AUTHCENTER_APPLICATIONS_READ'),
                        ('AUTHCENTER_EVENT_HOOKS_WRITE', 'Manage event hooks and replay deliveries', 'AUTHCENTER_APPLICATIONS_WRITE'),
                        ('AUTHCENTER_FEDERATION_READ', 'Read enterprise federation providers and routing', 'AUTHCENTER_APPLICATIONS_READ'),
                        ('AUTHCENTER_FEDERATION_WRITE', 'Manage enterprise federation providers and routing', 'AUTHCENTER_APPLICATIONS_WRITE'),
                        ('AUTHCENTER_PROVISIONING_READ', 'Read provisioning tokens', 'AUTHCENTER_APPLICATIONS_READ'),
                        ('AUTHCENTER_PROVISIONING_WRITE', 'Manage provisioning tokens', 'AUTHCENTER_APPLICATIONS_WRITE');

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE rp FROM RolePermissions rp
                JOIN Permissions p ON p.Id = rp.PermissionId
                JOIN ApplicationSystems a ON a.Id = p.ApplicationSystemId AND a.Code = 'AUTHCENTER'
                WHERE p.Code IN ('AUTHCENTER_EVENT_HOOKS_READ', 'AUTHCENTER_EVENT_HOOKS_WRITE', 'AUTHCENTER_FEDERATION_READ',
                                 'AUTHCENTER_FEDERATION_WRITE', 'AUTHCENTER_PROVISIONING_READ', 'AUTHCENTER_PROVISIONING_WRITE');
                DELETE p FROM Permissions p
                JOIN ApplicationSystems a ON a.Id = p.ApplicationSystemId AND a.Code = 'AUTHCENTER'
                WHERE p.Code IN ('AUTHCENTER_EVENT_HOOKS_READ', 'AUTHCENTER_EVENT_HOOKS_WRITE', 'AUTHCENTER_FEDERATION_READ',
                                 'AUTHCENTER_FEDERATION_WRITE', 'AUTHCENTER_PROVISIONING_READ', 'AUTHCENTER_PROVISIONING_WRITE');
                """);
        }
    }
}
