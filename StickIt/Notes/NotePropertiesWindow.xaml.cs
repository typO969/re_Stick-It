using System;
using System.Windows;

using StickIt.Persistence;
using StickIt.Services;

namespace StickIt
{
   public partial class NotePropertiesWindow : Window
   {
      public NotePropertiesWindow(NoteWindow noteWindow)
      {
         InitializeComponent();
         AppThemeService.ApplyDialogTheme(this);
         DataContext = NotePropertiesViewModel.FromNoteWindow(noteWindow);
      }

      private void Close_Click(object sender, RoutedEventArgs e) => Close();
   }

   public sealed class NotePropertiesViewModel
   {
      public string NoteTitle { get; init; } = string.Empty;
      public string NoteId { get; init; } = string.Empty;
      public string Contains { get; init; } = string.Empty;
      public string CreatedUtc { get; init; } = string.Empty;
      public string ModifiedUtc { get; init; } = string.Empty;
      public string Position { get; init; } = string.Empty;
      public string Size { get; init; } = string.Empty;
      public string Color { get; init; } = string.Empty;
      public string Sticky { get; init; } = string.Empty;
      public string StickyTarget { get; init; } = string.Empty;
      public string Font { get; init; } = string.Empty;
      public string Monitor { get; init; } = string.Empty;

      public static NotePropertiesViewModel FromNoteWindow(NoteWindow w)
      {
         var text = w.GetText();
         var chars = text.Length;
         var words = CountWords(text);
         var lines = CountLines(text);

         return new NotePropertiesViewModel
         {
            NoteTitle = string.IsNullOrWhiteSpace(w.GetTitle()) ? "Untitled" : w.GetTitle(),
            NoteId = w.NoteId,
            Contains = $"{chars} chars, {words} words, {lines} lines",
            CreatedUtc = FormatDate(w.GetCreatedUtc()),
            ModifiedUtc = FormatDate(w.GetModifiedUtc()),
            Position = $"X={w.Left:0}, Y={w.Top:0}",
            Size = $"W={w.Width:0}, H={w.Height:0}",
            Color = w.GetColorKey().ToString(),
            Sticky = StickyLabel(w.GetStuckMode()),
            StickyTarget = FormatStickyTarget(w.GetStickyTargetPersist()),
            Font = $"{w.GetFontFamily()}, {w.GetFontSize():0.#} dip",
            Monitor = FormatMonitor(w)
         };
      }

      private static string FormatDate(DateTime dt)
      {
         if (dt == default)
            return "Unknown";

         return dt.ToLocalTime().ToString("g");
      }

      private static int CountWords(string s)
      {
         if (string.IsNullOrWhiteSpace(s)) return 0;
         return s.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
      }

      private static int CountLines(string s)
      {
         if (string.IsNullOrEmpty(s)) return 0;
         return s.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).Length;
      }

      private static string StickyLabel(int mode) => mode switch
      {
         0 => "Not stuck",
         1 => "Always on top",
         2 => "Stuck to window",
         _ => $"Unknown ({mode})"
      };

      private static string FormatStickyTarget(StickyTargetPersist? target)
      {
         if (target == null)
            return "None";

         var title = string.IsNullOrWhiteSpace(target.WindowTitle) ? "Untitled window" : target.WindowTitle;
         var processName = string.IsNullOrWhiteSpace(target.ProcessName) ? "Unknown process" : target.ProcessName;

         var pidLabel = target.ProcessId > 0 ? $" (PID {target.ProcessId})" : string.Empty;
         return $"{processName} - {title}{pidLabel}";
      }

      private static string FormatMonitor(NoteWindow w)
      {
         try
         {
            var screen = MonitorAffinityService.GetScreenForWindow(w);
            return $"{screen.DeviceName} ({screen.WorkingArea.Width}x{screen.WorkingArea.Height})";
         }
         catch
         {
            return "Unknown";
         }
      }
   }
}
