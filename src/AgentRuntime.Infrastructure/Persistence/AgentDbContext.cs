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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TaskRecord>(b =>
        {
            b.HasKey(t => t.TaskId);
        });

        modelBuilder.Entity<AgentRecord>(b =>
        {
            b.HasKey(a => a.AgentId);
            b.HasIndex(a => a.ParentAgentId);
            b.HasIndex(a => a.RootAgentId);
            b.HasIndex(a => a.TaskId);
            b.HasIndex(a => a.Status);
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
        });

        modelBuilder.Entity<ArtifactRecord>(b =>
        {
            b.HasKey(a => a.ArtifactId);
            b.HasIndex(a => a.TaskId);
        });

        modelBuilder.Entity<MemoryEntity>(b =>
        {
            b.HasKey(m => m.MemoryId);
            b.HasIndex(m => new { m.AgentId, m.Key });
            b.HasIndex(m => m.Kind);
        });

        modelBuilder.Entity<ToolCallRecord>(b =>
        {
            b.HasKey(t => t.Id);
            b.Property(t => t.Id).ValueGeneratedOnAdd();
            b.HasIndex(t => t.AgentId);
        });
    }
}
