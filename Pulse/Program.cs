using Pulse.Data;
using Pulse.Hubs;
using Pulse.Infrastructure;
using Pulse.Jobs;
using Pulse.Models;
using Pulse.Services;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// ── QuestPDF community licence ────────────────────────────────────────────────
QuestPDF.Settings.License = LicenseType.Community;

// ── Database ──────────────────────────────────────────────────────────────────
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
	?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

// AddDbContextFactory registers:
//   • IDbContextFactory<ApplicationDbContext> as Singleton  → used by ChatHistoryService
//   • ApplicationDbContext                    as Scoped     → used by Identity / Razor Pages
builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
	options.UseSqlServer(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

// ── Identity ──────────────────────────────────────────────────────────────────
builder.Services.AddDefaultIdentity<ApplicationUser>(options =>
	{
		options.SignIn.RequireConfirmedAccount = true;
		// Lockout thresholds are enforced dynamically in Login.cshtml.cs
		// using live SecuritySettings. Set high defaults here so Identity
		// never auto-locks before our custom logic runs.
		options.Lockout.MaxFailedAccessAttempts = 100;
		options.Lockout.DefaultLockoutTimeSpan  = TimeSpan.FromHours(24);
		options.Lockout.AllowedForNewUsers      = true;
	})
	.AddRoles<IdentityRole>()
	.AddEntityFrameworkStores<ApplicationDbContext>();

// ── MVC + Razor Pages ─────────────────────────────────────────────────────────
builder.Services.AddControllersWithViews();

// ── SignalR ───────────────────────────────────────────────────────────────────
builder.Services.AddSignalR();

// ── Hangfire ──────────────────────────────────────────────────────────────────
builder.Services.AddHangfire(config => config
	.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
	.UseSimpleAssemblyNameTypeSerializer()
	.UseRecommendedSerializerSettings()
	.UseSqlServerStorage(connectionString, new SqlServerStorageOptions
	{
		CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
		SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
		QueuePollInterval = TimeSpan.Zero,
		UseRecommendedIsolationLevel = true,
		DisableGlobalLocks = true,
		SchemaName = "Hangfire"
	}));
builder.Services.AddHangfireServer();

// ── Agent services ────────────────────────────────────────────────────────────
// Gemini LLM — agentic tasks with many tool calls can run for several minutes
builder.Services.AddHttpClient("Gemini")
	.ConfigureHttpClient(c => c.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddHttpClient("GeminiEmbed")
	.ConfigureHttpClient(c => c.Timeout = TimeSpan.FromMinutes(2));
builder.Services.AddHttpClient("MemoryExtract")
	.ConfigureHttpClient(c => c.Timeout = TimeSpan.FromMinutes(2));
builder.Services.AddSingleton<LlmSettingsService>();
builder.Services.AddSingleton<LlmSettingsSeeder>();
builder.Services.AddSingleton<GeminiKeyRotationService>();
builder.Services.AddSingleton<ChatHistoryService>();
builder.Services.AddSingleton<GeminiEmbeddingService>();
builder.Services.AddSingleton<SharpVectorIndexService>();
builder.Services.AddSingleton<RagService>();
builder.Services.AddScoped<MemoryService>();
builder.Services.AddTransient<McpService>();
builder.Services.AddTransient<AgentOrchestrationService>();
builder.Services.AddTransient<AgentTaskJob>();
builder.Services.AddTransient<MemoryExtractionJob>();

// ── Database tools (MCP-style, in-process) ────────────────────────────────────
builder.Services.AddTransient<IDatabaseTools, DatabaseTools>();
builder.Services.AddTransient<DatabaseToolsPlugin>();

// ── PDF generation ────────────────────────────────────────────────────────────
builder.Services.AddSingleton<DownloadTokenStore>();
builder.Services.AddTransient<IPdfService, PdfService>();
builder.Services.AddTransient<PdfGeneratorPlugin>();
builder.Services.AddHttpContextAccessor();

// ── Word (DOCX) generation ────────────────────────────────────────────────────
builder.Services.AddTransient<IWordService, WordService>();
builder.Services.AddTransient<WordGeneratorPlugin>();

// ── Excel (XLSX) generation ───────────────────────────────────────────────────
builder.Services.AddTransient<IExcelService, ExcelService>();
builder.Services.AddTransient<ExcelGeneratorPlugin>();

// ── Terminal (shell execution) ────────────────────────────────────────────────
builder.Services.AddTransient<TerminalPlugin>();

// ── n8n webhook client ────────────────────────────────────────────────────────
builder.Services.AddHttpClient("N8n")
	.ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));

