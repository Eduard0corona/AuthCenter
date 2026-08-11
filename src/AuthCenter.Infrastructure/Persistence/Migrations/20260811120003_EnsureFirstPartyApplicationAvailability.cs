using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthCenter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnsureFirstPartyApplicationAvailability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DECLARE @applicationId uniqueidentifier;
                SELECT TOP (1) @applicationId = Id
                FROM ApplicationSystems
                WHERE Code = 'AUTHCENTER';

                IF @applicationId IS NULL
                BEGIN
                    SET @applicationId = NEWID();
                    INSERT INTO ApplicationSystems
                        (Id, Code, Name, Description, IsActive, CreatedAt, UpdatedAt)
                    VALUES
                        (@applicationId, 'AUTHCENTER', 'AuthCenter',
                         'First-party administration and account portal', 1, SYSUTCDATETIME(), NULL);
                END
                ELSE
                BEGIN
                    UPDATE ApplicationSystems
                    SET IsActive = 1,
                        UpdatedAt = CASE WHEN IsActive = 0 THEN SYSUTCDATETIME() ELSE UpdatedAt END
                    WHERE Id = @applicationId;
                END;

                IF NOT EXISTS (
                    SELECT 1 FROM ApplicationRegistrationSettings WHERE ApplicationSystemId = @applicationId
                )
                BEGIN
                    INSERT INTO ApplicationRegistrationSettings
                        (Id, ApplicationSystemId, RegistrationMode, DefaultRoleId,
                         RequireEmailConfirmation, AllowGoogleLogin, AllowMicrosoftLogin,
                         AllowGitHubLogin, AllowAppleLogin, AllowMagicLink, AllowPasswordLogin,
                         RequireMfa, AllowedEmailDomains, CreatedAt, UpdatedAt)
                    VALUES
                        (NEWID(), @applicationId, 2, NULL,
                         0, 1, 0, 0, 0, 0, 1,
                         0, NULL, SYSUTCDATETIME(), NULL);
                END;

                IF NOT EXISTS (
                    SELECT 1 FROM ApplicationBrandingSettings WHERE ApplicationSystemId = @applicationId
                )
                BEGIN
                    INSERT INTO ApplicationBrandingSettings
                        (Id, ApplicationSystemId, DisplayName, PrimaryColor, BackgroundColor,
                         LogoUrl, SupportUrl, PrivacyUrl, TermsUrl, CreatedAt, UpdatedAt)
                    VALUES
                        (NEWID(), @applicationId, 'AuthCenter', '#2563EB', '#F8FAFC',
                         NULL, NULL, NULL, NULL, SYSUTCDATETIME(), NULL);
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Baseline identity data is intentionally retained on downgrade. Deleting the
            // first-party application could cascade into operator access, roles and sessions.
        }
    }
}
