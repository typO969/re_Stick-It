using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

using StickIt.Services;

namespace StickIt
{
   public partial class NoteManagerWindow : Window
   {
      private readonly ObservableCollection<NoteManagerItem> _items = new();

      public NoteManagerWindow()
      {
         InitializeComponent();
         AppThemeService.ApplyDialogTheme(this);
         NotesGrid.ItemsSource = _items;
         Loaded += (_, __) => RefreshItems();
         Activated += (_, __) => RefreshItems();
      }

      private App AppInstance => (App)System.Windows.Application.Current;

      private void RefreshItems()
      {
         var windows = AppInstance.GetOpenWindowsSnapshot();

         _items.Clear();
         foreach (var w in windows)
         {
            _items.Add(NoteManagerItem.FromWindow(w));
         }
      }

      private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshItems();

      private void Done_Click(object sender, RoutedEventArgs e) => Close();

      private NoteWindow? GetSelectedWindow()
      {
         return (NotesGrid.SelectedItem as NoteManagerItem)?.Window;
      }

      private void AddNote_Click(object sender, RoutedEventArgs e)
      {
         AppInstance.CreateNewNoteNear(GetSelectedWindow());
         RefreshItems();
      }

      private void SaveNotesNow_Click(object sender, RoutedEventArgs e)
      {
         AppInstance.SaveAllNotesNow();
      }
      private void SyncNow_Click(object sender, RoutedEventArgs e)
      {
         if (AppInstance.TrySyncNow(out var message))
            System.Windows.MessageBox.Show(this, message, "Sync", MessageBoxButton.OK, MessageBoxImage.Information);
         else
            System.Windows.MessageBox.Show(this, message, "Sync", MessageBoxButton.OK, MessageBoxImage.Warning);
      }

      private void PullNow_Click(object sender, RoutedEventArgs e)
      {
         if (AppInstance.TryPullFromSync(out var message))
            System.Windows.MessageBox.Show(this, message, "Sync Pull", MessageBoxButton.OK, MessageBoxImage.Information);
         else
            System.Windows.MessageBox.Show(this, message, "Sync Pull", MessageBoxButton.OK, MessageBoxImage.Warning);

         RefreshItems();
      }

      private void PushNow_Click(object sender, RoutedEventArgs e)
      {
         if (AppInstance.TryPushToSync(out var message))
            System.Windows.MessageBox.Show(this, message, "Sync Push", MessageBoxButton.OK, MessageBoxImage.Information);
         else
            System.Windows.MessageBox.Show(this, message, "Sync Push", MessageBoxButton.OK, MessageBoxImage.Warning);
      }

