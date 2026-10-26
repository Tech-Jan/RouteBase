using Microsoft.EntityFrameworkCore;

using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Models;

public class RouteBaseDbContext : DbContext
{
    public RouteBaseDbContext(DbContextOptions<RouteBaseDbContext> options)
        : base(options)
    {
    }

    /// <summary>K1 strong-id converters — model-wide, so every mapped property/FK of an id type
    /// reads and writes its INTEGER column unchanged (DDL parity untouched). Probe coverage:
    /// StrongIdEfProbeTests pins autoincrement readback, Where ==, Contains, nullable FK, SET NULL.</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Properties<CommitId>().HaveConversion<CommitId.EfConverter>();
        configurationBuilder.Properties<TrackId>().HaveConversion<TrackId.EfConverter>();
        configurationBuilder.Properties<SegmentId>().HaveConversion<SegmentId.EfConverter>();
        configurationBuilder.Properties<GpsInSegmentId>().HaveConversion<GpsInSegmentId.EfConverter>();
        configurationBuilder.Properties<RaceId>().HaveConversion<RaceId.EfConverter>();
        configurationBuilder.Properties<CheckpointId>().HaveConversion<CheckpointId.EfConverter>();
        configurationBuilder.Properties<ParticipantId>().HaveConversion<ParticipantId.EfConverter>();
        configurationBuilder.Properties<PhotoId>().HaveConversion<PhotoId.EfConverter>();
    }

    public DbSet<Activity> Activities { get; set; }
    public DbSet<MultiDayTour> MultiDayTours { get; set; }
    public DbSet<Photo> Photos { get; set; }
    public DbSet<AppCredentials> AppCredentials { get; set; }
    public DbSet<Segment> Segments { get; set; }
    public DbSet<SegmentGps> SegmentGps { get; set; }
    public DbSet<GpsInSegment> GpsInSegments { get; set; }
    public DbSet<SegmentEffortExclusion> SegmentEffortExclusions { get; set; }
    public DbSet<ProfileSettings> ProfileSettings { get; set; }
    public DbSet<AppUser> Users { get; set; }
    public DbSet<Friendship> Friendships { get; set; }
    public DbSet<RecordingSession> RecordingSessions { get; set; }
    public DbSet<Heatmap> Heatmaps { get; set; }
    public DbSet<ActivityLifecycleLog> ActivityLifecycleLogs { get; set; }
    public DbSet<Race> Races { get; set; }
    public DbSet<RaceCheckpoint> RaceCheckpoints { get; set; }
    public DbSet<RaceParticipant> RaceParticipants { get; set; }
    public DbSet<RaceSplitTime> RaceSplitTimes { get; set; }
    public DbSet<ShareLink> ShareLinks { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configure Activity
        modelBuilder.Entity<Activity>(entity =>
        {
            entity.HasKey(e => e.TrackId);
            // Same K1 fallback as TrackEditCommit: the converter kills store-generation inference.
            entity.Property(e => e.TrackId).ValueGeneratedOnAdd();
            entity.Property(e => e.Visibility).HasDefaultValue(Visibilities.Default);
            // Store-level default so raw-SQL insert paths (import/strava) that omit the column still get a
            // real, visible activity. Parity with DbSchemaManager: TEXT NOT NULL DEFAULT 'released'.
            entity.Property(e => e.Lifecycle).HasDefaultValue(LifecycleStates.Released);
            // Merged-into pointer: purging the merge PRODUCT un-supersedes its sources rather than leaving
            // them pointing at a row that no longer exists. No navigation property — the id is all any
            // caller wants, and a self-nav on Activity would drag into every Include().
            entity.HasOne<Activity>()
                  .WithMany()
                  .HasForeignKey(e => e.SupersededBy)
                  .OnDelete(DeleteBehavior.SetNull);
            // The two commit pointers. Purging a commit nulls them rather than leaving them pointing at a row
            // that is gone — the dangling-id class this model exists to eliminate. These FKs are why
            // TrackEditCommit is an EF entity at all (see its header): EF can only reference EF entities, and
            // while the commit was a raw-DDL table these had to be hand-held by a DB trigger.
            entity.HasOne<TrackEditCommit>()
                  .WithMany()
                  .HasForeignKey(e => e.EditedCommitId)
                  .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<TrackEditCommit>()
                  .WithMany()
                  .HasForeignKey(e => e.ConsumedCommitId)
                  .OnDelete(DeleteBehavior.SetNull);
            // The checkout pointer — the lock, the badge and the resume target in one column. SET NULL is
            // load-bearing, not tidiness: aborting an edit is a DELETE of the session row, and the FK is what
            // releases the ride. No navigation property (house style — the id is all any caller wants).
            entity.HasOne<ActivityEditSession>()
                  .WithMany()
                  .HasForeignKey(e => e.InEditSessionId)
                  .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(e => e.Lifecycle).HasDatabaseName("IX_Activity_Lifecycle");
            entity.HasIndex(e => e.UserId).HasDatabaseName("IX_Activity_UserId");
            // (user_id ASC, start_datetime_utc DESC) — matches the legacy DDL in DbSchemaManager
            // (IX_Activity_UserId_StartDate); parity-canonical shape, enforced by DbSchemaParityTests.
            entity.HasIndex(e => new { e.UserId, e.StartDateTimeUtc }).IsDescending(false, true).HasDatabaseName("IX_Activity_UserId_StartDate");
            entity.HasIndex(e => e.Visibility).HasDatabaseName("IX_Activity_Visibility");
            // Parity with DbSchemaManager.EnsureIndexesAsync — ordering index for GetActivitiesAsync (ME-02).
            entity.HasIndex(e => e.StartDateTimeUtc).IsDescending().HasDatabaseName("IX_Activity_StartDate");
            // ActivityQueryService.GetAllActivitiesAsync/GetActivitiesWithGpsCoordinatesAsync order by
            // this column (the dashboard/overview grid's sort) — was an unindexed ORDER BY (TEMP B-TREE
            // sort), unlike start_datetime_utc above which already had one. Not the same column as the
            // dropped IX_Activity_StartDateLocal (DbSchemaManager.IndexDdl): that one served a
            // datetime()-wrapped filter no bare-column index can satisfy; this serves a plain ORDER BY.
            entity.HasIndex(e => e.StartDateTimeLocal).IsDescending().HasDatabaseName("IX_Activity_StartDateLoc");
        });

        // The edit commit. In the model for its SHAPE only — no DbSet; TrackEditCommitStore owns access.
        // Being here is what lets Activity and Activity_Lifecycle_Log declare real FKs to it (see the
        // entity's header). DDL parity: DbSchemaManager.EnsureTrackEditTablesAsync.
        modelBuilder.Entity<TrackEditCommit>(entity =>
        {
            entity.HasKey(e => e.CommitId);
            // K1 fallback (documented in the S6 probe): with a value converter on the key, EF stops
            // inferring store generation — every Added row then shares the sentinel key and the
            // identity map throws. Declaring generation explicitly restores autoincrement readback.
            entity.Property(e => e.CommitId).ValueGeneratedOnAdd();
            entity.HasIndex(e => new { e.UserId, e.CommittedAt }).HasDatabaseName("IX_TrackEditCommit_UserId");
        });

        // The edit session. Also SHAPE-only (no DbSet; EditSessionStore owns the data) — it is in the model so
        // Activity.in_edit_session_id can be a real FK to it. DDL parity: DbSchemaManager.EnsureTrackEditTablesAsync.
        modelBuilder.Entity<ActivityEditSession>(entity =>
        {
            entity.HasKey(e => e.SessionId);
            entity.Property(e => e.Status).HasDefaultValue(ActivityEditSessionStatuses.Active);
            entity.Property(e => e.Cursor).HasDefaultValue(0);
            entity.Property(e => e.StampsJson).HasDefaultValue("[]");
            entity.Property(e => e.EditCount).HasDefaultValue(0);
            entity.HasIndex(e => new { e.UserId, e.Status }).HasDatabaseName("IX_ActivityEditSession_UserId");
        });

        // Append-only lifecycle audit ("why did my ride vanish?"). Cascades with its activity — the log
        // explains the state of activities that still exist. DDL parity: DbSchemaManager.
        modelBuilder.Entity<ActivityLifecycleLog>(entity =>
        {
            entity.HasKey(e => e.LogId);
            entity.HasOne<Activity>()
                  .WithMany()
                  .HasForeignKey(e => e.TrackId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<TrackEditCommit>()
                  .WithMany()
                  .HasForeignKey(e => e.CommitId)
                  .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(e => new { e.TrackId, e.At }).HasDatabaseName("IX_ActivityLifecycleLog_TrackId");
        });

        // GPS point data is no longer an EF entity — it lives packed in Track_Blob (BlobStore), decoded via
        // the codec / ITrackReader. GpsData survives as a persistence-free POCO.

        // Configure Photo (replaces legacy Picture; that table is dropped in the same migration).
        // Every photo is assigned to an Activity (§5, no unassigned rows); the rendition files live on
        // the filesystem (IPhotoImageStore), only the ThumbHash is a DB column. DDL parity:
        // DbSchemaManager.EnsurePhotosTableAsync (enforced by DbSchemaParityTests).
        modelBuilder.Entity<Photo>(entity =>
        {
            entity.HasKey(e => e.PhotoId);
            // Same K1 fallback as TrackEditCommit: the converter kills store-generation inference.
            entity.Property(e => e.PhotoId).ValueGeneratedOnAdd();
            // Cascade so a deleted activity takes its photo rows with it; the FS files are purged
            // separately (IPhotoImageStore) — the filesystem isn't transactional with the DB.
            entity.HasOne<Activity>().WithMany(a => a.Photos).HasForeignKey(e => e.TrackId).OnDelete(DeleteBehavior.Cascade);
            // Merge origin: SET NULL, not cascade — purging the ride a photo came from must not delete the
            // photo, which now belongs to the merged ride. It only loses its way back.
            entity.HasOne<Activity>().WithMany().HasForeignKey(e => e.MergedFromTrackId).OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(e => e.TrackId).HasDatabaseName("IX_Photo_TrackId");
            entity.HasIndex(e => e.UserId).HasDatabaseName("IX_Photo_UserId");
            // D5 dedup lookups — both scoped per user (§11).
            entity.HasIndex(e => new { e.UserId, e.ContentHash }).HasDatabaseName("IX_Photo_UserId_ContentHash");
            entity.HasIndex(e => new { e.UserId, e.CapturedUtc }).HasDatabaseName("IX_Photo_UserId_CapturedUtc");
        });

        // Configure SegmentGps
        modelBuilder.Entity<SegmentGps>(entity =>
        {
            entity.HasIndex(e => e.SegmentId).HasDatabaseName("IX_SegmentGps_SegmentId");
        });

        // Configure GpsInSegment
        modelBuilder.Entity<GpsInSegment>(entity =>
        {
            entity.HasKey(e => e.GpsInSegmentId);
            // Same K1 fallback as TrackEditCommit: the converter kills store-generation inference.
            entity.Property(e => e.GpsInSegmentId).ValueGeneratedOnAdd();
            entity.HasIndex(e => e.SegmentId).HasDatabaseName("IX_GpsInSegment_SegmentId");
            entity.HasIndex(e => e.TrackId).HasDatabaseName("IX_GpsInSegment_TrackId");
        });

        // Durable effort-exclusion list (item 25). Read-time anti-join marker; no FK cascade (legacy
        // no-cascade convention — rows purged explicitly at delete seams). Indexed on (segment_id,
        // track_id) so the anti-join is one indexed lookup; DbSchemaManager mirrors this DDL for
        // legacy DBs (parity-tested).
        modelBuilder.Entity<SegmentEffortExclusion>(entity =>
        {
            entity.HasKey(e => e.ExclusionId);
            entity.HasIndex(e => new { e.SegmentId, e.TrackId }).HasDatabaseName("IX_SegmentEffortExclusion_SegmentTrack");
        });

        // Configure ProfileSettings
        modelBuilder.Entity<ProfileSettings>(entity =>
        {
            entity.HasKey(e => e.Id);
            // ME-04 closed at step 1b: UserId is NOT NULL, so this index really is unique. While it was
            // nullable SQLite counted each NULL as distinct, the constraint silently did not hold for
            // global rows, and the boot path had to dedupe them in code.
            entity.HasIndex(e => new { e.Category, e.SettingKey, e.UserId })
                .IsUnique();
        });

        // Configure Segment
        modelBuilder.Entity<Segment>(entity =>
        {
            // Same K1 fallback as TrackEditCommit: the converter kills store-generation inference.
            entity.Property(e => e.SegmentId).ValueGeneratedOnAdd();
            entity.Property(e => e.Visibility).HasDefaultValue(Visibilities.Default);
            entity.Property(e => e.SegmentKind).HasDefaultValue(SegmentKinds.Polyline);
            entity.HasIndex(e => e.UserId).HasDatabaseName("IX_Segment_UserId");
            entity.HasIndex(e => e.Visibility).HasDatabaseName("IX_Segment_Visibility");
        });

        // Configure MultiDayTour
        modelBuilder.Entity<MultiDayTour>(entity =>
        {
            entity.HasKey(e => e.MultiDayId);
            entity.HasMany(e => e.Activities)
                .WithOne(e => e.MultiDayTour)
                .HasForeignKey(e => e.MultiDayId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(e => e.UserId).HasDatabaseName("IX_MultiDayTour_UserId");
        });

        // Configure AppUser
        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.HasKey(e => e.UserId);
        });

        // Configure AppCredentials
        modelBuilder.Entity<AppCredentials>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Type);
            entity.HasIndex(e => new { e.UserId, e.Type }).HasDatabaseName("IX_AppCredentials_UserId_Type");
        });

        // Configure RecordingSession
        modelBuilder.Entity<RecordingSession>(entity =>
        {
            entity.HasKey(e => e.SessionId);
            entity.Property(e => e.Status).HasDefaultValue(RecordingSessionStatus.Recording);
            entity.Property(e => e.ActivityType).HasDefaultValue("Run");
            entity.Property(e => e.Visibility).HasDefaultValue(Visibilities.Default);
            entity.Property(e => e.PointCount).HasDefaultValue(0);
            entity.Property(e => e.LastSequence).HasDefaultValue(-1);
            entity.HasIndex(e => e.UserId).HasDatabaseName("IX_RecordingSession_UserId");
            entity.HasIndex(e => e.Status).HasDatabaseName("IX_RecordingSession_Status");
            // Single-active-session invariant (RS-02) — mirrors the partial unique index in
            // DbSchemaManager.EnsureRecordingSessionTableAsync (parity enforced by DbSchemaParityTests).
            entity.HasIndex(e => e.UserId, "IX_RecordingSession_ActiveUser")
                .IsUnique().HasFilter("status IN ('recording','paused')");
            // Mirrors legacy DDL (DbSchemaManager.EnsureRecordingSessionTableAsync): session dies with its activity.
            // Fresh DBs (EnsureCreated) get no FK otherwise → orphaned sessions on activity delete (ME-01).
            entity.HasOne<Activity>().WithMany().HasForeignKey(e => e.TrackId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.TrackId).HasDatabaseName("IX_RecordingSession_TrackId");
        });

        // Configure Heatmap (optional, one per shapes segment). Tile imagery lives on the filesystem
        // (HeatmapTilePackStore), not in the DB — no child tile entity.
        modelBuilder.Entity<Heatmap>(entity =>
        {
            entity.HasKey(e => e.HeatmapId);
            entity.Property(e => e.Status).HasDefaultValue(HeatmapStatuses.Pending);
            entity.HasIndex(e => e.SegmentId).IsUnique().HasDatabaseName("IX_Heatmap_SegmentId");
            entity.HasOne(e => e.Segment)
                .WithOne()
                .HasForeignKey<Heatmap>(e => e.SegmentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // --- Race slice (course = an Activity, never a Segment; see Race.cs) ---------------------
        // DDL parity: DbSchemaManager.EnsureRaceTablesAsync (enforced by DbSchemaParityTests).

        modelBuilder.Entity<Race>(entity =>
        {
            entity.HasKey(e => e.RaceId);
            // Same K1 fallback as TrackEditCommit: the converter kills store-generation inference.
            entity.Property(e => e.RaceId).ValueGeneratedOnAdd();
            entity.Property(e => e.Visibility).HasDefaultValue(Visibilities.Default);
            entity.Property(e => e.EditPolicy).HasDefaultValue("owner");
            // The course IS the race. Purging the course activity takes the race with it rather than
            // leaving a race that can never be replayed.
            entity.HasOne<Activity>().WithMany().HasForeignKey(e => e.CourseTrackId).OnDelete(DeleteBehavior.Cascade);
            // The reference effort only FITS the cost curve. Purging it must leave the race replayable —
            // it falls back to the course. SET NULL, never cascade.
            entity.HasOne<Activity>().WithMany().HasForeignKey(e => e.ReferenceTrackId).OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(e => e.UserId).HasDatabaseName("IX_Race_UserId");
            entity.HasIndex(e => e.CourseTrackId).HasDatabaseName("IX_Race_TrackId");
        });

        modelBuilder.Entity<RaceCheckpoint>(entity =>
        {
            entity.HasKey(e => e.CheckpointId);
            // Same K1 fallback as TrackEditCommit: the converter kills store-generation inference.
            entity.Property(e => e.CheckpointId).ValueGeneratedOnAdd();
            entity.HasOne(e => e.Race).WithMany(r => r.Checkpoints).HasForeignKey(e => e.RaceId).OnDelete(DeleteBehavior.Cascade);
            // Ordering is an invariant, not a convention: the pacing model walks checkpoints pairwise.
            entity.HasIndex(e => new { e.RaceId, e.Seq }).IsUnique().HasDatabaseName("IX_RaceCheckpoint_RaceSeq");
        });

        modelBuilder.Entity<RaceParticipant>(entity =>
        {
            entity.HasKey(e => e.ParticipantId);
            // Same K1 fallback as TrackEditCommit: the converter kills store-generation inference.
            entity.Property(e => e.ParticipantId).ValueGeneratedOnAdd();
            entity.Property(e => e.Kind).HasDefaultValue(RaceParticipantKind.Simulated);
            entity.Property(e => e.Seed).HasDefaultValue(0);
            entity.HasOne(e => e.Race).WithMany(r => r.Participants).HasForeignKey(e => e.RaceId).OnDelete(DeleteBehavior.Cascade);
            // A real participant IS his track. Purging the activity removes him from the field — SET NULL
            // would leave a 'real' row with no geometry and no splits, i.e. an invisible ghost.
            entity.HasOne<Activity>().WithMany().HasForeignKey(e => e.TrackId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.RaceId).HasDatabaseName("IX_RaceParticipant_RaceId");
            entity.HasIndex(e => e.TrackId).HasDatabaseName("IX_RaceParticipant_TrackId");
        });

        modelBuilder.Entity<RaceSplitTime>(entity =>
        {
            entity.HasKey(e => new { e.ParticipantId, e.CheckpointId });
            entity.HasOne(e => e.Participant).WithMany(p => p.SplitTimes).HasForeignKey(e => e.ParticipantId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Checkpoint).WithMany().HasForeignKey(e => e.CheckpointId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.CheckpointId).HasDatabaseName("IX_RaceSplitTime_CheckpointId");
        });

        // --- Share slice (anonymous capability URLs; see ShareLink.cs) ----------------------------
        // DDL parity: DbSchemaManager.EnsureShareLinkTableAsync (enforced by DbSchemaParityTests).

        modelBuilder.Entity<ShareLink>(entity =>
        {
            entity.HasKey(e => e.ShareLinkId);
            // Both cascade: purging the shared resource must revoke its links structurally, leaving no
            // window in which a live token resolves to a row that is gone. No navigation properties — the
            // id is all the resolver wants, and a nav would drag ShareLink into every Include().
            entity.HasOne<Activity>().WithMany().HasForeignKey(e => e.TrackId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Segment>().WithMany().HasForeignKey(e => e.SegmentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Race>().WithMany().HasForeignKey(e => e.RaceId).OnDelete(DeleteBehavior.Cascade);
            // THE lookup key. Unique so a hash collision surfaces as a write failure, never as two grants.
            entity.HasIndex(e => e.TokenHash).IsUnique().HasDatabaseName("IX_ShareLink_TokenHash");
            entity.HasIndex(e => e.UserId).HasDatabaseName("IX_ShareLink_UserId");
            // EF indexes every FK by convention, so these exist on the fresh side whether or not they are
            // declared — declared explicitly (with names) so the legacy DDL can mirror them exactly.
            entity.HasIndex(e => e.TrackId).HasDatabaseName("IX_ShareLink_TrackId");
            entity.HasIndex(e => e.SegmentId).HasDatabaseName("IX_ShareLink_SegmentId");
            entity.HasIndex(e => e.RaceId).HasDatabaseName("IX_ShareLink_RaceId");
            // The sweep's scan key.
            entity.HasIndex(e => e.ExpiresAt).HasDatabaseName("IX_ShareLink_ExpiresAt");
        });

        // Configure Friendship
        modelBuilder.Entity<Friendship>(entity =>
        {
            entity.HasKey(e => e.FriendshipId);
            entity.HasIndex(e => new { e.RequesterId, e.AddresseeId }).IsUnique();
            entity.HasIndex(e => new { e.RequesterId, e.Status }).HasDatabaseName("IX_Friendship_Requester");
            entity.HasIndex(e => new { e.AddresseeId, e.Status }).HasDatabaseName("IX_Friendship_Addressee");
            entity.HasOne(e => e.Requester).WithMany().HasForeignKey(e => e.RequesterId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Addressee).WithMany().HasForeignKey(e => e.AddresseeId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
