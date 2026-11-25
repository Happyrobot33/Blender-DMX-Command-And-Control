using FluentMigrator;

namespace Server.Data.Migrations;

[Migration(202611250006, "Add users, render_machines and job_tasks.render_machine_id")]
public class M202611250006_AddAuthentication : Migration
{
    public override void Up()
    {
        Create.Table("users")
            .WithColumn("id").AsInt32().NotNullable().PrimaryKey("pk_users").Identity()
            .WithColumn("username").AsString(64).NotNullable().Unique("ux_users_username")
            // ASP.NET Core PasswordHasher output (PBKDF2, salted, versioned format).
            .WithColumn("password_hash").AsAnsiString(255).NotNullable()
            // Rotated on password change; signed-in sessions carrying an old stamp are rejected.
            .WithColumn("security_stamp").AsFixedLengthAnsiString(32).NotNullable()
            .WithColumn("created_at_utc").AsCustom("DATETIME(6)").NotNullable()
            .WithColumn("last_login_at_utc").AsCustom("DATETIME(6)").Nullable();

        Create.Table("render_machines")
            .WithColumn("id").AsInt32().NotNullable().PrimaryKey("pk_render_machines").Identity()
            .WithColumn("name").AsString(100).NotNullable().Unique("ux_render_machines_name")
            // SHA-256 (hex) of the API token. The token itself is only shown once, when issued.
            .WithColumn("token_hash").AsFixedLengthAnsiString(64).NotNullable().Unique("ux_render_machines_token_hash")
            // First characters of the token, so admins can tell tokens apart.
            .WithColumn("token_prefix").AsAnsiString(16).NotNullable()
            .WithColumn("is_enabled").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("created_at_utc").AsCustom("DATETIME(6)").NotNullable()
            .WithColumn("token_issued_at_utc").AsCustom("DATETIME(6)").NotNullable()
            .WithColumn("last_seen_at_utc").AsCustom("DATETIME(6)").Nullable();

        Alter.Table("job_tasks")
            .AddColumn("render_machine_id").AsInt32().Nullable()
                .ForeignKey("fk_job_tasks_render_machines", "render_machines", "id").OnDelete(System.Data.Rule.SetNull);
    }

    public override void Down()
    {
        Delete.ForeignKey("fk_job_tasks_render_machines").OnTable("job_tasks");
        Delete.Column("render_machine_id").FromTable("job_tasks");
        Delete.Table("render_machines");
        Delete.Table("users");
    }
}
