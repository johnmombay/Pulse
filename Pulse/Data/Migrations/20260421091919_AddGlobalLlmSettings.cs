using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGlobalLlmSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GlobalLlmSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    ModelId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ApiVersion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlobalLlmSettings", x => x.Id);
                    table.CheckConstraint("CK_GlobalLlmSettings_Singleton", "[Id] = 1");
                });

            // Seed the singleton row by copying the first non-empty ModelId found
            // in the existing per-tenant AppSettings table, so existing installs keep
            // their model after upgrade. Falls back to "" / "V1Beta" when nothing is set.
            migrationBuilder.Sql(@"
                INSERT INTO [GlobalLlmSettings] ([Id], [ModelId], [ApiVersion])
                SELECT 1,
                       COALESCE((SELECT TOP 1 [ModelId]    FROM [AppSettings] WHERE [ModelId] <> '' ORDER BY [UpdatedAtUtc] DESC), ''),
                       COALESCE((SELECT TOP 1 [ApiVersion] FROM [AppSettings] WHERE [ApiVersion] <> '' ORDER BY [UpdatedAtUtc] DESC), 'V1Beta')
                WHERE NOT EXISTS (SELECT 1 FROM [GlobalLlmSettings] WHERE [Id] = 1);
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GlobalLlmSettings");
        }
    }
}
