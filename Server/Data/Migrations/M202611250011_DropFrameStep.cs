using FluentMigrator;

namespace Server.Data.Migrations;

[Migration(202611250011, "Drop jobs.frame_step (frame step is always 1)")]
public class M202611250011_DropFrameStep : Migration
{
    public override void Up()
    {
        Delete.Column("frame_step").FromTable("jobs");
    }

    public override void Down()
    {
        Alter.Table("jobs").AddColumn("frame_step").AsInt32().Nullable();
    }
}
