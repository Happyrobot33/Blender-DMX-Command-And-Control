using FluentMigrator;

namespace Server.Data.Migrations;

[Migration(202611250005, "Add progress tracking to job_tasks and render completion to jobs")]
public class M202611250005_AddTaskProgress : Migration
{
    public override void Up()
    {
        Alter.Table("job_tasks")
            .AddColumn("frames_completed").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("error_message").AsString(2000).Nullable()
            .AddColumn("started_at_utc").AsCustom("DATETIME(6)").Nullable()
            .AddColumn("completed_at_utc").AsCustom("DATETIME(6)").Nullable()
            .AddColumn("updated_at_utc").AsCustom("DATETIME(6)").Nullable();

        // jobs.completed_at_utc records when the upload finished; this records when rendering did.
        Alter.Table("jobs")
            .AddColumn("rendered_at_utc").AsCustom("DATETIME(6)").Nullable();
    }

    public override void Down()
    {
        Delete.Column("rendered_at_utc").FromTable("jobs");
        Delete.Column("frames_completed").Column("error_message").Column("started_at_utc")
            .Column("completed_at_utc").Column("updated_at_utc").FromTable("job_tasks");
    }
}
