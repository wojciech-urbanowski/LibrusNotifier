using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LibrusNotifier.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChangeDetection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Notifications_ElementId_Group",
                table: "Notifications");

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Notifications",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "New");

            migrationBuilder.AddColumn<int>(
                name: "RelatedElementId",
                table: "Notifications",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "GoneHandledUtc",
                table: "Elements",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_ElementId_Group_Kind",
                table: "Notifications",
                columns: new[] { "ElementId", "Group", "Kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Notifications_ElementId_Group_Kind",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "RelatedElementId",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "GoneHandledUtc",
                table: "Elements");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_ElementId_Group",
                table: "Notifications",
                columns: new[] { "ElementId", "Group" },
                unique: true);
        }
    }
}
