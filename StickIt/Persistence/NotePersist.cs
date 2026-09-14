using System;
using System.Collections.Generic;

using StickIt.Services;
using StickIt.Sticky;

namespace StickIt.Persistence
{

   public sealed class NotePersist
   {
      public string Id { get; set; } = Guid.NewGuid().ToString("N");

      // Window position/size
      public double Left { get; set; }
      public double Top { get; set; }
      public double Width { get; set; }
      public double Height { get; set; }

      public string? Rtf { get; set; } = null;
      public int RtfSchemaVersion { get; set; } = 1;
      public string? InkIsfBase64 { get; set; } = null;

      // Content + appearance
      public string Title { get; set; } = "Untitled";
      public string Text { get; set; } = "";

      public string ColorKey { get; set; } = nameof(NoteColors.NoteColor.ThreeMYellow);

      public string FontFamily { get; set; } = "Segoe UI";
      public double FontSize { get; set; } = 14.0;
      public double LineHeightMultiplier { get; set; } = 1.0;

      public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
      public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;


      public StickyTargetPersist? StickyTargetPersist { get; set; } = null;

      public bool IsMinimized { get; set; } = false; // already present; keep one canonical field

      // Inside the NotePersist class, add this line:
      public bool IsLocked { get; set; } = false;

      public double RotationAngle { get; set; } = 0;

      // Phase-3 readiness: monitor affinity metadata for smarter multi-monitor restore.
      public string? MonitorDeviceName { get; set; } = null;
      public double? MonitorWorkAreaLeft { get; set; } = null;
      public double? MonitorWorkAreaTop { get; set; } = null;
      public double? MonitorWorkAreaWidth { get; set; } = null;
      public double? MonitorWorkAreaHeight { get; set; } = null;


      // 0 = not stuck, 1 = always-on-top, 2 = stuck-to-program (future)
      public int StuckMode { get; set; } = 0;
   }

   public sealed class StickItState
   {
      public int Version { get; set; } = StateMigrator.CurrentVersion;

      public List<NotePersist> Notes { get; set; } = new();
      public AppPreferences Preferences { get; set; } = new();
   }

}
