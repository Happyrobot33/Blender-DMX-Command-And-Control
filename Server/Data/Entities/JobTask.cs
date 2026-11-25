using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Server.Data.Entities;

public enum JobTaskStatus
{
    Pending,

    Assigned,

    Running,

    Completed,

    Failed,
}

[Table("job_tasks")]
[Index(nameof(JobId), nameof(TaskIndex), IsUnique = true, Name = "ux_job_tasks_job_id_task_index")]
[Index(nameof(Status), Name = "ix_job_tasks_status")]
public class JobTask
{
    [Key]
    [Column("id")]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Column("job_id")]
    public Guid JobId { get; set; }

    [ForeignKey(nameof(JobId))]
    public Job Job { get; set; } = null!;

    [Column("task_index")]
    public int TaskIndex { get; set; }

    [Column("frame_start")]
    public int FrameStart { get; set; }

    [Column("frame_end")]
    public int FrameEnd { get; set; }

    [Column("frame_count")]
    public int FrameCount { get; set; }

    [Column("status", TypeName = "varchar(32)")]
    public JobTaskStatus Status { get; set; } = JobTaskStatus.Pending;

    [Column("frames_completed")]
    public int FramesCompleted { get; set; }

    [MaxLength(2000)]
    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    [Column("created_at_utc")]
    public DateTime CreatedAtUtc { get; set; }

    // How often this task has been reported as failed; each failure puts it back in the queue.
    [Column("fail_count")]
    public int FailCount { get; set; }

    [Column("last_failed_at_utc")]
    public DateTime? LastFailedAtUtc { get; set; }

    [Column("claimed_at_utc")]
    public DateTime? ClaimedAtUtc { get; set; }

    [Column("started_at_utc")]
    public DateTime? StartedAtUtc { get; set; }

    [Column("completed_at_utc")]
    public DateTime? CompletedAtUtc { get; set; }

    [Column("updated_at_utc")]
    public DateTime? UpdatedAtUtc { get; set; }

    [Column("render_machine_id")]
    public int? RenderMachineId { get; set; }

    [ForeignKey(nameof(RenderMachineId))]
    public RenderMachine? RenderMachine { get; set; }

    [NotMapped]
    public double ProgressPercent => FrameCount == 0 ? 0 : 100.0 * FramesCompleted / FrameCount;

    [NotMapped]
    public TimeSpan? TimeRemaining => Status is not (JobTaskStatus.Assigned or JobTaskStatus.Running) ? null
        : Server.Jobs.RemainingTime.Estimate(FramesCompleted, FrameCount, ClaimedAtUtc ?? StartedAtUtc, DateTime.UtcNow);
}
