using Cadastro.Domain.UserImports;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Cadastro.Infrastructure.Persistence;

/// <summary>
/// Contexto do esquema <c>import</c> do banco <c>LegadoUsers</c> (exceção ao Art. III.4, ADR-0007). O runtime usa o login
/// <c>cadastro_app</c> (sem acesso ao <c>dbo</c>); as migrations rodam com <c>cadastro_migrator</c>.
/// </summary>
public sealed class CadastroDbContext(DbContextOptions<CadastroDbContext> options) : DbContext(options)
{
    /// <summary>Esquema dono das tabelas do Cadastro dentro do banco do Legado.</summary>
    public const string Schema = "import";

    /// <summary>Assembly que guarda as migrations (projeto <c>Cadastro.Database</c>).</summary>
    public const string MigrationsAssembly = "Cadastro.Database";

    /// <summary>Lotes de importação.</summary>
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();

    /// <summary>Linhas dos lotes.</summary>
    public DbSet<ImportLine> ImportLines => Set<ImportLine>();

    /// <summary>Trilha de auditoria.</summary>
    public DbSet<ImportAuditEntry> AuditEntries => Set<ImportAuditEntry>();

    /// <summary>Outbox de mensagens a publicar.</summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>Inbox de mensagens já consumidas.</summary>
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    /// <summary>
    /// Opções comuns ao runtime e ao design-time: histórico de migrations dentro do esquema <c>import</c>, porque a
    /// tabela <c>dbo.__EFMigrationsHistory</c> pertence ao Legado e o <c>cadastro_app</c> não enxerga o <c>dbo</c>.
    /// </summary>
    public static void ConfigureSqlServer(SqlServerDbContextOptionsBuilder sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        sql.MigrationsAssembly(MigrationsAssembly);
        sql.MigrationsHistoryTable("__EFMigrationsHistory", Schema);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<ImportBatch>(b =>
        {
            b.ToTable("import_batches");
            b.HasKey(x => x.Id);
            b.Property(x => x.FileHash).HasMaxLength(64).IsUnicode(false).IsFixedLength().IsRequired();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
            b.Property(x => x.CreatedBy).HasMaxLength(200).IsRequired();
            b.Ignore(x => x.FinishedLines);
            // Concorrência otimista: consumidores em paralelo atualizam os contadores; o segundo a gravar relê e tenta de novo.
            b.Property(x => x.RowVersion).IsRowVersion();
            // Idempotência do reenvio: o mesmo arquivo só existe uma vez, exceto lotes cancelados (podem ser reenviados).
            b.HasIndex(x => x.FileHash).IsUnique().HasFilter("[Status] <> 'Cancelled'").HasDatabaseName("ux_import_batches_file_hash");
            // Limpeza de retenção (relatório de 90 dias).
            b.HasIndex(x => x.ReportExpiresAtUtc).HasDatabaseName("ix_import_batches_report_expires");
        });

        modelBuilder.Entity<ImportLine>(l =>
        {
            l.ToTable("import_lines");
            l.HasKey(x => x.Id);
            l.Property(x => x.Status).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
            l.Property(x => x.LegacyMarkStatus).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
            l.Property(x => x.FailureReason).HasMaxLength(100).IsUnicode(false);
            l.Property(x => x.NameCipher).HasMaxLength(1024);
            l.Property(x => x.EmailCipher).HasMaxLength(1024);
            l.Property(x => x.CpfCipher).HasMaxLength(256);
            l.Property(x => x.PhoneCipher).HasMaxLength(256);
            l.Property(x => x.EmailIndex).HasMaxLength(32).IsFixedLength();
            l.Property(x => x.KeyVersion).HasMaxLength(50).IsUnicode(false);
            l.Property(x => x.EmailIndexKeyVersion).HasMaxLength(50).IsUnicode(false);
            l.Ignore(x => x.HasPersonalData);
            l.HasOne<ImportBatch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Cascade);
            l.HasIndex(x => new { x.BatchId, x.LineNumber }).IsUnique().HasDatabaseName("ux_import_lines_batch_line");
            // Chave por e-mail (achar/detectar duplicidade sem decifrar); só linhas com dado pessoal ainda guardado.
            l.HasIndex(x => x.EmailIndex).HasFilter("[EmailIndex] IS NOT NULL").HasDatabaseName("ix_import_lines_email_index");
            l.HasIndex(x => new { x.BatchId, x.Status }).HasDatabaseName("ix_import_lines_batch_status");
        });

        modelBuilder.Entity<ImportAuditEntry>(a =>
        {
            a.ToTable("audit_entries");
            a.HasKey(x => x.Id);
            a.Property(x => x.Actor).HasMaxLength(200).IsRequired();
            a.Property(x => x.Action).HasMaxLength(100).IsUnicode(false).IsRequired();
            a.Property(x => x.CorrelationId).HasMaxLength(100).IsUnicode(false);
            a.HasIndex(x => x.ExpiresAtUtc).HasDatabaseName("ix_audit_entries_expires");
            a.HasIndex(x => x.BatchId).HasDatabaseName("ix_audit_entries_batch");
        });

        modelBuilder.Entity<OutboxMessage>(o =>
        {
            o.ToTable("outbox_messages");
            o.HasKey(x => x.Id);
            o.Property(x => x.Type).HasMaxLength(200).IsUnicode(false).IsRequired();
            o.Property(x => x.Payload).IsRequired();
            o.Property(x => x.TraceParent).HasMaxLength(55).IsUnicode(false);
            o.Property(x => x.LastErrorCode).HasMaxLength(100).IsUnicode(false);
            // O relay lê só as pendentes, em ordem de gravação.
            o.HasIndex(x => new { x.NextAttemptAtUtc, x.OccurredAtUtc }).HasFilter("[PublishedAtUtc] IS NULL").HasDatabaseName("ix_outbox_messages_pending");
        });

        modelBuilder.Entity<InboxMessage>(i =>
        {
            i.ToTable("inbox_messages");
            i.HasKey(x => x.MessageId);
            i.Property(x => x.MessageId).ValueGeneratedNever();
        });
    }
}
