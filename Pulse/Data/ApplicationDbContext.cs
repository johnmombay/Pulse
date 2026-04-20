using Pulse.Data.Entities;
using Pulse.Infrastructure;
using Pulse.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Data
{
	public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
	{
		private readonly ITenantContext? _tenantContext;

		public ApplicationDbContext(
			DbContextOptions<ApplicationDbContext> options,
			ITenantContext? tenantContext = null)
			: base(options)
		{
			_tenantContext = tenantContext;
		}

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

		// ── Multi-tenancy ─────────────────────────────────────────────────────────
		public DbSet<Tenant>                   Tenants              => Set<Tenant>();
		public DbSet<GlobalAgentMailSettings>  GlobalAgentMailSettings => Set<GlobalAgentMailSettings>();

		protected override void OnModelCreating(ModelBuilder builder)
		{
			base.OnModelCreating(builder);

			builder.Entity<AgentMemory>(e =>
			{
				e.HasIndex(m => m.TenantId);
				e.HasIndex(m => m.UserId);
				e.HasIndex(m => new { m.UserId, m.IsActive });
				e.Property(m => m.EmbeddingJson).HasColumnType("nvarchar(max)");
			});

			builder.Entity<ChatMessageEntity>(e =>
			{
				e.HasIndex(m => m.TenantId);
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
				e.HasIndex(t => t.TenantId);
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
				e.HasIndex(r => r.TenantId);
				e.HasIndex(r => r.UserId);
				e.HasIndex(r => new { r.UserId, r.IsRead });
				e.HasIndex(r => r.ScheduledTaskId);
				e.Property(r => r.Output).HasColumnType("nvarchar(max)");
				e.HasOne(r => r.ScheduledTask)
				 .WithMany(t => t.Results)
				 .HasForeignKey(r => r.ScheduledTaskId)
				 .OnDelete(DeleteBehavior.Cascade);
			});

			// ── AppSettings (one row per tenant) ────────────────────────────────
			builder.Entity<AppSettingsEntity>(e =>
			{
				e.Property(s => s.Id).ValueGeneratedNever();
				e.HasIndex(a => a.TenantId).IsUnique();
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
				e.HasIndex(m => m.TenantId);
				e.Property(m => m.Id).HasMaxLength(64);
				e.Property(m => m.Name).HasMaxLength(200);
				e.Property(m => m.TransportType).HasMaxLength(20);
				e.Property(m => m.Url).HasMaxLength(500);
				e.Property(m => m.Command).HasMaxLength(500);
				e.Property(m => m.Arguments).HasMaxLength(2000);
			});

			builder.Entity<SkillEntity>(e =>
			{
				e.HasIndex(s => s.TenantId);
				e.Property(s => s.Id).HasMaxLength(64);
				e.Property(s => s.Name).HasMaxLength(200);
				e.Property(s => s.Icon).HasMaxLength(16);
				e.Property(s => s.Description).HasMaxLength(1000);
				e.Property(s => s.Instructions).HasColumnType("nvarchar(max)");
			});

			builder.Entity<RagDocumentEntity>(e =>
			{
				e.HasIndex(r => r.TenantId);
				e.Property(r => r.Id).HasMaxLength(64);
				e.Property(r => r.Name).HasMaxLength(200);
				e.Property(r => r.Description).HasMaxLength(1000);
				e.Property(r => r.OriginalFileName).HasMaxLength(260);
			});

			builder.Entity<FlatFileSourceEntity>(e =>
			{
				e.HasIndex(f => f.TenantId);
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
				e.HasIndex(d => d.TenantId);
				e.Property(d => d.Id).HasMaxLength(128);
				e.Property(d => d.Label).HasMaxLength(200);
				e.Property(d => d.ConnectionString).HasColumnType("nvarchar(max)");
				e.Property(d => d.AllowedSchemasJson).HasColumnType("nvarchar(max)");
			});

			builder.Entity<WorkflowDefinition>(e =>
			{
				e.HasIndex(w => w.TenantId);
			});

			builder.Entity<WorkflowRun>(e =>
			{
				e.HasIndex(w => w.TenantId);
			});

			// ── Multi-tenancy ─────────────────────────────────────────────────────
			builder.Entity<ApplicationUser>(e =>
			{
				e.HasOne(u => u.Tenant)
				 .WithMany(t => t.Users)
				 .HasForeignKey(u => u.TenantId)
				 .OnDelete(DeleteBehavior.Restrict)  // Don't cascade-delete users when tenant is deleted — admin should clean up users first
				 .IsRequired(false);

				e.HasIndex(u => u.TenantId);
			});

			builder.Entity<Tenant>(e =>
			{
				e.Property(t => t.Id).ValueGeneratedNever();
				e.Property(t => t.Name).HasMaxLength(200).IsRequired();
				e.Property(t => t.Slug).HasMaxLength(100).IsRequired();
				e.HasIndex(t => t.Slug).IsUnique();
				e.HasIndex(t => t.Name).IsUnique();
			});

			builder.Entity<GlobalAgentMailSettings>(e =>
			{
				e.Property(s => s.Id).ValueGeneratedNever();
				e.ToTable(t => t.HasCheckConstraint("CK_GlobalAgentMailSettings_Singleton", "[Id] = 1"));
				e.Property(s => s.ApiBaseUrl).HasMaxLength(500);
				e.Property(s => s.ApiKey).HasMaxLength(500);
				e.Property(s => s.FromAddress).HasMaxLength(200);
				e.Property(s => s.FromName).HasMaxLength(200);
				e.Property(s => s.DefaultInbox).HasMaxLength(200);
			});

			// ── Multi-tenancy global query filters ───────────────────────────────────────
			// Filter is bypassed for SuperAdmin (TenantId == null) so they can see all tenants.
			// Background jobs set _tenantContext via SetTenantId() before querying.
			builder.Entity<AppSettingsEntity>()
				.HasQueryFilter(e => _tenantContext == null
								   || _tenantContext.TenantId == null
								   || e.TenantId == _tenantContext.TenantId);

			builder.Entity<McpServerEntity>()
				.HasQueryFilter(e => _tenantContext == null
								   || _tenantContext.TenantId == null
								   || e.TenantId == _tenantContext.TenantId);

			builder.Entity<SkillEntity>()
				.HasQueryFilter(e => _tenantContext == null
								   || _tenantContext.TenantId == null
								   || e.TenantId == _tenantContext.TenantId);

			builder.Entity<RagDocumentEntity>()
				.HasQueryFilter(e => _tenantContext == null
								   || _tenantContext.TenantId == null
								   || e.TenantId == _tenantContext.TenantId);

			builder.Entity<FlatFileSourceEntity>()
				.HasQueryFilter(e => _tenantContext == null
								   || _tenantContext.TenantId == null
								   || e.TenantId == _tenantContext.TenantId);

			builder.Entity<DatabaseConnectionEntity>()
				.HasQueryFilter(e => _tenantContext == null
								   || _tenantContext.TenantId == null
								   || e.TenantId == _tenantContext.TenantId);

			builder.Entity<AgentMemory>()
				.HasQueryFilter(e => _tenantContext == null
								   || _tenantContext.TenantId == null
								   || e.TenantId == _tenantContext.TenantId);

			builder.Entity<ChatMessageEntity>()
				.HasQueryFilter(e => _tenantContext == null
								   || _tenantContext.TenantId == null
								   || e.TenantId == _tenantContext.TenantId);

			builder.Entity<ScheduledTask>()
				.HasQueryFilter(e => _tenantContext == null
								   || _tenantContext.TenantId == null
								   || e.TenantId == _tenantContext.TenantId);

			builder.Entity<ScheduledTaskResult>()
				.HasQueryFilter(e => _tenantContext == null
								   || _tenantContext.TenantId == null
								   || e.TenantId == _tenantContext.TenantId);

			builder.Entity<WorkflowDefinition>()
				.HasQueryFilter(e => _tenantContext == null
								   || _tenantContext.TenantId == null
								   || e.TenantId == _tenantContext.TenantId);

			builder.Entity<WorkflowRun>()
				.HasQueryFilter(e => _tenantContext == null
								   || _tenantContext.TenantId == null
								   || e.TenantId == _tenantContext.TenantId);
		}

		public override int SaveChanges()
		{
			var tenantId = _tenantContext?.TenantId;

			if (tenantId.HasValue)
			{
				foreach (var entry in ChangeTracker.Entries<ITenantOwned>()
					.Where(e => e.State == EntityState.Added && e.Entity.TenantId == Guid.Empty))
				{
					entry.Entity.TenantId = tenantId.Value;
				}
			}

			return base.SaveChanges();
		}

		public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
		{
			var tenantId = _tenantContext?.TenantId;

			if (tenantId.HasValue)
			{
				foreach (var entry in ChangeTracker.Entries<ITenantOwned>()
					.Where(e => e.State == EntityState.Added && e.Entity.TenantId == Guid.Empty))
				{
					entry.Entity.TenantId = tenantId.Value;
				}
			}

			return base.SaveChangesAsync(cancellationToken);
		}
	}
}
