using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthCenter.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AuthCenterDbContext))]
[Migration("20260808033000_AddOutbox")]
public partial class AddOutbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "OutboxMessages",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Type = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                ProtectedPayload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                NextAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                LockedUntil = table.Column<DateTime>(type: "datetime2", nullable: true),
                ProcessedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                AttemptCount = table.Column<int>(type: "int", nullable: false),
                LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_OutboxMessages", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessages_ProcessedAt_NextAttemptAt_LockedUntil",
            table: "OutboxMessages",
            columns: new[] { "ProcessedAt", "NextAttemptAt", "LockedUntil" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "OutboxMessages");
}
