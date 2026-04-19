using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowIdToScheduledTask : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "WorkflowDefinitionId",
                table: "ScheduledTasks",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTasks_WorkflowDefinitionId",
                table: "ScheduledTasks",
                column: "WorkflowDefinitionId");

            migrationBuilder.AddForeignKey(
                name: "FK_ScheduledTasks_WorkflowDefinitions_WorkflowDefinitionId",
                table: "ScheduledTasks",
                column: "WorkflowDefinitionId",
                principalTable: "WorkflowDefinitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ScheduledTasks_WorkflowDefinitions_WorkflowDefinitionId",
                table: "ScheduledTasks");

            migrationBuilder.DropIndex(
                name: "IX_ScheduledTasks_WorkflowDefinitionId",
                table: "ScheduledTasks");

            migrationBuilder.DropColumn(
                name: "WorkflowDefinitionId",
                table: "ScheduledTasks");
        }
    }
}
