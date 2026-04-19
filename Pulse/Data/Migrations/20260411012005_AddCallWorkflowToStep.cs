using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCallWorkflowToStep : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CalledWorkflowId",
                table: "WorkflowSteps",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowSteps_CalledWorkflowId",
                table: "WorkflowSteps",
                column: "CalledWorkflowId");

            migrationBuilder.AddForeignKey(
                name: "FK_WorkflowSteps_WorkflowDefinitions_CalledWorkflowId",
                table: "WorkflowSteps",
                column: "CalledWorkflowId",
                principalTable: "WorkflowDefinitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WorkflowSteps_WorkflowDefinitions_CalledWorkflowId",
                table: "WorkflowSteps");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowSteps_CalledWorkflowId",
                table: "WorkflowSteps");

            migrationBuilder.DropColumn(
                name: "CalledWorkflowId",
                table: "WorkflowSteps");
        }
    }
}
