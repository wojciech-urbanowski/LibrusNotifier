using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LibrusNotifier.Data.Migrations
{
    /// <inheritdoc />
    public partial class DailyDigest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastFetchUtc",
                table: "Baselines",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // Istniejące bazy: do następnego pobrania listy za ostatnie pobranie uznajemy pierwsze (zapis początkowy).
            migrationBuilder.Sql("UPDATE \"Baselines\" SET \"LastFetchUtc\" = \"CompletedUtc\";");

            migrationBuilder.CreateTable(
                name: "DigestLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Day = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Group = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    SentUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EventCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DigestLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DigestLogs_Day_Kind_Group",
                table: "DigestLogs",
                columns: new[] { "Day", "Kind", "Group" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DigestLogs");

            migrationBuilder.DropColumn(
                name: "LastFetchUtc",
                table: "Baselines");
        }
    }
}
