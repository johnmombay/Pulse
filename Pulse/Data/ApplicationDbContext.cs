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
		public DbSet<AgentDefinitionEntity>    AgentDefinitions     => Set<AgentDefinitionEntity>();

		// ── Multi-tenancy ─────────────────────────────────────────────────────────
		public DbSet<Tenant>                   Tenants              => Set<Tenant>();
		public DbSet<SubscriptionPlan>         SubscriptionPlans    => Set<SubscriptionPlan>();
		public DbSet<TenantSubscription>       TenantSubscriptions  => Set<TenantSubscription>();
		public DbSet<GlobalAgentMailSettings>  GlobalAgentMailSettings => Set<GlobalAgentMailSettings>();
		public DbSet<GlobalLlmSettings>        GlobalLlmSettings    => Set<GlobalLlmSettings>();
		public DbSet<LlmUsageEntity>           LlmUsage                 => Set<LlmUsageEntity>();
		public DbSet<PaymentGatewaySettings>   PaymentGatewaySettings   => Set<PaymentGatewaySettings>();
		public DbSet<Invoice>                  Invoices                 => Set<Invoice>();
		public DbSet<InAppNotification>        InAppNotifications       => Set<InAppNotification>();
		public DbSet<UserTelegramSettings>     UserTelegramSettings     => Set<UserTelegramSettings>();

		protected override void OnModelCreating(ModelBuilder builder)
		{
			base.OnModelCreating(builder);

			builder.Entity<AgentMemory>(e =>
			{
				e.HasIndex(m => m.TenantId);
				e.HasIndex(m => m.UserId);
				e.HasIndex(m => new { m.UserId, m.IsActive });
				e.HasIndex(m => new { m.UserId, m.AgentDefinitionId, m.IsActive });
				e.Property(m => m.AgentDefinitionId).HasMaxLength(64);
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
				e.Property(m => m.ApiKey).HasMaxLength(500);
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

			builder.Entity<AgentDefinitionEntity>(e =>
			{
				e.HasIndex(a => a.TenantId);
				e.Property(a => a.Id).HasMaxLength(64);
				e.Property(a => a.Name).HasMaxLength(200);
				e.Property(a => a.Icon).HasMaxLength(16);
				e.Property(a => a.Description).HasMaxLength(1000);
				e.Property(a => a.ModelId).HasMaxLength(200);
				e.Property(a => a.SystemPrompt).HasColumnType("nvarchar(max)");
				e.Property(a => a.AllowedPluginKeysJson).HasColumnType("nvarchar(max)");
				e.Property(a => a.AllowedSkillIdsJson).HasColumnType("nvarchar(max)");
				e.Property(a => a.AllowedMcpServerIdsJson).HasColumnType("nvarchar(max)");
				e.Property(a => a.AllowedDatabaseKeysJson).HasColumnType("nvarchar(max)");
				e.Property(a => a.AllowedFlatFileIdsJson).HasColumnType("nvarchar(max)");
				e.Property(a => a.AllowedRagDocumentIdsJson).HasColumnType("nvarchar(max)");

				// At most one orchestrator per tenant. Filtered unique index is a
				// SQL Server feature — it only indexes rows where IsOrchestrator = 1,
				// so non-orchestrator rows don't collide.
				e.HasIndex(a => new { a.TenantId, a.IsOrchestrator })
				 .IsUnique()
				 .HasFilter("[IsOrchestrator] = 1");
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

			builder.Entity<SubscriptionPlan>(e =>
			{
				e.Property(p => p.Name).HasMaxLength(100).IsRequired();
				e.Property(p => p.Description).HasMaxLength(500);
				e.Property(p => p.MonthlyPrice).HasColumnType("decimal(18,2)");
				e.Property(p => p.AnnualPrice).HasColumnType("decimal(18,2)");
				e.Property(p => p.OveragePricePerUser).HasColumnType("decimal(18,4)");
				e.Property(p => p.OveragePricePerDatabase).HasColumnType("decimal(18,4)");
				e.Property(p => p.OveragePricePerAgent).HasColumnType("decimal(18,4)");
				e.Property(p => p.OveragePricePerUsageUnit).HasColumnType("decimal(18,6)");
			});

			builder.Entity<TenantSubscription>(e =>
			{
				e.Property(s => s.BillingCycle).HasConversion<string>().HasMaxLength(20);
				e.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
				e.HasIndex(s => s.TenantId);
				e.HasOne(s => s.Tenant)
				 .WithMany()
				 .HasForeignKey(s => s.TenantId)
				 .OnDelete(DeleteBehavior.Cascade);
				e.HasOne(s => s.SubscriptionPlan)
				 .WithMany(p => p.TenantSubscriptions)
				 .HasForeignKey(s => s.SubscriptionPlanId)
				 .OnDelete(DeleteBehavior.Restrict);
				e.HasMany(s => s.Invoices)
				 .WithOne(i => i.TenantSubscription)
				 .HasForeignKey(i => i.TenantSubscriptionId)
				 .OnDelete(DeleteBehavior.Restrict);
			});

			builder.Entity<Invoice>(e =>
			{
				e.HasIndex(i => i.TenantId);
				e.HasIndex(i => i.Status);
				e.HasIndex(i => new { i.TenantId, i.Status });
				e.Property(i => i.Number).HasMaxLength(50).IsRequired();
				e.Property(i => i.Amount).HasColumnType("decimal(18,2)");
				e.Property(i => i.Status).HasConversion<string>().HasMaxLength(20);
				e.Property(i => i.BillingCycle).HasConversion<string>().HasMaxLength(20);
				e.Property(i => i.PaymentReference).HasMaxLength(500);
				e.HasOne(i => i.Tenant)
				 .WithMany()
				 .HasForeignKey(i => i.TenantId)
				 .OnDelete(DeleteBehavior.Cascade);
			});

			builder.Entity<InAppNotification>(e =>
			{
				e.HasIndex(n => n.UserId);
				e.HasIndex(n => new { n.UserId, n.IsRead });
				e.Property(n => n.UserId).HasMaxLength(450).IsRequired();
				e.Property(n => n.Title).HasMaxLength(200).IsRequired();
				e.Property(n => n.Body).HasMaxLength(2000).IsRequired();
				e.Property(n => n.ActionUrl).HasMaxLength(500);
				e.HasOne(n => n.User)
				 .WithMany()
				 .HasForeignKey(n => n.UserId)
				 .OnDelete(DeleteBehavior.Cascade);
			});

			builder.Entity<UserTelegramSettings>(e =>
			{
				e.HasIndex(t => t.UserId).IsUnique();
				e.Property(t => t.BotToken).HasMaxLength(200);
				e.Property(t => t.PairCode).HasMaxLength(32);
				e.HasOne(t => t.User)
				 .WithMany()
				 .HasForeignKey(t => t.UserId)
				 .OnDelete(DeleteBehavior.Cascade);
			});

			builder.Entity<GlobalAgentMailSettings>(e =>
			{
				e.Property(s => s.Id).ValueGeneratedNever();
				e.ToTable(t => t.HasCheckConstraint("CK_GlobalAgentMailSettings_Singleton", "[Id] = 1"));
				e.Property(s => s.ApiKey).HasMaxLength(500);
				e.Property(s => s.FromAddress).HasMaxLength(200);
				e.Property(s => s.FromName).HasMaxLength(200);
				e.Property(s => s.DefaultInbox).HasMaxLength(200);
			});

			builder.Entity<GlobalLlmSettings>(e =>
			{
				e.Property(s => s.Id).ValueGeneratedNever();
				e.ToTable(t => t.HasCheckConstraint("CK_GlobalLlmSettings_Singleton", "[Id] = 1"));
				e.Property(s => s.ModelId).HasMaxLength(200);
			});

			builder.Entity<LlmUsageEntity>(e =>
			{
				e.HasIndex(u => u.TenantId);
				e.HasIndex(u => new { u.TenantId, u.CreatedUtc });
				e.HasIndex(u => new { u.TenantId, u.UserId, u.CreatedUtc });
				e.Property(u => u.UserId).HasMaxLength(450);
				e.Property(u => u.AgentName).HasMaxLength(200);
				e.Property(u => u.ModelId).HasMaxLength(200);
			});

			builder.Entity<PaymentGatewaySettings>(e =>
			{
				e.HasIndex(p => p.IsActive);
				e.Property(p => p.Provider).HasConversion<string>().HasMaxLength(20);
				e.Property(p => p.ApiKey).HasMaxLength(500);
				e.Property(p => p.SecretKey).HasMaxLength(500);
				e.Property(p => p.WebhookSecret).HasMaxLength(500);
				e.Property(p => p.ClientId).HasMaxLength(500);
				e.Property(p => p.ClientSecret).HasMaxLength(500);
				e.Property(p => p.MerchantCode).HasMaxLength(200);

				// Seed one row per provider - all inactive by default
				e.HasData(
					new PaymentGatewaySettings { Id = 1, Provider = PaymentGatewayProvider.HitPay,   IsActive = false, IsTestMode = true, UpdatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
					new PaymentGatewaySettings { Id = 2, Provider = PaymentGatewayProvider.PayMongo,  IsActive = false, IsTestMode = true, UpdatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
					new PaymentGatewaySettings { Id = 3, Provider = PaymentGatewayProvider.DragonPay, IsActive = false, IsTestMode = true, UpdatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
					new PaymentGatewaySettings { Id = 4, Provider = PaymentGatewayProvider.Stripe,    IsActive = false, IsTestMode = true, UpdatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
					new PaymentGatewaySettings { Id = 5, Provider = PaymentGatewayProvider.PayPal,    IsActive = false, IsTestMode = true, UpdatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) }
				);
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

			builder.Entity<AgentDefinitionEntity>()
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

			builder.Entity<LlmUsageEntity>()
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
