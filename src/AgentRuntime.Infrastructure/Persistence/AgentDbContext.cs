using Microsoft.EntityFrameworkCore;

namespace AgentRuntime.Infrastructure.Persistence;

public sealed class AgentDbContext(DbContextOptions<AgentDbContext> options) : DbContext(options)
{
    public DbSet<TaskRecord> Tasks => Set<TaskRecord>();
    public DbSet<AgentRecord> Agents => Set<AgentRecord>();
    public DbSet<MessageRecord> Messages => Set<MessageRecord>();
    public DbSet<EventRecord> Events => Set<EventRecord>();
    public DbSet<ArtifactRecord> Artifacts => Set<ArtifactRecord>();
    public DbSet<MemoryEntity> MemoryEntries => Set<MemoryEntity>();
    public DbSet<ToolCallRecord> ToolCalls => Set<ToolCallRecord>();
    public DbSet<WorldRecord> Worlds => Set<WorldRecord>();
    public DbSet<WorkspaceRecord> Workspaces => Set<WorkspaceRecord>();
    public DbSet<SecretRecord> Secrets => Set<SecretRecord>();
    public DbSet<AuditRecord> AuditEntries => Set<AuditRecord>();
    public DbSet<AuditHeadRecord> AuditHeads => Set<AuditHeadRecord>();
    public DbSet<TenantRecord> Tenants => Set<TenantRecord>();
    public DbSet<UserRecord> Users => Set<UserRecord>();
    public DbSet<MembershipRecord> Memberships => Set<MembershipRecord>();
    public DbSet<SessionRecord> Sessions => Set<SessionRecord>();
    public DbSet<ApiKeyRecord> ApiKeys => Set<ApiKeyRecord>();
    public DbSet<InvitationRecord> Invitations => Set<InvitationRecord>();
    public DbSet<BillingEventRecord> BillingEvents => Set<BillingEventRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Rows written before multi-tenancy belong to the default organization.
        foreach (var type in new[] { typeof(TaskRecord), typeof(AgentRecord), typeof(MessageRecord), typeof(EventRecord), typeof(ArtifactRecord),
                     typeof(MemoryEntity), typeof(ToolCallRecord), typeof(WorldRecord), typeof(WorkspaceRecord) })
        {
            modelBuilder.Entity(type).Property<string>("TenantId").HasDefaultValue("default");
        }

        modelBuilder.Entity<TaskRecord>(b =>
        {
            b.HasKey(t => t.TaskId);
            b.HasIndex(t => new { t.TenantId, t.CreatedAt });
        });

        modelBuilder.Entity<AgentRecord>(b =>
        {
            b.HasKey(a => a.AgentId);
            b.HasIndex(a => a.ParentAgentId);
            b.HasIndex(a => a.RootAgentId);
            b.HasIndex(a => a.TaskId);
            b.HasIndex(a => a.Status);
            b.HasIndex(a => a.TenantId);
        });

        modelBuilder.Entity<MessageRecord>(b =>
        {
            b.HasKey(m => m.MessageId);
            b.HasIndex(m => m.ConversationId);
            b.HasIndex(m => new { m.FromAgentId, m.ToAgentId });
            b.HasIndex(m => m.TaskId);
        });

        modelBuilder.Entity<EventRecord>(b =>
        {
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).ValueGeneratedOnAdd();
            b.HasIndex(e => e.TaskId);
            b.HasIndex(e => e.AgentId);
            b.HasIndex(e => e.Timestamp);
            b.HasIndex(e => new { e.TenantId, e.Timestamp });
        });

        modelBuilder.Entity<ArtifactRecord>(b =>
        {
            b.HasKey(a => a.ArtifactId);
            b.HasIndex(a => a.TaskId);
        });

        modelBuilder.Entity<MemoryEntity>(b =>
        {
            b.HasKey(m => m.MemoryId);
            b.HasIndex(m => new { m.TenantId, m.AgentId, m.Key });
            b.HasIndex(m => m.Kind);
        });

        modelBuilder.Entity<ToolCallRecord>(b =>
        {
            b.HasKey(t => t.Id);
            b.Property(t => t.Id).ValueGeneratedOnAdd();
            b.HasIndex(t => t.AgentId);
        });

        modelBuilder.Entity<SecretRecord>(b =>
        {
            b.HasKey(s => new { s.Scope, s.Key });
        });

        modelBuilder.Entity<AuditRecord>(b =>
        {
            b.HasKey(a => a.Id);
            b.Property(a => a.Id).ValueGeneratedOnAdd();
            b.HasIndex(a => a.Key).IsUnique();
            b.HasIndex(a => new { a.Scope, a.Seq }).IsUnique();
            b.HasIndex(a => new { a.Scope, a.ActorId });
        });

        modelBuilder.Entity<TenantRecord>(b =>
        {
            b.HasKey(t => t.TenantId);
            b.HasIndex(t => t.StripeCustomerId);
        });

        modelBuilder.Entity<UserRecord>(b =>
        {
            b.HasKey(u => u.UserId);
            b.HasIndex(u => u.Email).IsUnique();
        });

        modelBuilder.Entity<MembershipRecord>(b =>
        {
            b.HasKey(m => new { m.TenantId, m.UserId });
            b.HasIndex(m => m.UserId);
        });

        modelBuilder.Entity<SessionRecord>(b =>
        {
            b.HasKey(x => x.TokenHash);
            b.HasIndex(x => x.UserId);
        });

        modelBuilder.Entity<ApiKeyRecord>(b =>
        {
            b.HasKey(k => k.KeyId);
            b.HasIndex(k => k.TenantId);
        });

        modelBuilder.Entity<InvitationRecord>(b =>
        {
            b.HasKey(i => i.InvitationId);
            b.HasIndex(i => i.TokenHash).IsUnique();
            b.HasIndex(i => i.TenantId);
        });

        modelBuilder.Entity<BillingEventRecord>(b => b.HasKey(e => e.EventId));

        modelBuilder.Entity<AuditHeadRecord>(b =>
        {
            b.HasKey(h => h.Scope);
        });

        modelBuilder.Entity<WorkspaceRecord>(b =>
        {
            b.HasKey(w => w.WorkspaceId);
            b.HasIndex(w => new { w.TenantId, w.CreatedAt });
        });

        modelBuilder.Entity<WorldRecord>(b =>
        {
            b.HasKey(w => w.WorldId);
            b.HasIndex(w => new { w.TenantId, w.CreatedAt });
        });
    }
}
