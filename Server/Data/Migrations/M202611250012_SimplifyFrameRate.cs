using FluentMigrator;

namespace Server.Data.Migrations;

[Migration(202611250012, "Replace jobs.frame_rate_num/den with integer frame_rate")]
public class M202611250012_SimplifyFrameRate : Migration
{
    public override void Up()
    {
        Alter.Table("jobs").AddColumn("frame_rate").AsInt32().Nullable();
        Execute.Sql("UPDATE jobs SET frame_rate = ROUND(CAST(frame_rate_num AS DOUBLE) / frame_rate_den) WHERE frame_rate_num IS NOT NULL AND frame_rate_den > 0");
        Delete.Column("frame_rate_num").Column("frame_rate_den").FromTable("jobs");
    }

    public override void Down()
    {
        Alter.Table("jobs")
            .AddColumn("frame_rate_num").AsInt32().Nullable()
            .AddColumn("frame_rate_den").AsInt32().Nullable();
        Execute.Sql("UPDATE jobs SET frame_rate_num = frame_rate, frame_rate_den = 1 WHERE frame_rate IS NOT NULL");
        Delete.Column("frame_rate").FromTable("jobs");
    }
}
