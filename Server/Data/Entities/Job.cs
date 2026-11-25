using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Server.Data.Entities;

public enum VideoStatus
{
    None,

    Pending,

    Encoding,

    Packaging,

    Ready,

    Failed,
}

public enum JobStatus
{
    Uploading,

    Uploaded,

    Analyzing,

    Ready,

    Rendering,

    Completed,

    Failed,
}

[Table("jobs")]
[Index(nameof(CreatedAtUtc), Name = "ix_jobs_created_at_utc")]
public class Job
{
    [Key]
    [Column("id")]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(255)]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    [Column("status", TypeName = "varchar(32)")]
    public JobStatus Status { get; set; } = JobStatus.Uploading;

    [MaxLength(255)]
    [Column("config_file_name")]
    public string? ConfigFileName { get; set; }

    [MaxLength(255)]
    [Column("source_folder_name")]
    public string? SourceFolderName { get; set; }

    [Required]
    [MaxLength(1024)]
    [Column("main_blend_file")]
    public string MainBlendFile { get; set; } = string.Empty;

    [Column("file_count")]
    public int FileCount { get; set; }

    [Column("total_bytes")]
    public long TotalBytes { get; set; }

    [MaxLength(2000)]
    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    [Column("created_at_utc")]
    public DateTime CreatedAtUtc { get; set; }

    [Column("completed_at_utc")]
    public DateTime? CompletedAtUtc { get; set; }

    // Frame range read from the main .blend file's active scene; null until analyzed.

    [MaxLength(255)]
    [Column("scene_name")]
    public string? SceneName { get; set; }

    [Column("frame_start")]
    public int? FrameStart { get; set; }

    [Column("frame_end")]
    public int? FrameEnd { get; set; }

    [Column("analyzed_at_utc")]
    public DateTime? AnalyzedAtUtc { get; set; }

    [Column("frame_rate")]
    public int? FrameRate { get; set; }

    // Video generation (runs once every sub task has completed).

    [Column("video_status", TypeName = "varchar(32)")]
    public VideoStatus VideoStatus { get; set; } = VideoStatus.None;

    [Column("video_frames_encoded")]
    public int VideoFramesEncoded { get; set; }

    [Column("video_frame_count")]
    public int VideoFrameCount { get; set; }

    [MaxLength(255)]
    [Column("video_file_name")]
    public string? VideoFileName { get; set; }

    [MaxLength(2000)]
    [Column("video_error")]
    public string? VideoError { get; set; }

    [Column("video_started_at_utc")]
    public DateTime? VideoStartedAtUtc { get; set; }

    [Column("video_completed_at_utc")]
    public DateTime? VideoCompletedAtUtc { get; set; }

    [NotMapped]
    public double VideoProgressPercent => VideoFrameCount == 0 ? 0 : Math.Min(100, 100.0 * VideoFramesEncoded / VideoFrameCount);

    [NotMapped]
    public TimeSpan? VideoTimeRemaining => VideoStatus != VideoStatus.Encoding ? null
        : Server.Jobs.RemainingTime.Estimate(VideoFramesEncoded, VideoFrameCount, VideoStartedAtUtc, DateTime.UtcNow);

    [NotMapped]
    public string? FrameRateDisplay => FrameRate?.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [NotMapped]
    public bool IsBusy => IsInProgress || VideoStatus is VideoStatus.Encoding or VideoStatus.Packaging;

    [Column("rendered_at_utc")]
    public DateTime? RenderedAtUtc { get; set; }

    [NotMapped]
    public bool IsInProgress => Status is JobStatus.Uploading or JobStatus.Analyzing;

    // Job progress is derived from its tasks at query time (requires Tasks to be loaded).

    [NotMapped]
    public int TotalFrames => Tasks.Sum(t => t.FrameCount);

    [NotMapped]
    public int FramesCompleted => Tasks.Sum(t => t.FramesCompleted);

    [NotMapped]
    public double ProgressPercent => TotalFrames == 0 ? 0 : 100.0 * FramesCompleted / TotalFrames;

    [NotMapped]
    public TimeSpan? RenderTimeRemaining => Status != JobStatus.Rendering ? null
        : Server.Jobs.RemainingTime.Estimate(FramesCompleted, TotalFrames,
            Tasks.Where(t => t.ClaimedAtUtc is not null).Select(t => t.ClaimedAtUtc).Min(), DateTime.UtcNow);

    [InverseProperty(nameof(JobTask.Job))]
    public List<JobTask> Tasks { get; set; } = [];
}
