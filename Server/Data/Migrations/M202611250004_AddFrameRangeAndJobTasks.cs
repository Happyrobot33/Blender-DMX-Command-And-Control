using FluentMigrator;

namespace Server.Data.Migrations;

[Migration(202611250004, "Add job frame range and job_tasks table")]
public class M202611250004_AddFrameRangeAndJobTasks : Migration
{
    public override void Up()
    {
        // Null until the main .blend file has been scanned.
        Alter.Table("jobs")
            .AddColumn("scene_name").AsString(255).Nullable()
            .AddColumn("frame_start").AsInt32().Nullable()
            .AddColumn("frame_end").AsInt32().Nullable()
            .AddColumn("frame_step").AsInt32().Nullable()
            .AddColumn("analyzed_at_utc").AsCustom("DATETIME(6)").Nullable();

        Create.Table("job_tasks")
            .WithColumn("id").AsInt64().NotNullable().PrimaryKey("pk_job_tasks").Identity()
            .WithColumn("job_id").AsFixedLengthAnsiString(36).NotNullable()
                .ForeignKey("fk_job_tasks_jobs", "jobs", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("task_index").AsInt32().NotNullable()
            .WithColumn("frame_start").AsInt32().NotNullable()
            .WithColumn("frame_end").AsInt32().NotNullable()
            .WithColumn("frame_count").AsInt32().NotNullable()
            .WithColumn("status").AsAnsiString(32).NotNullable()
            .WithColumn("created_at_utc").AsCustom("DATETIME(6)").NotNullable();

        Create.Index("ux_job_tasks_job_id_task_index").OnTable("job_tasks")
            .OnColumn("job_id").Ascending()
            .OnColumn("task_index").Ascending()
            .WithOptions().Unique();
    }

    public override void Down()
    {
        Delete.Table("job_tasks");
        Delete.Column("scene_name").Column("frame_start").Column("frame_end").Column("frame_step").Column("analyzed_at_utc")
            .FromTable("jobs");
    }
}
