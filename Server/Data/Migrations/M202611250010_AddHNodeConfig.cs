using FluentMigrator;

namespace Server.Data.Migrations;

[Migration(202611250010, "Add jobs.config_file_name (HNode config)")]
public class M202611250010_AddHNodeConfig : Migration
{
    public override void Up()
    {
        // Required for every new upload (enforced by the upload service). Nullable only because jobs
        // uploaded before this migration have no config.
        Alter.Table("jobs").AddColumn("config_file_name").AsString(255).Nullable();
    }

    public override void Down()
    {
        Delete.Column("config_file_name").FromTable("jobs");
    }
}
