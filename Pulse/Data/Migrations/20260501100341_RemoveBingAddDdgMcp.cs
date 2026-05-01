using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveBingAddDdgMcp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WebSearch_ApiKey",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "WebSearch_IsEnabled",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "WebSearch_MaxResults",
                table: "AppSettings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WebSearch_ApiKey",
                table: "AppSettings",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "WebSearch_IsEnabled",
                table: "AppSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "WebSearch_MaxResults",
                table: "AppSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }
    }
}
