using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShiftHandover.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class RestrictShiftRecordDeletes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Accidents_Shifts_ShiftId",
                table: "Accidents");

            migrationBuilder.DropForeignKey(
                name: "FK_Incidents_Shifts_ShiftId",
                table: "Incidents");

            migrationBuilder.DropForeignKey(
                name: "FK_ManpowerRecords_Shifts_ShiftId",
                table: "ManpowerRecords");

            migrationBuilder.AddForeignKey(
                name: "FK_Accidents_Shifts_ShiftId",
                table: "Accidents",
                column: "ShiftId",
                principalTable: "Shifts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Incidents_Shifts_ShiftId",
                table: "Incidents",
                column: "ShiftId",
                principalTable: "Shifts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ManpowerRecords_Shifts_ShiftId",
                table: "ManpowerRecords",
                column: "ShiftId",
                principalTable: "Shifts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Accidents_Shifts_ShiftId",
                table: "Accidents");

            migrationBuilder.DropForeignKey(
                name: "FK_Incidents_Shifts_ShiftId",
                table: "Incidents");

            migrationBuilder.DropForeignKey(
                name: "FK_ManpowerRecords_Shifts_ShiftId",
                table: "ManpowerRecords");

            migrationBuilder.AddForeignKey(
                name: "FK_Accidents_Shifts_ShiftId",
                table: "Accidents",
                column: "ShiftId",
                principalTable: "Shifts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Incidents_Shifts_ShiftId",
                table: "Incidents",
                column: "ShiftId",
                principalTable: "Shifts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ManpowerRecords_Shifts_ShiftId",
                table: "ManpowerRecords",
                column: "ShiftId",
                principalTable: "Shifts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
