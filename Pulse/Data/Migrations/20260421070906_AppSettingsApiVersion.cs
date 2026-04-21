using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Data.Migrations
{
    /// <inheritdoc />
    public partial class AppSettingsApiVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApiVersion",
                table: "AppSettings",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApiVersion",
                table: "AppSettings");
        }
    }
}
