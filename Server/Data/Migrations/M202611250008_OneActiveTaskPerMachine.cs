using FluentMigrator;

namespace Server.Data.Migrations;

[Migration(202611250008, "One active task per render machine")]
public class M202611250008_OneActiveTaskPerMachine : Migration
{
    public override void Up()
    {
        // Existing data may already break the rule: keep each machine's oldest active task and
        // return the rest to the queue.
        Execute.Sql("""
            UPDATE job_tasks t
            JOIN (SELECT render_machine_id, MIN(id) AS keep_id
                  FROM job_tasks
                  WHERE status IN ('Assigned', 'Running') AND render_machine_id IS NOT NULL
                  GROUP BY render_machine_id) k ON t.render_machine_id = k.render_machine_id
            SET t.status = 'Pending', t.render_machine_id = NULL, t.frames_completed = 0,
                t.claimed_at_utc = NULL, t.started_at_utc = NULL
            WHERE t.status IN ('Assigned', 'Running') AND t.id <> k.keep_id
            """);

        Execute.Sql("""
            ALTER TABLE job_tasks
                ADD COLUMN active_machine_id INT
                    AS (CASE WHEN status IN ('Assigned', 'Running') THEN render_machine_id END) VIRTUAL,
                ADD UNIQUE INDEX ux_job_tasks_active_machine (active_machine_id)
            """);
    }

    public override void Down()
    {
        Execute.Sql("ALTER TABLE job_tasks DROP INDEX ux_job_tasks_active_machine, DROP COLUMN active_machine_id");
    }
}
