using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthCenter.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AuthCenterDbContext))]
[Migration("20260808032000_ScopeRoleNamesByApplication")]
public partial class ScopeRoleNamesByApplication : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "DisplayName",
            table: "AspNetRoles",
            type: "nvarchar(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.Sql("""
            UPDATE role
            SET role.DisplayName = COALESCE(role.Name, CONVERT(nvarchar(36), role.Id)),
                role.Name = COALESCE(app.Code, 'GLOBAL') + ':' + COALESCE(role.Name, CONVERT(nvarchar(36), role.Id)),
                role.NormalizedName = UPPER(COALESCE(app.Code, 'GLOBAL') + ':' + COALESCE(role.Name, CONVERT(nvarchar(36), role.Id)))
            FROM AspNetRoles AS role
            LEFT JOIN ApplicationSystems AS app ON app.Id = role.ApplicationSystemId;
            """);

        migrationBuilder.AlterColumn<string>(
            name: "DisplayName",
            table: "AspNetRoles",
            type: "nvarchar(256)",
            maxLength: 256,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "nvarchar(256)",
            oldMaxLength: 256,
            oldNullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "DisplayName", table: "AspNetRoles");
}
