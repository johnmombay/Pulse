using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentDefinitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgentDefinitionId",
                table: "AgentMemories",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AgentDefinitions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Icon = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    SystemPrompt = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModelId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    IsOrchestrator = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    AllowedPluginKeysJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AllowedSkillIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AllowedMcpServerIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AllowedDatabaseKeysJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AllowedFlatFileIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AllowedRagDocumentIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentDefinitions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentMemories_UserId_AgentDefinitionId_IsActive",
                table: "AgentMemories",
                columns: new[] { "UserId", "AgentDefinitionId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentDefinitions_TenantId",
                table: "AgentDefinitions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentDefinitions_TenantId_IsOrchestrator",
                table: "AgentDefinitions",
                columns: new[] { "TenantId", "IsOrchestrator" },
                unique: true,
                filter: "[IsOrchestrator] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentDefinitions");

            migrationBuilder.DropIndex(
                name: "IX_AgentMemories_UserId_AgentDefinitionId_IsActive",
                table: "AgentMemories");

            migrationBuilder.DropColumn(
                name: "AgentDefinitionId",
                table: "AgentMemories");
        }
    }
}