// ── AgentMail (email) ─────────────────────────────────────────────────────────
builder.Services.AddHttpClient("AgentMail")
	.ConfigureHttpClient(c => c.Timeout = TimeSpan.FromMinutes(2));
builder.Services.AddTransient<AgentMailService>();
builder.Services.AddTransient<AgentMailPlugin>();

// ── File extraction ───────────────────────────────────────────────────────────
builder.Services.AddTransient<FileExtractionService>();

// ── Flat-file data sources ────────────────────────────────────────────────────
builder.Services.AddSingleton<FlatFileDataService>();
builder.Services.AddTransient<FlatFileDataPlugin>();

// ── Scheduler ─────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<SchedulerService>();
builder.Services.AddTransient<ScheduledAgentRunner>();
builder.Services.AddTransient<ScheduledTaskJob>();

// -- Workflows
builder.Services.AddTransient<WorkflowService>();
builder.Services.AddTransient<WorkflowRunner>();
builder.Services.AddTransient<WorkflowJob>();

// ChartGeneratorPlugin is instantiated per-execution inside AgentOrchestrationService
// (needs the live sessionId and IHubContext — not suitable for DI registration)

// ─────────────────────────────────────────────────────────────────────────────
var app = builder.Build();

// ── Auto-migrate and re-register scheduled jobs ───────────────────────────────
using (var startupScope = app.Services.CreateScope())
{
	var db = startupScope.ServiceProvider
		.GetRequiredService<IDbContextFactory<Pulse.Data.ApplicationDbContext>>();
	await using var ctx = await db.CreateDbContextAsync();
	await ctx.Database.MigrateAsync();

	// ── Migrate llm-settings.json → DB (one-time) ─────────────────────────────
	await startupScope.ServiceProvider
		.GetRequiredService<LlmSettingsSeeder>()
		.SeedAsync();
	await startupScope.ServiceProvider
		.GetRequiredService<LlmSettingsService>()
		.ReloadAsync();

	// ── Seed roles + default admin user ───────────────────────────────────────
	var roleManager = startupScope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
	var userManager = startupScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

	foreach (var role in new[] { "Admin", "User" })
	{
		if (!await roleManager.RoleExistsAsync(role))
			await roleManager.CreateAsync(new IdentityRole(role));
	}

	var adminEmail    = app.Configuration["Seed:AdminEmail"]    ?? "admin@pulse.local";
	var adminPassword = app.Configuration["Seed:AdminPassword"] ?? "Admin@123!";
	var adminName     = app.Configuration["Seed:AdminFullName"] ?? "System Admin";

	if (await userManager.FindByEmailAsync(adminEmail) is null)
	{
		var admin = new ApplicationUser
		{
			UserName       = adminEmail,
			Email          = adminEmail,
			FirstName      = adminName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty,
			LastName       = adminName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault() ?? string.Empty,
			EmailConfirmed = true
		};
		var result = await userManager.CreateAsync(admin, adminPassword);
		if (result.Succeeded)
			await userManager.AddToRoleAsync(admin, "Admin");
	}

	// Re-register all enabled recurring tasks with Hangfire after restart
	var scheduler = startupScope.ServiceProvider.GetRequiredService<Pulse.Services.SchedulerService>();
	var tasks = await ctx.ScheduledTasks
		.Where(t => t.IsEnabled && t.FrequencyType != Pulse.Models.FrequencyType.OneTime)
		.ToListAsync();
	foreach (var t in tasks)
		scheduler.RegisterHangfireJob(t);
}

// ── HTTP pipeline ─────────────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
	app.UseMigrationsEndPoint();
}
else
{
	app.UseExceptionHandler("/Home/Error");
	app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthorization();

// Hangfire dashboard (authenticated users only)
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
	Authorization = [new HangfireAuthorizationFilter()],
	AppPath = "/"
});

app.MapStaticAssets();

// SignalR hub
app.MapHub<AgentHub>("/agentHub");

app.MapControllerRoute(
	name: "default",
	pattern: "{controller=Home}/{action=Index}/{id?}")
	.WithStaticAssets();

app.MapRazorPages()
   .WithStaticAssets();

app.Run();
