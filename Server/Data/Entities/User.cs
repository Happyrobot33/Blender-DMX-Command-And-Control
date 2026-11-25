using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Server.Data.Entities;

public enum UserRole
{
    User,
    Admin,
}

[Table("users")]
[Index(nameof(Username), IsUnique = true, Name = "ux_users_username")]
public class User
{
    [Key]
    [Column("id")]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    [MaxLength(64)]
    [Column("username")]
    public string Username { get; set; } = string.Empty;

    [Required]
    [MaxLength(255)]
    [Column("password_hash", TypeName = "varchar(255)")]
    public string PasswordHash { get; set; } = string.Empty;

    [Required]
    [Column("security_stamp", TypeName = "char(32)")]
    public string SecurityStamp { get; set; } = string.Empty;

    [Column("role", TypeName = "varchar(32)")]
    public UserRole Role { get; set; } = UserRole.User;

    [Column("created_at_utc")]
    public DateTime CreatedAtUtc { get; set; }

    [Column("last_login_at_utc")]
    public DateTime? LastLoginAtUtc { get; set; }
}
