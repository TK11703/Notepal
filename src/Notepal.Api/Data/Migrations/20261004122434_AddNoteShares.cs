using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Notepal.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNoteShares : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NoteShares",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NoteId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OwnerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    OwnerEmail = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    RecipientId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    RecipientEmail = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    RecipientName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Permission = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NoteShares", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NoteShares_Notes_NoteId",
                        column: x => x.NoteId,
                        principalTable: "Notes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NoteShares_NoteId_RecipientEmail",
                table: "NoteShares",
                columns: new[] { "NoteId", "RecipientEmail" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NoteShares_OwnerId",
                table: "NoteShares",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_NoteShares_RecipientEmail",
                table: "NoteShares",
                column: "RecipientEmail");

            migrationBuilder.CreateIndex(
                name: "IX_NoteShares_RecipientId",
                table: "NoteShares",
                column: "RecipientId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NoteShares");
        }
    }
}
