using Pulse.Data.Entities;
using Pulse.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Data
{
	public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
		: IdentityDbContext<ApplicationUser>(options)
	{
		public DbSet<AgentMemory>          AgentMemories        => Set<AgentMemory>();
		public DbSet<ChatMessageEntity>    ChatMessages         => Set<ChatMessageEntity>();
		public DbSet<ScheduledTask>        ScheduledTasks       => Set<ScheduledTask>();
		public DbSet<ScheduledTaskResult>  ScheduledTaskResults => Set<ScheduledTaskResult>();
		public DbSet<WorkflowDefinition>   WorkflowDefinitions  => Set<WorkflowDefinition>();
		public DbSet<WorkflowStep>         WorkflowSteps        => Set<WorkflowStep>();
		public DbSet<WorkflowRun>          WorkflowRuns         => Set<WorkflowRun>();
		public DbSet<WorkflowStepRun>      WorkflowStepRuns     => Set<WorkflowStepRun>();

		// ── LLM / App settings (migrated from llm-settings.json) ─────────────────
		public DbSet<AppSettingsEntity>        AppSettings          => Set<AppSettingsEntity>();
		public DbSet<AppApiKeyEntity>          AppApiKeys           => Set<AppApiKeyEntity>();
		public DbSet<McpServerEntity>          McpServers           => Set<McpServerEntity>();
		public DbSet<SkillEntity>              Skills               => Set<SkillEntity>();
		public DbSet<RagDocumentEntity>        RagDocuments         => Set<RagDocumentEntity>();
		public DbSet<FlatFileSourceEntity>     FlatFileSources      => Set<FlatFileSourceEntity>();
		public DbSet<DatabaseConnectionEntity> DatabaseConnections  => Set<DatabaseConnectionEntity>();

		protected override void OnModelCreating(ModelBuilder builder)
		{
			base.OnModelCreating(builder);

			builder.Entity<AgentMemory>(e =>
			{
				e.HasIndex(m => m.UserId);
				e.HasIndex(m => new { m.UserId, m.IsActive });
				e.Property(m => m.EmbeddingJson).HasColumnType("nvarchar(max)");
			});

			builder.Entity<ChatMessageEntity>(e =>
			{
				e.HasIndex(m => m.UserId);
				e.HasIndex(m => new { m.UserId, m.SessionId, m.Timestamp });
				e.Property(m => m.Content).HasColumnType("nvarchar(max)");
			});

			builder.Entity<WorkflowStep>(e =>
			{
				e.HasOne(s => s.CalledWorkflow)
				 .WithMany()
				 .HasForeignKey(s => s.CalledWorkflowId)
				 .OnDelete(DeleteBehavior.ClientSetNull);
			});

			builder.Entity<ScheduledTask>(e =>
			{
				e.HasIndex(t => t.UserId);
				e.HasIndex(t => new { t.UserId, t.IsEnabled });
				e.Property(t => t.Instructions).HasColumnType("nvarchar(max)");
				e.Property(t => t.LastError).HasColumnType("nvarchar(max)");
				e.HasOne(t => t.WorkflowDefinition)
				 .WithMany()
				 .HasForeignKey(t => t.WorkflowDefinitionId)
				 .OnDelete(DeleteBehavior.SetNull);
			});

			builder.Entity<ScheduledTaskResult>(e =>
			{
				e.HasIndex(r => r.UserId);
				e.HasIndex(r => new { r.UserId, r.IsRead });
				e.HasIndex(r => r.ScheduledTaskId);
				e.Property(r => r.Output).HasColumnType("nvarchar(max)");
				e.HasOne(r => r.ScheduledTask)
				 .WithMany(t => t.Results)
				 .HasForeignKey(r => r.ScheduledTaskId)
				 .OnDelete(DeleteBehavior.Cascade);
			});

			// ── AppSettings (singleton row, Id = 1) ──────────────────────────────
			builder.Entity<AppSettingsEntity>(e =>
			{
				e.Property(s => s.Id).ValueGeneratedNever();
				e.ToTable(t => t.HasCheckConstraint("CK_AppSettings_Singleton", "[Id] = 1"));
				e.Property(s => s.AppName).HasMaxLength(200);
				e.Property(s => s.ModelId).HasMaxLength(200);
				e.Property(s => s.LogoFileName).HasMaxLength(260);
				e.Property(s => s.LogoVersion).HasMaxLength(64);

				e.OwnsOne(s => s.Terminal, t =>
				{
					t.Property(p => p.DefaultShell).HasColumnName("Terminal_DefaultShell").HasMaxLength(50);
					t.Property(p => p.WorkingDirectory).HasColumnName("Terminal_WorkingDirectory").HasMaxLength(500);
					t.Property(p => p.IsEnabled).HasColumnName("Terminal_IsEnabled");
					t.Property(p => p.TimeoutSeconds).HasColumnName("Terminal_TimeoutSeconds");
					t.Property(p => p.MaxOutputLength).HasColumnName("Terminal_MaxOutputLength");
				});

				e.OwnsOne(s => s.AgentMail, a =>
				{
					a.Property(p => p.IsEnabled).HasColumnName("AgentMail_IsEnabled");
					a.Property(p => p.ApiKey).HasColumnName("AgentMail_ApiKey").HasMaxLength(500);
					a.Property(p => p.BaseUrl).HasColumnName("AgentMail_BaseUrl").HasMaxLength(500);
					a.Property(p => p.DefaultInbox).HasColumnName("AgentMail_DefaultInbox").HasMaxLength(200);
				});

				e.OwnsOne(s => s.Security, sc =>
				{
					sc.Property(p => p.MaxFailedLoginAttempts).HasColumnName("Security_MaxFailedLoginAttempts");
					sc.Property(p => p.LoginLockoutHours).HasColumnName("Security_LoginLockoutHours");
				});
			});

			builder.Entity<AppApiKeyEntity>(e =>
			{
				e.Property(k => k.Key).HasMaxLength(256).IsRequired();
				e.HasIndex(k => k.SortOrder);
			});

			builder.Entity<McpServerEntity>(e =>
			{
				e.Property(m => m.Id).HasMaxLength(64);
				e.Property(m => m.Name).HasMaxLength(200);
				e.Property(m => m.TransportType).HasMaxLength(20);
				e.Property(m => m.Url).HasMaxLength(500);
				e.Property(m => m.Command).HasMaxLength(500);
				e.Property(m => m.Arguments).HasMaxLength(2000);
			});

			builder.Entity<SkillEntity>(e =>
			{
				e.Property(s => s.Id).HasMaxLength(64);
				e.Property(s => s.Name).HasMaxLength(200);
				e.Property(s => s.Icon).HasMaxLength(16);
				e.Property(s => s.Description).HasMaxLength(1000);
				e.Property(s => s.Instructions).HasColumnType("nvarchar(max)");
			});

			builder.Entity<RagDocumentEntity>(e =>
			{
				e.Property(r => r.Id).HasMaxLength(64);
				e.Property(r => r.Name).HasMaxLength(200);
				e.Property(r => r.Description).HasMaxLength(1000);
				e.Property(r => r.OriginalFileName).HasMaxLength(260);
			});

			builder.Entity<FlatFileSourceEntity>(e =>
			{
				e.Property(f => f.Id).HasMaxLength(60);
				e.Property(f => f.Label).HasMaxLength(200);
				e.Property(f => f.FilePath).HasMaxLength(500);
				e.Property(f => f.Delimiter).HasMaxLength(10);
				e.Property(f => f.SheetName).HasMaxLength(200);
				e.Property(f => f.Encoding).HasMaxLength(50);
				e.Property(f => f.FixedWidthColumnsJson).HasColumnType("nvarchar(max)");
			});

			builder.Entity<DatabaseConnectionEntity>(e =>
			{
				e.Property(d => d.Id).HasMaxLength(128);
				e.Property(d => d.Label).HasMaxLength(200);
				e.Property(d => d.ConnectionString).HasColumnType("nvarchar(max)");
				e.Property(d => d.AllowedSchemasJson).HasColumnType("nvarchar(max)");
			});
		}
	}
}
