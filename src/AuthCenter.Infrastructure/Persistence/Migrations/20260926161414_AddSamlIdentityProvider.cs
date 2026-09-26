using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthCenter.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// SAML applications (AuthCenter as their identity provider) and their permissions, granted to
    /// the roles that manage OAuth clients.
    /// </summary>
    public partial class AddSamlIdentityProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SamlServiceProviders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    ApplicationSystemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    EntityId = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    AssertionConsumerServiceUrlsJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    SingleLogoutServiceUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    NameIdFormat = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameIdSalt = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SigningCertificate = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    RequireSignedRequests = table.Column<bool>(type: "bit", nullable: false),
                    EncryptionCertificate = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    EncryptAssertions = table.Column<bool>(type: "bit", nullable: false),
                    SignResponse = table.Column<bool>(type: "bit", nullable: false),
                    AttributesJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    AllowIdpInitiated = table.Column<bool>(type: "bit", nullable: false),
                    DefaultRelayState = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AssertionLifetimeMinutes = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SamlServiceProviders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SamlServiceProviders_ApplicationSystems_ApplicationSystemId",
                        column: x => x.ApplicationSystemId,
                        principalTable: "ApplicationSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SamlServiceProviders_ApplicationSystemId",
                table: "SamlServiceProviders",
                column: "ApplicationSystemId");

            migrationBuilder.CreateIndex(
                name: "IX_SamlServiceProviders_EntityId",
                table: "SamlServiceProviders",
                column: "EntityId",
                unique: true);

            // Every role that manages OAuth clients manages SAML applications too, so no operator
            // loses the integrations they looked after.
            migrationBuilder.Sql("""
                DECLARE @applicationId uniqueidentifier;
                SELECT TOP (1) @applicationId = Id FROM ApplicationSystems WHERE Code = 'AUTHCENTER';

                IF @applicationId IS NOT NULL
                BEGIN
                    DECLARE @map TABLE (Code nvarchar(100) NOT NULL, Name nvarchar(200) NOT NULL, LegacyCode nvarchar(100) NOT NULL);
                    INSERT INTO @map (Code, Name, LegacyCode) VALUES
                        ('AUTHCENTER_SAML_APPS_READ', 'Read SAML applications and the identity provider', 'AUTHCENTER_OAUTH_CLIENTS_READ'),
                        ('AUTHCENTER_SAML_APPS_WRITE', 'Manage SAML applications', 'AUTHCENTER_OAUTH_CLIENTS_WRITE');

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
                WHERE p.Code IN ('AUTHCENTER_SAML_APPS_READ', 'AUTHCENTER_SAML_APPS_WRITE');
                DELETE p FROM Permissions p
                JOIN ApplicationSystems a ON a.Id = p.ApplicationSystemId AND a.Code = 'AUTHCENTER'
                WHERE p.Code IN ('AUTHCENTER_SAML_APPS_READ', 'AUTHCENTER_SAML_APPS_WRITE');
                """);

            migrationBuilder.DropTable(
                name: "SamlServiceProviders");
        }
    }
}
