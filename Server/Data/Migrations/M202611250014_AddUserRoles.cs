using FluentMigrator;

namespace Server.Data.Migrations;

// User roles: Admin or User. Everyone who exists today had full rights, so they become admins.
[Migration(202611250014, "Add users.role")]
public class M202611250014_AddUserRoles : Migration
{
    public override void Up()
    {
        Alter.Table("users").AddColumn("role").AsAnsiString(32).NotNullable().WithDefaultValue("User");
        Execute.Sql("UPDATE users SET role = 'Admin'");
    }

    public override void Down()
    {
        Delete.Column("role").FromTable("users");
    }
}
