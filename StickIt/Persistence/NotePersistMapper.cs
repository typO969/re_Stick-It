using System;

using StickIt.Models;
using StickIt.Services;

namespace StickIt.Persistence
{
   public static class NotePersistMapper
   {
      public static NoteModel ToModel(NotePersist p)
      {
         var m = new NoteModel();

         // Canonical identity
         m.Props.Id = p.Id;

         // Visible metadata
         m.Title = p.Title;

         m.Props.CreatedUtc = p.CreatedUtc;
         m.Props.ModifiedUtc = p.ModifiedUtc;

         if (Enum.TryParse(p.ColorKey, out NoteColors.NoteColor ck))
            m.ColorKey = ck;
         else
            m.ColorKey = NoteColors.NoteColor.ThreeMYellow;

         m.IsLocked = p.IsLocked;

         m.RotationAngle = p.RotationAngle;

         m.FontFamily = p.FontFamily;
         m.FontSize = p.FontSize;
         m.Props.LineHeightMultiplier = p.LineHeightMultiplier > 0 ? p.LineHeightMultiplier : m.Props.LineHeightMultiplier;

         return m;
      }

      public static NotePersist FromWindow(
           NoteWindow w,
            int stuckModeFinal,
            DateTime modifiedUtc)
      {

         var persisted = new NotePersist
         {
            Id = w.NoteId,

            Left = w.Left,
            Top = w.Top,
            Width = w.Width,
            Height = w.Height,

            Title = w.GetTitle(),

            Rtf = w.GetRtf(),
            RtfSchemaVersion = 1,
            InkIsfBase64 = w.GetInkIsfBase64(),
            Text = w.GetText(),

            ColorKey = w.GetColorKey().ToString(),

            IsLocked = w.GetIsLocked(),

            RotationAngle = w.GetRotationAngle(),

            FontFamily = w.GetFontFamily(),
            FontSize = w.GetFontSize(),
            LineHeightMultiplier = w.GetLineHeightMultiplier(),

            StickyTargetPersist = w.GetStickyTargetPersist(),

            StuckMode = stuckModeFinal,
            IsMinimized = w.GetIsMinimized(),

            // Canonical timestamps now live on the note itself
            CreatedUtc = w.GetCreatedUtc(),
            ModifiedUtc = modifiedUtc
         };

         MonitorAffinityService.CaptureForWindow(persisted, w);
         return persisted;
      }
   }
}