      private void ExportSelected_Click(object sender, RoutedEventArgs e)
      {
         var selected = GetSelectedWindow();
         if (selected == null)
         {
            System.Windows.MessageBox.Show(this, "Select a note first.", "Export note", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
         }

         selected.ExportNoteFromManager();
      }

      private void LoadIntoSelected_Click(object sender, RoutedEventArgs e)
      {
         var selected = GetSelectedWindow();
         if (selected == null)
         {
            System.Windows.MessageBox.Show(this, "Select a note first.", "Load note", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
         }

         selected.ImportNoteFromManager();
         RefreshItems();
      }

      private void Show_Click(object sender, RoutedEventArgs e)
      {
         if (sender is not FrameworkElement element || element.Tag is not NoteWindow w) return;

         w.Show();
         if (w.WindowState == WindowState.Minimized)
            w.WindowState = WindowState.Normal;

         w.Activate();
         w.Topmost = true;
         w.Topmost = false;

         AppInstance.QueueSaveFromWindow();
         RefreshItems();
      }

      private void Minimize_Click(object sender, RoutedEventArgs e)
      {
         if (sender is not FrameworkElement element || element.Tag is not NoteWindow w) return;

         w.WindowState = WindowState.Minimized;
         AppInstance.QueueSaveFromWindow();
         RefreshItems();
      }

      private void Restore_Click(object sender, RoutedEventArgs e)
      {
         if (sender is not FrameworkElement element || element.Tag is not NoteWindow w) return;

         w.Show();
         w.WindowState = WindowState.Normal;
         w.Activate();

         AppInstance.QueueSaveFromWindow();
         RefreshItems();
      }

      private void Close_Click(object sender, RoutedEventArgs e)
      {
         if (sender is not FrameworkElement element || element.Tag is not NoteWindow w) return;

         w.Close();
         AppInstance.QueueSaveFromWindow();
         RefreshItems();
      }

      private void ManagerLockToggle_Click(object sender, RoutedEventArgs e)
      {
         if (sender is System.Windows.Controls.CheckBox cb && cb.Tag is NoteWindow window)
         {
            if (window.DataContext is StickIt.Models.NoteModel note)
            {
               note.IsLocked = (cb.IsChecked == true);
               window.ApplyLockState();
               AppInstance.QueueSaveFromWindow();
            }
         }
      }

      private void DebugForceAging_Click(object sender, RoutedEventArgs e)
      {
         if (sender is MenuItem mi && int.TryParse(mi.Tag?.ToString(), out int targetDays))
         {
            // Because your DataGrid uses a custom wrapper object, 
            // we use dynamic to safely pull the Window property out of the selected row
            dynamic selectedItem = NotesGrid.SelectedItem;
            if (selectedItem == null) return;

            // Grab the active NoteWindow and its underlying NoteModel
            if (selectedItem.Window is NoteWindow openWindow &&
                openWindow.DataContext is StickIt.Models.NoteModel selectedNote)
            {
               // TIME TRAVEL: Physically alter the creation date!
               selectedNote.Props.CreatedUtc = DateTime.UtcNow.AddDays(-targetDays);

               // Reset the Easter Egg flag so we can trigger it multiple times during testing
               selectedNote.Props.HasSeenAnniversary = false;

               // If we are testing the Easter Egg, reset the rotation first so it can cleanly flip 180 deg
               if (targetDays == 365)
               {
                  openWindow.ApplyRotation();
               }

               // Trigger the redraw on the actual desktop window!
               openWindow.ApplyAging();

               // Force the app to save the new "fake" date to disk
               ((App)System.Windows.Application.Current).QueueSaveFromWindow();

               // Optional: Refresh the NoteManager list so it doesn't show old data
               Refresh_Click(this, new RoutedEventArgs());
            }
         }
      }

   }

   public sealed class NoteManagerItem
   {
      public string Title { get; init; } = string.Empty;
      public string Color { get; init; } = string.Empty;
      public string Sticky { get; init; } = string.Empty;
      public string Modified { get; init; } = string.Empty;
      public string IsMinimized { get; init; } = string.Empty;

      // --- NEW DEBUG PROPERTIES ---
      public string StuckTo { get; init; } = string.Empty;
      public double Rotation { get; init; }
      public int AgeDays { get; init; }
      public int AgingStage { get; init; }
      public bool IsLocked { get; init; }
      public double X { get; set; }
      public double Y { get; set; }

      public NoteWindow Window { get; init; } = null!;

      public static NoteManagerItem FromWindow(NoteWindow w)
      {
         var noteModel = w.DataContext as StickIt.Models.NoteModel;
         int age = 0;
         int stage = 0;

         if (noteModel != null)
         {
            age = (int)(DateTime.UtcNow - noteModel.Props.CreatedUtc).TotalDays;

            // Calculate stage for the dashboard
            if (age >= 60) stage = 8;
            else if (age >= 53) stage = 7;
            else if (age >= 45) stage = 6;
            else if (age >= 38) stage = 5;
            else if (age >= 30) stage = 4;
            else if (age >= 21) stage = 3;
            else if (age >= 14) stage = 2;
            else if (age >= 7) stage = 1;
         }

         return new NoteManagerItem
         {
            Title = string.IsNullOrWhiteSpace(w.GetTitle()) ? "Untitled" : w.GetTitle(),
            Color = w.GetColorKey().ToString(),
            Sticky = StickyLabel(w.GetStuckMode()),
            Modified = FormatDate(w.GetModifiedUtc()),
            IsMinimized = w.GetIsMinimized() ? "Yes" : "No",

            // --- POPULATE THE NEW COLUMNS ---
            StuckTo = w.GetStuckToWindowName(), // <--- Calls the helper we just added!
            Rotation = Math.Round(noteModel?.RotationAngle ?? 0.0, 1),
            AgeDays = age,
            AgingStage = stage,
            IsLocked = noteModel?.IsLocked ?? false,
            X = w.Left,
            Y = w.Top,

            Window = w
         };
      }

      private static string StickyLabel(int mode) => mode switch
      {
         0 => "Not stuck",
         1 => "Always on top",
         2 => "Stuck to window",
         _ => $"Unknown ({mode})"
      };

      private static string FormatDate(DateTime dt)
      {
         if (dt == default)
            return "Unknown";

         return dt.ToLocalTime().ToString("g");
      }
   }
}
