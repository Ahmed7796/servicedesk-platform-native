using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServiceDesk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditLogSequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_ServiceRequestId",
                table: "AuditLogs");

            migrationBuilder.AddColumn<int>(
                name: "Sequence",
                table: "AuditLogs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Hand-written: number the existing rows 1, 2, 3... per request before the unique index is created.
            migrationBuilder.Sql(@"
                UPDATE ""AuditLogs"" a
                SET ""Sequence"" = n.rn
                FROM (
                    SELECT ""Id"", ROW_NUMBER() OVER (PARTITION BY ""ServiceRequestId"" ORDER BY ""OccurredAt"", CASE WHEN ""Action"" = 'Submitted' THEN 0 ELSE 1 END, ""Id"") AS rn
                    FROM ""AuditLogs""
                ) n
                WHERE a.""Id"" = n.""Id"";");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_ServiceRequestId_Sequence",
                table: "AuditLogs",
                columns: new[] { "ServiceRequestId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_ServiceRequestId_Sequence",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "Sequence",
                table: "AuditLogs");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_ServiceRequestId",
                table: "AuditLogs",
                column: "ServiceRequestId");
        }
    }
}
