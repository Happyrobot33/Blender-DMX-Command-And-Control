using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Server.Data.Entities;

[Table("render_machines")]
[Index(nameof(Name), IsUnique = true, Name = "ux_render_machines_name")]
[Index(nameof(TokenHash), IsUnique = true, Name = "ux_render_machines_token_hash")]
public class RenderMachine
{
    [Key]
    [Column("id")]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    [Required]
    [Column("token_hash", TypeName = "char(64)")]
    public string TokenHash { get; set; } = string.Empty;

    [Required]
    [Column("token_prefix", TypeName = "varchar(16)")]
    public string TokenPrefix { get; set; } = string.Empty;

    [Column("is_enabled")]
    public bool IsEnabled { get; set; } = true;

    [Column("created_at_utc")]
    public DateTime CreatedAtUtc { get; set; }

    [Column("token_issued_at_utc")]
    public DateTime TokenIssuedAtUtc { get; set; }

    [Column("last_seen_at_utc")]
    public DateTime? LastSeenAtUtc { get; set; }
}
