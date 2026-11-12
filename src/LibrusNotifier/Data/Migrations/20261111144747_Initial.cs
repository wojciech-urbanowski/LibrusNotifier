using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LibrusNotifier.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Baselines",
                columns: table => new
                {
                    Account = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Module = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Baselines", x => new { x.Account, x.Module });
                });

            migrationBuilder.CreateTable(
                name: "Elements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Type = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DataJson = table.Column<string>(type: "TEXT", nullable: false),
                    DetailsJson = table.Column<string>(type: "TEXT", nullable: true),
                    VisibleToParent = table.Column<bool>(type: "INTEGER", nullable: false),
                    VisibleToStudent = table.Column<bool>(type: "INTEGER", nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Elements", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ElementId = table.Column<int>(type: "INTEGER", nullable: false),
                    Group = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SentUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notifications_Elements_ElementId",
                        column: x => x.ElementId,
                        principalTable: "Elements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Elements_Key",
                table: "Elements",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Elements_Type",
                table: "Elements",
                column: "Type");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_ElementId_Group",
                table: "Notifications",
                columns: new[] { "ElementId", "Group" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_Status",
                table: "Notifications",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Baselines");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "Elements");
        }
    }
}
