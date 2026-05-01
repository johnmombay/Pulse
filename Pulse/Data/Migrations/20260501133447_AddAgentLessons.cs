using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentLessons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentLessons",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    AgentDefinitionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Domain = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    GoalText = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ActionTaken = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LessonText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Score = table.Column<double>(type: "float", nullable: false),
                    ReinforcementCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EmbeddingJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentLessons", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentLessons_TenantId",
                table: "AgentLessons",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentLessons_TenantId_Domain",
                table: "AgentLessons",
                columns: new[] { "TenantId", "Domain" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentLessons_TenantId_Domain_AgentDefinitionId",
                table: "AgentLessons",
                columns: new[] { "TenantId", "Domain", "AgentDefinitionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentLessons");
        }
    }
}
