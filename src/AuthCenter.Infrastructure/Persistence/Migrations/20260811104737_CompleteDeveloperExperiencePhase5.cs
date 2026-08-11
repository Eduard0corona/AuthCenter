using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthCenter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompleteDeveloperExperiencePhase5 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApplicationBrandingSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationSystemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PrimaryColor = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false),
                    BackgroundColor = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false),
                    LogoUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SupportUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PrivacyUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TermsUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationBrandingSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApplicationBrandingSettings_ApplicationSystems_ApplicationSystemId",
                        column: x => x.ApplicationSystemId,
                        principalTable: "ApplicationSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OAuthConsentGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OAuthClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScopesJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    GrantedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OAuthConsentGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OAuthConsentGrants_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OAuthConsentGrants_OAuthClients_OAuthClientId",
                        column: x => x.OAuthClientId,
                        principalTable: "OAuthClients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationBrandingSettings_ApplicationSystemId",
                table: "ApplicationBrandingSettings",
                column: "ApplicationSystemId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OAuthConsentGrants_OAuthClientId",
                table: "OAuthConsentGrants",
                column: "OAuthClientId");

            migrationBuilder.CreateIndex(
                name: "IX_OAuthConsentGrants_UserId_OAuthClientId",
                table: "OAuthConsentGrants",
                columns: new[] { "UserId", "OAuthClientId" },
                unique: true);

            migrationBuilder.Sql("""
                INSERT INTO ApplicationBrandingSettings
                    (Id, ApplicationSystemId, DisplayName, PrimaryColor, BackgroundColor, CreatedAt)
                SELECT NEWID(), app.Id, LEFT(app.Name, 100), '#2563EB', '#F8FAFC', SYSUTCDATETIME()
                FROM ApplicationSystems app
                WHERE NOT EXISTS (
                    SELECT 1 FROM ApplicationBrandingSettings branding
                    WHERE branding.ApplicationSystemId = app.Id
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApplicationBrandingSettings");

            migrationBuilder.DropTable(
                name: "OAuthConsentGrants");
        }
    }
}
