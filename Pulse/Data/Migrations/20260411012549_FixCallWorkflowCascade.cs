using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Data.Migrations
{
    /// <inheritdoc />
    public partial class FixCallWorkflowCascade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WorkflowSteps_WorkflowDefinitions_CalledWorkflowId",
                table: "WorkflowSteps");

            migrationBuilder.AddForeignKey(
                name: "FK_WorkflowSteps_WorkflowDefinitions_CalledWorkflowId",
                table: "WorkflowSteps",
                column: "CalledWorkflowId",
                principalTable: "WorkflowDefinitions",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WorkflowSteps_WorkflowDefinitions_CalledWorkflowId",
                table: "WorkflowSteps");

            migrationBuilder.AddForeignKey(
                name: "FK_WorkflowSteps_WorkflowDefinitions_CalledWorkflowId",
                table: "WorkflowSteps",
                column: "CalledWorkflowId",
                principalTable: "WorkflowDefinitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
