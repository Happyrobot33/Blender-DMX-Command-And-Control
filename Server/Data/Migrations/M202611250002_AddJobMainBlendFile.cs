using FluentMigrator;

namespace Server.Data.Migrations;

[Migration(202611250002, "Add jobs.main_blend_file (required)")]
public class M202611250002_AddJobMainBlendFile : Migration
{
    public override void Up()
    {
        Alter.Table("jobs")
            .AddColumn("main_blend_file").AsString(1024).Nullable();

        // Backfill existing jobs with their first uploaded .blend file. Jobs that have none
        // (e.g. failed uploads) get an empty string so the column can be made NOT NULL.
        Execute.Sql("""
            UPDATE jobs j
            SET j.main_blend_file = COALESCE(
                (SELECT MIN(f.relative_path) FROM job_files f
                 WHERE f.job_id = j.id AND LOWER(f.relative_path) LIKE '%.blend'),
                '')
            """);

        Alter.Column("main_blend_file").OnTable("jobs").AsString(1024).NotNullable();
    }

    public override void Down()
    {
        Delete.Column("main_blend_file").FromTable("jobs");
    }
}
