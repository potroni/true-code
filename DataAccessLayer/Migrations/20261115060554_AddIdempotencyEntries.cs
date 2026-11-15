using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DataAccessLayer.Migrations
{
    /// <inheritdoc />
    public partial class AddIdempotencyEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "idempotency_entries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    operation = table.Column<int>(type: "integer", nullable: false),
                    key_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    original_key = table.Column<string>(type: "text", nullable: false),
                    class_id = table.Column<long>(type: "bigint", nullable: false),
                    seats = table.Column<int>(type: "integer", nullable: false),
                    response_body = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency_entries", x => x.id);
                    table.CheckConstraint("ck_idempotency_entries_key_hash", "octet_length(key_hash) = 32");
                    table.CheckConstraint("ck_idempotency_entries_operation", "operation IN (1, 2)");
                    table.CheckConstraint("ck_idempotency_entries_seats", "seats > 0");
                    table.ForeignKey(
                        name: "FK_idempotency_entries_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_idempotency_entries_user_operation_hash",
                table: "idempotency_entries",
                columns: new[] { "user_id", "operation", "key_hash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "idempotency_entries");
        }
    }
}
