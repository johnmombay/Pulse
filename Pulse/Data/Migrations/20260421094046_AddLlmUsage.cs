using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LlmUsage",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    AgentName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModelId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PromptTokens = table.Column<int>(type: "int", nullable: false),
                    CompletionTokens = table.Column<int>(type: "int", nullable: false),
                    TotalTokens = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LlmUsage", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LlmUsage_TenantId",
                table: "LlmUsage",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_LlmUsage_TenantId_CreatedUtc",
                table: "LlmUsage",
                columns: new[] { "TenantId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LlmUsage_TenantId_UserId_CreatedUtc",
                table: "LlmUsage",
                columns: new[] { "TenantId", "UserId", "CreatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LlmUsage");
        }
    }
}
