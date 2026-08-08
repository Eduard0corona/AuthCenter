using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthCenter.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AuthCenterDbContext))]
[Migration("20260808031000_AddDistributedRateLimitBuckets")]
public partial class AddDistributedRateLimitBuckets : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "DistributedRateLimitBuckets",
            columns: table => new
            {
                Key = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                WindowStartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                PermitCount = table.Column<int>(type: "int", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_DistributedRateLimitBuckets", x => x.Key));

        migrationBuilder.CreateIndex(
            name: "IX_DistributedRateLimitBuckets_ExpiresAt",
            table: "DistributedRateLimitBuckets",
            column: "ExpiresAt");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "DistributedRateLimitBuckets");
}
