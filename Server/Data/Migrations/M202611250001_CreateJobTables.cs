using FluentMigrator;

namespace Server.Data.Migrations;

[Migration(202611250001, "Create jobs and job_files tables")]
public class M202611250001_CreateJobTables : Migration
{
    public override void Up()
    {
        Create.Table("jobs")
            .WithColumn("id").AsFixedLengthAnsiString(36).NotNullable().PrimaryKey("pk_jobs")
            .WithColumn("name").AsString(255).NotNullable()
            .WithColumn("status").AsAnsiString(32).NotNullable()
            .WithColumn("source_folder_name").AsString(255).Nullable()
            .WithColumn("file_count").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("total_bytes").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("error_message").AsString(2000).Nullable()
            .WithColumn("created_at_utc").AsCustom("DATETIME(6)").NotNullable()
            .WithColumn("completed_at_utc").AsCustom("DATETIME(6)").Nullable();

        Create.Index("ix_jobs_created_at_utc").OnTable("jobs").OnColumn("created_at_utc").Descending();

        Create.Table("job_files")
            .WithColumn("id").AsInt64().NotNullable().PrimaryKey("pk_job_files").Identity()
            .WithColumn("job_id").AsFixedLengthAnsiString(36).NotNullable()
                .ForeignKey("fk_job_files_jobs", "jobs", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("relative_path").AsString(1024).NotNullable()
            .WithColumn("size_bytes").AsInt64().NotNullable()
            .WithColumn("uploaded_at_utc").AsCustom("DATETIME(6)").NotNullable();

        Create.Index("ix_job_files_job_id").OnTable("job_files").OnColumn("job_id");
    }

    public override void Down()
    {
        Delete.Table("job_files");
        Delete.Table("jobs");
    }
}
