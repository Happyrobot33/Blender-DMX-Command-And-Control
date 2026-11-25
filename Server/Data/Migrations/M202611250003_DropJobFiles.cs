using FluentMigrator;

namespace Server.Data.Migrations;

[Migration(202611250003, "Drop job_files table (files are read from disk at runtime)")]
public class M202611250003_DropJobFiles : Migration
{
    public override void Up()
    {
        Delete.Table("job_files");
    }

    public override void Down()
    {
        Create.Table("job_files")
            .WithColumn("id").AsInt64().NotNullable().PrimaryKey("pk_job_files").Identity()
            .WithColumn("job_id").AsFixedLengthAnsiString(36).NotNullable()
                .ForeignKey("fk_job_files_jobs", "jobs", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("relative_path").AsString(1024).NotNullable()
            .WithColumn("size_bytes").AsInt64().NotNullable()
            .WithColumn("uploaded_at_utc").AsCustom("DATETIME(6)").NotNullable();

        Create.Index("ix_job_files_job_id").OnTable("job_files").OnColumn("job_id");
    }
}
