using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShareExpenses.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class SignInCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SignInCodes",
                schema: "identity",
                columns: table => new
                {
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CodeHash = table.Column<string>(type: "text", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AttemptsLeft = table.Column<int>(type: "integer", nullable: false),
                    CodesIssued = table.Column<int>(type: "integer", nullable: false),
                    WindowStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignInCodes", x => x.NormalizedEmail);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SignInCodes",
                schema: "identity");
        }
    }
}
