using FluentMigrator;

namespace Server.Data.Migrations;

// The .blend scan now runs after the upload request, so the sub task count chosen on upload is stored.
[Migration(202611250013, "Add jobs.requested_task_count")]
public class M202611250013_AddRequestedTaskCount : Migration
{
    public override void Up()
    {
        Alter.Table("jobs").AddColumn("requested_task_count").AsInt32().NotNullable().WithDefaultValue(10);
    }

    public override void Down()
    {
        Delete.Column("requested_task_count").FromTable("jobs");
    }
}
