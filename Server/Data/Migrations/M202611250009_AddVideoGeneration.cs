using FluentMigrator;

namespace Server.Data.Migrations;

[Migration(202611250009, "Add scene frame rate and video generation tracking to jobs")]
public class M202611250009_AddVideoGeneration : Migration
{
    public override void Up()
    {
        Alter.Table("jobs")
            // Scene frame rate as an exact fraction (fps / fps_base), e.g. 24000/1001 for 23.976.
            // Null for jobs analyzed before this migration; the video worker rescans those.
            .AddColumn("frame_rate_num").AsInt32().Nullable()
            .AddColumn("frame_rate_den").AsInt32().Nullable()
            .AddColumn("video_status").AsAnsiString(32).NotNullable().WithDefaultValue("None")
            .AddColumn("video_frames_encoded").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("video_frame_count").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("video_file_name").AsString(255).Nullable()
            .AddColumn("video_error").AsString(2000).Nullable()
            .AddColumn("video_started_at_utc").AsCustom("DATETIME(6)").Nullable()
            .AddColumn("video_completed_at_utc").AsCustom("DATETIME(6)").Nullable();
    }

    public override void Down()
    {
        Delete.Column("frame_rate_num").Column("frame_rate_den").Column("video_status")
            .Column("video_frames_encoded").Column("video_frame_count").Column("video_file_name").Column("video_error")
            .Column("video_started_at_utc").Column("video_completed_at_utc").FromTable("jobs");
    }
}
