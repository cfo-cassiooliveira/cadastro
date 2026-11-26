using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cadastro.Database.Migrations
{
    /// <inheritdoc />
    public partial class InitialImportSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "import");

            migrationBuilder.CreateTable(
                name: "audit_entries",
                schema: "import",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Action = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrelationId = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_entries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "import_batches",
                schema: "import",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    ValidLines = table.Column<int>(type: "int", nullable: false),
                    InvalidLines = table.Column<int>(type: "int", nullable: false),
                    DuplicateLines = table.Column<int>(type: "int", nullable: false),
                    CreatedCount = table.Column<int>(type: "int", nullable: false),
                    AlreadyExistsCount = table.Column<int>(type: "int", nullable: false),
                    ErrorCount = table.Column<int>(type: "int", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ConfirmedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FinishedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReportExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_import_batches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "inbox_messages",
                schema: "import",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProcessedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox_messages", x => x.MessageId);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "import",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TraceParent = table.Column<string>(type: "varchar(55)", unicode: false, maxLength: 55, nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LastErrorCode = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "import_lines",
                schema: "import",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    FailureReason = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    LegacyMarkStatus = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    NameCipher = table.Column<byte[]>(type: "varbinary(1024)", maxLength: 1024, nullable: true),
                    EmailCipher = table.Column<byte[]>(type: "varbinary(1024)", maxLength: 1024, nullable: true),
                    CpfCipher = table.Column<byte[]>(type: "varbinary(256)", maxLength: 256, nullable: true),
                    PhoneCipher = table.Column<byte[]>(type: "varbinary(256)", maxLength: 256, nullable: true),
                    EmailIndex = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: true),
                    EmailIndexKeyVersion = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    KeyVersion = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    ReprocessCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FinishedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PersonalDataErasedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_import_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_import_lines_import_batches_BatchId",
                        column: x => x.BatchId,
                        principalSchema: "import",
                        principalTable: "import_batches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_batch",
                schema: "import",
                table: "audit_entries",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_expires",
                schema: "import",
                table: "audit_entries",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "ix_import_batches_report_expires",
                schema: "import",
                table: "import_batches",
                column: "ReportExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "ux_import_batches_file_hash",
                schema: "import",
                table: "import_batches",
                column: "FileHash",
                unique: true,
                filter: "[Status] <> 'Cancelled'");

            migrationBuilder.CreateIndex(
                name: "ix_import_lines_batch_status",
                schema: "import",
                table: "import_lines",
                columns: new[] { "BatchId", "Status" });

            migrationBuilder.CreateIndex(
                name: "ix_import_lines_email_index",
                schema: "import",
                table: "import_lines",
                column: "EmailIndex",
                filter: "[EmailIndex] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_import_lines_batch_line",
                schema: "import",
                table: "import_lines",
                columns: new[] { "BatchId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_pending",
                schema: "import",
                table: "outbox_messages",
                columns: new[] { "NextAttemptAtUtc", "OccurredAtUtc" },
                filter: "[PublishedAtUtc] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_entries",
                schema: "import");

            migrationBuilder.DropTable(
                name: "import_lines",
                schema: "import");

            migrationBuilder.DropTable(
                name: "inbox_messages",
                schema: "import");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "import");

            migrationBuilder.DropTable(
                name: "import_batches",
                schema: "import");
        }
    }
}
