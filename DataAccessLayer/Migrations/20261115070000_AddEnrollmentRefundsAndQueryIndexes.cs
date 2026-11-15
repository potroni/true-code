using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataAccessLayer.Migrations
{
    public partial class AddEnrollmentRefundsAndQueryIndexes : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "refunded_visits",
                table: "enrollments",
                type: "integer",
                nullable: true);

            // решение: для исторических отмен сумма неизвестна, поэтому записываем 0 без изменения баланса.
            migrationBuilder.Sql("""
                UPDATE enrollments
                SET refunded_visits = 0
                WHERE status = 'cancelled' AND refunded_visits IS NULL;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_enrollments_refunded_visits",
                table: "enrollments",
                sql: "(status = 'active' AND refunded_visits IS NULL) OR " +
                     "(status = 'cancelled' AND refunded_visits IS NOT NULL AND refunded_visits >= 0)");

            migrationBuilder.CreateIndex(
                name: "ix_classes_scheduled_start_id",
                table: "classes",
                columns: new[] { "starts_at", "id" },
                filter: "NOT is_cancelled");

            migrationBuilder.CreateIndex(
                name: "ix_classes_scheduled_room_start_id",
                table: "classes",
                columns: new[] { "room_id", "starts_at", "id" },
                filter: "NOT is_cancelled");

            migrationBuilder.CreateIndex(
                name: "ix_enrollments_active_class_user",
                table: "enrollments",
                columns: new[] { "class_id", "user_id" },
                filter: "status = 'active'")
                .Annotation("Npgsql:IndexInclude", new[] { "seats" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex("ix_enrollments_active_class_user", "enrollments");
            migrationBuilder.DropIndex("ix_classes_scheduled_room_start_id", "classes");
            migrationBuilder.DropIndex("ix_classes_scheduled_start_id", "classes");
            migrationBuilder.DropCheckConstraint("ck_enrollments_refunded_visits", "enrollments");
            migrationBuilder.DropColumn("refunded_visits", "enrollments");
        }
    }
}
