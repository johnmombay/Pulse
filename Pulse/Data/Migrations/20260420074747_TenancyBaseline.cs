using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Data.Migrations
{
    /// <inheritdoc />
    public partial class TenancyBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AppSettings_Singleton",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "AgentMail_ApiKey",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "AgentMail_BaseUrl",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "AgentMail_DefaultInbox",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "AgentMail_IsEnabled",
                table: "AppSettings");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "WorkflowRuns",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "WorkflowDefinitions",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Skills",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "ScheduledTasks",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "ScheduledTaskResults",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "RagDocuments",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "McpServers",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "FlatFileSources",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "DatabaseConnections",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "ChatMessages",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "AspNetUserTokens",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128);

            migrationBuilder.AlterColumn<string>(
                name: "LoginProvider",
                table: "AspNetUserTokens",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AspNetUsers",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ProviderKey",
                table: "AspNetUserLogins",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128);

            migrationBuilder.AlterColumn<string>(
                name: "LoginProvider",
                table: "AspNetUserLogins",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AppSettings",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AgentMemories",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "GlobalAgentMailSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    ApiBaseUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ApiKey = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    FromAddress = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FromName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DefaultInbox = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlobalAgentMailSettings", x => x.Id);
                    table.CheckConstraint("CK_GlobalAgentMailSettings_Singleton", "[Id] = 1");
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_TenantId",
                table: "WorkflowRuns",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowDefinitions_TenantId",
                table: "WorkflowDefinitions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Skills_TenantId",
                table: "Skills",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTasks_TenantId",
                table: "ScheduledTasks",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTaskResults_TenantId",
                table: "ScheduledTaskResults",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_RagDocuments_TenantId",
                table: "RagDocuments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_McpServers_TenantId",
                table: "McpServers",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_FlatFileSources_TenantId",
                table: "FlatFileSources",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseConnections_TenantId",
                table: "DatabaseConnections",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_TenantId",
                table: "ChatMessages",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_TenantId",
                table: "AspNetUsers",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AppSettings_TenantId",
                table: "AppSettings",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentMemories_TenantId",
                table: "AgentMemories",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Name",
                table: "Tenants",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Slug",
                table: "Tenants",
                column: "Slug",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUsers_Tenants_TenantId",
                table: "AspNetUsers",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUsers_Tenants_TenantId",
                table: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "GlobalAgentMailSettings");

            migrationBuilder.DropTable(
                name: "Tenants");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowRuns_TenantId",
                table: "WorkflowRuns");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowDefinitions_TenantId",
                table: "WorkflowDefinitions");

            migrationBuilder.DropIndex(
                name: "IX_Skills_TenantId",
                table: "Skills");

            migrationBuilder.DropIndex(
                name: "IX_ScheduledTasks_TenantId",
                table: "ScheduledTasks");

            migrationBuilder.DropIndex(
                name: "IX_ScheduledTaskResults_TenantId",
                table: "ScheduledTaskResults");

            migrationBuilder.DropIndex(
                name: "IX_RagDocuments_TenantId",
                table: "RagDocuments");

            migrationBuilder.DropIndex(
                name: "IX_McpServers_TenantId",
                table: "McpServers");

            migrationBuilder.DropIndex(
                name: "IX_FlatFileSources_TenantId",
                table: "FlatFileSources");

            migrationBuilder.DropIndex(
                name: "IX_DatabaseConnections_TenantId",
                table: "DatabaseConnections");

            migrationBuilder.DropIndex(
                name: "IX_ChatMessages_TenantId",
                table: "ChatMessages");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_TenantId",
                table: "AspNetUsers");

            migrationBuilder.DropIndex(
                name: "IX_AppSettings_TenantId",
                table: "AppSettings");

            migrationBuilder.DropIndex(
                name: "IX_AgentMemories_TenantId",
                table: "AgentMemories");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "WorkflowDefinitions");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Skills");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "ScheduledTasks");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "ScheduledTaskResults");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "RagDocuments");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "McpServers");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "FlatFileSources");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "DatabaseConnections");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "ChatMessages");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AgentMemories");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "AspNetUserTokens",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");

            migrationBuilder.AlterColumn<string>(
                name: "LoginProvider",
                table: "AspNetUserTokens",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");

            migrationBuilder.AlterColumn<string>(
                name: "ProviderKey",
                table: "AspNetUserLogins",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");

            migrationBuilder.AlterColumn<string>(
                name: "LoginProvider",
                table: "AspNetUserLogins",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");

            migrationBuilder.AddColumn<string>(
                name: "AgentMail_ApiKey",
                table: "AppSettings",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AgentMail_BaseUrl",
                table: "AppSettings",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AgentMail_DefaultInbox",
                table: "AppSettings",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "AgentMail_IsEnabled",
                table: "AppSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "CK_AppSettings_Singleton",
                table: "AppSettings",
                sql: "[Id] = 1");
        }
    }
}
