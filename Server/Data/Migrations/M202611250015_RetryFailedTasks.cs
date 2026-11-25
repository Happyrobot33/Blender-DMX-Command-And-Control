using FluentMigrator;

namespace Server.Data.Migrations;

// Failed tasks no longer fail their job: they go back to Pending and are retried, with failures counted.
// Existing Failed tasks are requeued with one recorded failure, and jobs that had failed only because of
// a task are reopened. Jobs that failed during upload or analysis (no failed tasks) are left alone.
[Migration(202611250015, "Retry failed tasks instead of failing the job")]
public class M202611250015_RetryFailedTasks : Migration
{
    public override void Up()
    {
        Alter.Table("job_tasks")
            .AddColumn("fail_count").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("last_failed_at_utc").AsCustom("DATETIME(6)").Nullable();

        Execute.Sql("""
            UPDATE job_tasks
            SET status = 'Pending', render_machine_id = NULL, frames_completed = 0,
                claimed_at_utc = NULL, started_at_utc = NULL, completed_at_utc = NULL,
                fail_count = 1, last_failed_at_utc = updated_at_utc
            WHERE status = 'Failed'
            """);

        Execute.Sql("""
            UPDATE jobs j
            SET j.status = CASE WHEN EXISTS (SELECT 1 FROM job_tasks t WHERE t.job_id = j.id AND t.status <> 'Pending')
                                THEN 'Rendering' ELSE 'Ready' END,
                j.error_message = NULL
            WHERE j.status = 'Failed'
              AND EXISTS (SELECT 1 FROM job_tasks t WHERE t.job_id = j.id AND t.fail_count > 0)
            """);
    }

    public override void Down()
    {
        Delete.Column("fail_count").Column("last_failed_at_utc").FromTable("job_tasks");
    }
}
