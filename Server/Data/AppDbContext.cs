using Microsoft.EntityFrameworkCore;
using Server.Data.Entities;

namespace Server.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Job> Jobs => Set<Job>();

    public DbSet<JobTask> JobTasks => Set<JobTask>();

    public DbSet<User> Users => Set<User>();

    public DbSet<RenderMachine> RenderMachines => Set<RenderMachine>();
}
