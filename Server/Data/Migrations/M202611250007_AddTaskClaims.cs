using FluentMigrator;

namespace Server.Data.Migrations;

[Migration(202611250007, "Add job_tasks.claimed_at_utc and status index for task claiming")]
public class M202611250007_AddTaskClaims : Migration
{
    public override void Up()
    {
        Alter.Table("job_tasks")
            .AddColumn("claimed_at_utc").AsCustom("DATETIME(6)").Nullable();

        // The claim query looks for Pending tasks.
        Create.Index("ix_job_tasks_status").OnTable("job_tasks").OnColumn("status");
    }

    public override void Down()
    {
        Delete.Index("ix_job_tasks_status").OnTable("job_tasks");
        Delete.Column("claimed_at_utc").FromTable("job_tasks");
    }
}
