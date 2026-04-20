using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Data.Migrations
{
    /// <inheritdoc />
    public partial class AppSettingsIdentityId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQL Server cannot convert an existing column to IDENTITY in-place.
            // We have to drop the column and re-create it. The pre-multi-tenant
            // singleton AppSettings row (Id = 1, TenantId = Guid.Empty) is no
            // longer valid in the new per-tenant model, so we clear the table
            // before recreating the column. Per-tenant rows are recreated on
            // demand by SignupTenant / LlmSettingsService.
            migrationBuilder.Sql("DELETE FROM [AppSettings];");

            migrationBuilder.DropPrimaryKey(
                name: "PK_AppSettings",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "AppSettings");

            migrationBuilder.AddColumn<int>(
                name: "Id",
                table: "AppSettings",
                type: "int",
                nullable: false,
                defaultValue: 0)
                .Annotation("SqlServer:Identity", "1, 1");

            migrationBuilder.AddPrimaryKey(
                name: "PK_AppSettings",
                table: "AppSettings",
                column: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_AppSettings",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "AppSettings");

            migrationBuilder.AddColumn<int>(
                name: "Id",
                table: "AppSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddPrimaryKey(
                name: "PK_AppSettings",
                table: "AppSettings",
                column: "Id");
        }
    }
}
