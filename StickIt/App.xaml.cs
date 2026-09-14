using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

using Microsoft.Win32;

using StickIt.Persistence;
using StickIt.Services;
using StickIt.Sticky.Services;

namespace StickIt
{
   public partial class App : System.Windows.Application
   {
      private StickItState _state = new();
      private readonly List<NoteWindow> _windows = new();

      private bool _isShuttingDown;
      private TrayIconService? _tray;
      private DateTime _lastTrayNoticeUtc = DateTime.MinValue;
      private bool _shownStillRunningNoticeThisSession;
      private NoteManagerWindow? _noteManagerWindow;
      private PreferencesWindow? _preferencesWindow;

      private System.Threading.Mutex? _singleInstanceMutex;
      private const int MaxNotesToAutoOpen = 25;

      private double _nextSpawnLeft = 200;
      private double _nextSpawnTop = 200;





      private DateTime? _pendingSaveDueUtc;
      private bool _suppressAutoSave;

      // Debounce saving so we don’t write on every keystroke/mouse move
      private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };

      public AppPreferences Preferences => _state.Preferences;
      public bool IsShuttingDown => _isShuttingDown;

      protected override void OnStartup(StartupEventArgs e)
      {
         base.OnStartup(e);

         ShutdownMode = ShutdownMode.OnExplicitShutdown;

         bool createdNew;
         _singleInstanceMutex = new System.Threading.Mutex(true, @"Global\969Studios.StickIt.v4", out createdNew);

         if (!createdNew)
         {
            Shutdown();
            return;
         }


         _saveTimer.Tick += (_, __) =>
         {
            _saveTimer.Stop();
            PersistAllWindows();
         };
         SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

         _state = JsonStore.LoadOrDefault();
         ApplyPreferences(_state.Preferences, persist: false);

         if (_state.Notes.Count == 0)
         {
            _state.Notes.Add(new NotePersist
            {
               Left = 200,
               Top = 200,
               Width = 400,
               Height = 400,
               Text = "",
               ColorKey = nameof(NoteColors.NoteColor.ThreeMYellow)
            });
            JsonStore.Save(_state);
         }

         var notes = _state.Notes ?? new List<NotePersist>();

         if (notes.Count > MaxNotesToAutoOpen)
         {
            var total = notes.Count;

            notes = notes
                .OrderByDescending(n => n.ModifiedUtc)
                .Take(MaxNotesToAutoOpen)
                .ToList();

            _state.Notes = notes;
            JsonStore.Save(_state);

            _tray?.ShowTrimmedLoadNotice(notes.Count, total);
         }

         foreach (var note in notes)
         {
            SpawnWindow(note);
         }

         UpdateTrayIcon();





         Exit += (_, __) =>
         {
            _saveTimer.Stop();
            _tray?.Dispose();
            _tray = null;
            _preferencesWindow?.Close();
            _preferencesWindow = null;
            FlushPendingUiEdits();
            PersistAllWindowsOnShutdown();
         };

         SessionEnding += (_, __) =>
         {
            _saveTimer.Stop();
            FlushPendingUiEdits();
            PersistAllWindowsOnShutdown();
         };
      }

      private void FlushPendingUiEdits()
      {
         // 1) Force focused editors to commit (LostFocus/selection changes, etc.)
         try
         {
            Keyboard.ClearFocus();
         }
         catch
         {
            // best-effort
         }

         // 2) Drain dispatcher so pending input/render/binding work is applied before reading UI state
         try
         {
            Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });
         }
         catch
         {
            // best-effort
         }
      }

      private System.Windows.Point FindNonOverlappingPosition(double desiredLeft, double desiredTop, double w, double h)
      {
         double left = SystemParameters.VirtualScreenLeft;
         double top = SystemParameters.VirtualScreenTop;
         double width = SystemParameters.VirtualScreenWidth;
         double height = SystemParameters.VirtualScreenHeight;

         const int step = 24;
         const double margin = 20;

         double minX = left + margin;
         double minY = top + margin;
         double maxX = (left + width) - w - margin;
         double maxY = (top + height) - h - margin;

         // Clamp desired start
         double startX = Math.Max(minX, Math.Min(maxX, desiredLeft));
         double startY = Math.Max(minY, Math.Min(maxY, desiredTop));

         // Occupied rects = currently open note windows
         var occupied = _windows
            .Where(win => win != null && win.IsLoaded)
            .Select(win =>
            {
               // Use RestoreBounds if minimized, and fall back to the intended new note size.
               var b = (win.WindowState == WindowState.Minimized) ? win.RestoreBounds : new Rect(win.Left, win.Top, win.Width, win.Height);

               double ow = (b.Width > 1) ? b.Width : w;
               double oh = (b.Height > 1) ? b.Height : h;

               return new Rect(b.Left, b.Top, ow, oh);
            })
            .ToList();


         // Quick accept if free
         var startRect = new Rect(startX, startY, w, h);
         if (!occupied.Any(r => r.IntersectsWith(startRect)))
            return new System.Windows.Point(startX, startY);

         // Scan outward in a simple expanding pattern
         for (int ring = 1; ring < 200; ring++)
         {
            double dx = ring * step;
            double dy = ring * step;

            var candidates = new[]
            {
         new System.Windows.Point(startX + dx, startY),
         new System.Windows.Point(startX - dx, startY),
         new System.Windows.Point(startX, startY + dy),
         new System.Windows.Point(startX, startY - dy),

         new System.Windows.Point(startX + dx, startY + dy),
         new System.Windows.Point(startX + dx, startY - dy),
         new System.Windows.Point(startX - dx, startY + dy),
         new System.Windows.Point(startX - dx, startY - dy),
      };

            foreach (var c in candidates)
            {
               double cx = Math.Max(minX, Math.Min(maxX, c.X));
               double cy = Math.Max(minY, Math.Min(maxY, c.Y));

               var r = new Rect(cx, cy, w, h);
               if (!occupied.Any(o => o.IntersectsWith(r)))
                  return new System.Windows.Point(cx, cy);
            }
         }

         // Last resort: return clamped desired
         return new System.Windows.Point(startX, startY);
      }


      public void ShutdownRequested()
      {
         if (_isShuttingDown) return;
         _isShuttingDown = true;

         _saveTimer.Stop();

         FlushPendingUiEdits();
         PersistAllWindowsOnShutdown();

         foreach (var w in _windows.ToArray())
         {
            try { w.Close(); }
            catch { /* ignore */ }
         }

         _tray?.Dispose();
         _tray = null;

         Shutdown();
      }

      //Gemini in

      protected override void OnExit(ExitEventArgs e)
      {
         try
         {
            _noteManagerWindow?.Close();
         }
         catch
         {
            // best-effort
         }

         _noteManagerWindow = null;
         _preferencesWindow = null;

         _tray?.Dispose();
         _tray = null;

         // Only release if THIS instance actually created and owns the mutex
         if (_singleInstanceMutex != null)
         {
            //if (_ownsMutex)
            //{
            //   try
            //   {
            //      _singleInstanceMutex.ReleaseMutex();
            //   }
            //   catch
            //   {
            //      // Fallback catch just in case of thread-identity mismatch during shutdown
            //   }
            //}

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
         }

         base.OnExit(e);
      }

      //Gemini out


      public void CreateNewNoteFromTray()
      {
         // NotifyIcon can raise events via WinForms plumbing; force onto WPF UI thread.
         Dispatcher.BeginInvoke(new Action(() => CreateNewNoteNear(null)));
      }


      public void NotifyStillRunningIfLastNoteClosed()
      {
         if (_tray == null) return;
         if (_shownStillRunningNoticeThisSession) return;

         _shownStillRunningNoticeThisSession = true;
         _tray.ShowStillRunningNotice();
      }


      public void MinimizeAllNotes()
      {
         foreach (var w in _windows.ToArray())
         {
            try { w.WindowState = System.Windows.WindowState.Minimized; }
            catch { /* ignore */ }
         }

         _saveTimer.Stop();
         FlushPendingUiEdits();
         PersistAllWindows();
      }
      public void RestoreAllNotes()
      {
         foreach (var w in _windows.ToArray())
         {
            try
            {
               w.Show();
               w.WindowState = System.Windows.WindowState.Normal;
            }
            catch { /* ignore */ }
         }

         _saveTimer.Stop();
         FlushPendingUiEdits();
         PersistAllWindows();
      }
      public void ShowAllNotes()
      {
         foreach (var w in _windows.ToArray())
         {
            try
            {
               w.Show();

               // If minimized, restore to normal so it can actually come to front.
               if (w.WindowState == System.Windows.WindowState.Minimized)
                  w.WindowState = System.Windows.WindowState.Normal;

               w.Activate();

               // Helps when Activate() is ignored due to focus rules.
               w.Topmost = true;
               w.Topmost = false;
            }
            catch { /* ignore */ }
         }

         _saveTimer.Stop();
         FlushPendingUiEdits();
         PersistAllWindows();
      }

      public void SaveAllNotesNow()
      {
         _saveTimer.Stop();
         FlushPendingUiEdits();
         PersistAllWindows();
      }

      public bool TrySyncNow(out string message)
      {
         return TrySyncCore(SyncCommand.Auto, out message);
      }

      public bool TryPullFromSync(out string message)
      {
         return TrySyncCore(SyncCommand.Pull, out message);
      }

      public bool TryPushToSync(out string message)
      {
         return TrySyncCore(SyncCommand.Push, out message);
      }

      public bool TryImportExternalNote(NotePersist incomingNote, SyncImportMode importMode, NoteWindow? contextWindow, out string message)
      {
         if (incomingNote == null)
         {
            message = "Import failed: note payload is empty.";
            return false;
         }

         if (string.IsNullOrWhiteSpace(incomingNote.Id))
            incomingNote.Id = Guid.NewGuid().ToString("N");

         try
         {
            _saveTimer.Stop();
            FlushPendingUiEdits();
            PersistAllWindows();

            if (importMode == SyncImportMode.ReplaceCurrentNotes)
            {
               _suppressAutoSave = true;
               try
               {
                  foreach (var w in _windows.ToArray())
                  {
                     try { w.Close(); } catch { }
                  }

                  _windows.Clear();
                  _state.Notes = new List<NotePersist> { incomingNote };
                  SpawnWindow(incomingNote);
               }
               finally
               {
                  _suppressAutoSave = false;
               }

               JsonStore.Save(_state);
               message = "Import complete. Replaced current notes with imported note.";
               return true;
            }

            var existing = _state.Notes.FirstOrDefault(n => string.Equals(n.Id, incomingNote.Id, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
               _state.Notes.Add(incomingNote);
               SpawnWindow(incomingNote);
               JsonStore.Save(_state);
               message = "Import complete. Added imported note.";
               return true;
            }

            if (importMode == SyncImportMode.AddMissingSyncedNotes)
            {
               message = "Import complete. Note already exists; no changes made.";
               return true;
            }

            if (incomingNote.ModifiedUtc < existing.ModifiedUtc)
            {
               message = "Import complete. Existing note is newer; no changes made.";
               return true;
            }

            var index = _state.Notes.IndexOf(existing);
            if (index >= 0)
               _state.Notes[index] = incomingNote;

            var open = _windows.FirstOrDefault(w => string.Equals(w.NoteId, incomingNote.Id, StringComparison.OrdinalIgnoreCase));
            if (open != null)
            {
               _suppressAutoSave = true;
               try
               {
                  try { open.Close(); } catch { }
                  _windows.Remove(open);
               }
               finally
               {
                  _suppressAutoSave = false;
               }
            }

            SpawnWindow(incomingNote);
            JsonStore.Save(_state);
            message = "Import complete. Updated existing note from imported note.";
            return true;
         }
         catch (Exception ex)
         {
            message = $"Import failed: {ex.Message}";
            return false;
         }
      }

      private bool TrySyncCore(SyncCommand command, out string message)
      {
         _saveTimer.Stop();
         FlushPendingUiEdits();
         PersistAllWindows();

         if (!Preferences.SyncEnabled)
         {
            message = "Sync is disabled. Enable it in Preferences > Sync.";
            return false;
         }

         var syncPath = ResolveSyncPath(Preferences.SyncFilePath);
         if (string.IsNullOrWhiteSpace(syncPath))
         {
            message = "Sync file is not set. Choose a .3m sync file in Preferences > Sync (local path or cloud-synced folder).";
            return false;
         }

         if (!string.Equals(_state.Preferences.SyncFilePath, syncPath, StringComparison.Ordinal))
            _state.Preferences.SyncFilePath = syncPath;

         try
         {
            var localSyncState = BuildSyncState(Preferences.SyncPreferences);
            var localStamp = SyncStore.GetNotesModifiedUtc(localSyncState);
            var localDeviceId = EnsureSyncDeviceId();
            var pulledFromRemote = false;
            var remoteExists = System.IO.File.Exists(syncPath);

            if (command == SyncCommand.Push)
            {
               SyncStore.Save(syncPath, localSyncState, localDeviceId);
               message = "Push complete. Wrote current notes to sync file.";
               return MarkSyncSuccessAndSave(message);
            }

            if (!remoteExists)
            {
               if (command == SyncCommand.Pull)
               {
                  message = "Pull failed: sync file does not exist yet.";
                  return false;
               }

               SyncStore.Save(syncPath, localSyncState, localDeviceId);
               message = "Sync complete. Created sync file from current notes.";
               return MarkSyncSuccessAndSave(message);
            }

            var remoteDocument = SyncStore.LoadDocument(syncPath);
            var remoteState = remoteDocument.State;
            var remoteStamp = SyncStore.GetNotesModifiedUtc(remoteState);

            if (command == SyncCommand.Pull)
            {
               if (!ConfirmReplacePullIfNeeded(Preferences.SyncImportMode))
               {
                  message = "Pull canceled.";
                  return false;
               }

               ApplyRemoteSyncState(remoteState, Preferences.SyncPreferences, Preferences.SyncImportMode);
               message = "Pull complete. Loaded notes from sync file.";
               return MarkSyncSuccessAndSave(message);
            }

            var shouldPull = ShouldPullFromRemote(localStamp, remoteStamp, remoteDocument.DeviceId, localDeviceId);

            if (shouldPull)
            {
               if (!ConfirmReplacePullIfNeeded(Preferences.SyncImportMode))
               {
                  message = "Sync canceled.";
                  return false;
               }

               ApplyRemoteSyncState(remoteState, Preferences.SyncPreferences, Preferences.SyncImportMode);
               pulledFromRemote = true;
            } else
            {
               SyncStore.Save(syncPath, localSyncState, localDeviceId);
            }

            message = pulledFromRemote
               ? "Sync complete. Pulled newer notes from sync file."
               : "Sync complete. Pushed current notes to sync file.";
            return MarkSyncSuccessAndSave(message);
         }
         catch (Exception ex)
         {
            message = $"Sync failed: {ex.Message}";
            return false;
         }
      }

      private bool ShouldPullFromRemote(DateTime localStamp, DateTime remoteStamp, string remoteDeviceId, string localDeviceId)
      {
         return Preferences.SyncMode switch
         {
            SyncMode.AlwaysPull => true,
            SyncMode.AlwaysPush => false,
            SyncMode.PreferPullFromOtherDevice =>
               !string.IsNullOrWhiteSpace(remoteDeviceId)
               && !string.Equals(remoteDeviceId, localDeviceId, StringComparison.OrdinalIgnoreCase)
               ? true
               : remoteStamp > localStamp,
            _ => remoteStamp > localStamp
         };
      }

      private bool MarkSyncSuccessAndSave(string message)
      {
         _state.Preferences.LastSyncUtc = DateTime.UtcNow;
         JsonStore.Save(_state);
         return true;
      }

      private bool ConfirmReplacePullIfNeeded(SyncImportMode importMode)
      {
         if (importMode != SyncImportMode.ReplaceCurrentNotes)
            return true;

         if (!_state.Preferences.WarnBeforeReplaceOnPull)
            return true;

         if (_state.Notes.Count == 0)
            return true;

         Window? owner = _preferencesWindow?.IsLoaded == true
            ? _preferencesWindow
            : _windows.FirstOrDefault(w => w.IsLoaded);

         var (confirmed, dontWarnAgain) = ShowReplacePullConfirmation(owner);
         if (dontWarnAgain)
         {
            _state.Preferences.WarnBeforeReplaceOnPull = false;
            JsonStore.Save(_state);
         }

         return confirmed;
      }

      private static (bool confirmed, bool dontWarnAgain) ShowReplacePullConfirmation(Window? owner)
      {
         var dialog = new Window
         {
            Title = "Confirm replace",
            Width = 420,
            Height = 190,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ShowInTaskbar = false
         };

         var root = new System.Windows.Controls.Grid { Margin = new Thickness(12) };
         root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
         root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
         root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

         var text = new System.Windows.Controls.TextBlock
         {
            Text = "This will replace all current notes with synced notes. Continue?",
            TextWrapping = TextWrapping.Wrap
         };
         System.Windows.Controls.Grid.SetRow(text, 0);
         root.Children.Add(text);

         var dontWarn = new System.Windows.Controls.CheckBox
         {
            Margin = new Thickness(0, 10, 0, 8),
            Content = "Don't warn again"
         };
         System.Windows.Controls.Grid.SetRow(dontWarn, 1);
         root.Children.Add(dontWarn);

         var buttons = new System.Windows.Controls.StackPanel
         {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
         };

         var cancel = new System.Windows.Controls.Button { Content = "Cancel", MinWidth = 75, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
         var replace = new System.Windows.Controls.Button { Content = "Replace", MinWidth = 75, IsDefault = true };

         cancel.Click += (_, __) => dialog.DialogResult = false;
         replace.Click += (_, __) => dialog.DialogResult = true;

         buttons.Children.Add(cancel);
         buttons.Children.Add(replace);
         System.Windows.Controls.Grid.SetRow(buttons, 2);
         root.Children.Add(buttons);

         dialog.Content = root;
         var result = dialog.ShowDialog() == true;
         return (result, dontWarn.IsChecked == true);
      }

      private string EnsureSyncDeviceId()
      {
         if (!string.IsNullOrWhiteSpace(_state.Preferences.SyncDeviceId))
            return _state.Preferences.SyncDeviceId;

         _state.Preferences.SyncDeviceId = Guid.NewGuid().ToString("N");
         return _state.Preferences.SyncDeviceId;
      }

      private static string ResolveSyncPath(string? rawPath)
      {
         if (string.IsNullOrWhiteSpace(rawPath))
            return string.Empty;

         var path = rawPath.Trim().Trim('"');
         if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

         path = Environment.ExpandEnvironmentVariables(path);

         if (path.StartsWith("~\\", StringComparison.Ordinal))
         {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = System.IO.Path.Combine(home, path[2..]);
         }

         try
         {
            return System.IO.Path.GetFullPath(path);
         }
         catch
         {
            return path;
         }
      }


      public void QueueSaveFromWindow() => QueueSave(350);

      public void QueueTextSaveFromWindow() => QueueSave(700);
      public void QueueUndoRedoSaveFromWindow() => QueueSave(1300);

      public void RestoreHiddenNotes()
      {
         foreach (var w in _windows.Where(w => w.IsLoaded))
            w.SetIsMinimized(false);
         QueueSave(300);
      }

      public void ShowPreferences()
      {
         if (_preferencesWindow == null || !_preferencesWindow.IsLoaded)
         {
            _preferencesWindow = new PreferencesWindow(Preferences);
            _preferencesWindow.Owner = _windows.FirstOrDefault(w => w.IsLoaded);
            _preferencesWindow.Closed += (_, __) => _preferencesWindow = null;
            _preferencesWindow.Show();
            return;
         }

         _preferencesWindow.Activate();
      }

      public void ApplyPreferences(AppPreferences preferences, bool persist)
      {
         _state.Preferences = preferences ?? new AppPreferences();
         AppThemeService.ApplyTheme(_state.Preferences.DarkMode);
         UpdateTrayIcon();
         UpdateRunOnStartup();
         ApplyPreferencesToWindows();

         if (persist)
            JsonStore.Save(_state);
      }

      public void ApplyLeadingPreferenceToNotes(double multiplier)
      {
         var normalized = Math.Max(0.8, Math.Min(2.5, multiplier));
         foreach (var w in _windows.Where(w => w.IsLoaded))
         {
            w.ApplyLeadingPreference(normalized);
         }

         QueueSave(400);
      }

      private void ApplyPreferencesToWindows()
      {
         foreach (var w in _windows.ToArray().Where(w => w.IsLoaded))
         {
            w.ShowInTaskbar = Preferences.ShowTaskbarIcon;
            w.ApplyPreferences(Preferences);
            ClampToPreferredArea(w);
         }
      }

      private void UpdateRunOnStartup()
      {
         try
         {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(exePath))
               StartupRegistryService.SetRunOnStartup(Preferences.RunOnStartup, exePath);
         }
         catch
         {
            // best-effort
         }
      }

      private void UpdateTrayIcon()
      {
         if (!Preferences.ShowTrayIcon)
         {
            _tray?.Dispose();
            _tray = null;
            return;
         }

         if (_tray != null)
            return;

         var iconPath = System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "Properties",
            "Icons",
          "re_stickit.ico");

         if (!System.IO.File.Exists(iconPath))
            return;

         try
         {
            var icon = new System.Drawing.Icon(iconPath);

            _tray = new StickIt.Services.TrayIconService(
               icon,
               () => Dispatcher.BeginInvoke(new Action(() => CreateNewNoteNear(null))),
               () => Dispatcher.BeginInvoke(new Action(MinimizeAllNotes)),
               () => Dispatcher.BeginInvoke(new Action(RestoreAllNotes)),
               () => Dispatcher.BeginInvoke(new Action(SaveAllNotesNow)),
               () => Dispatcher.BeginInvoke(new Action(ShowAllNotes)),
               () => Dispatcher.BeginInvoke(new Action(ShutdownRequested))
            );
         }
         catch
         {
            // Best-effort tray icon initialization.
         }
      }

      public void ShowNoteManager()
      {
         if (_noteManagerWindow == null || !_noteManagerWindow.IsLoaded)
         {
            _noteManagerWindow = new NoteManagerWindow();
            _noteManagerWindow.Owner = _windows.FirstOrDefault(w => w.IsLoaded);
            _noteManagerWindow.Closed += (_, __) => _noteManagerWindow = null;
            _noteManagerWindow.Show();
            return;
         }

         _noteManagerWindow.Activate();
      }

      public IReadOnlyList<NoteWindow> GetOpenWindowsSnapshot()
      {
         return _windows.Where(w => w.IsLoaded).ToList();
      }

      public void UpdateDefaultBodyFont(string fontFamily, double fontSize)
      {
         _state.Preferences.BodyFontFamily = string.IsNullOrWhiteSpace(fontFamily) ? _state.Preferences.BodyFontFamily : fontFamily;
         _state.Preferences.BodyFontSize = fontSize > 0 ? fontSize : _state.Preferences.BodyFontSize;
         JsonStore.Save(_state);
      }

      public void ApplyFontSettingsToAllOpenNotes(FontSettingsData settings, NoteWindow? excludeWindow = null)
      {
         foreach (var window in _windows.Where(w => w.IsLoaded && !ReferenceEquals(w, excludeWindow)))
            window.ApplyFontSettingsToEntireNote(settings);

         QueueSave(700);
      }


      // GEMINI IN

      private void SpawnWindow(NotePersist note)
      {
         // Build the model from persisted state (canonical)
         //var model = new StickIt.Models.NoteModel();
         var model = NotePersistMapper.ToModel(note);


         // Identity + metadata
         model.Props.Id = note.Id;

         model.Title = note.Title;

         if (Enum.TryParse(note.ColorKey, out NoteColors.NoteColor ck))
            model.ColorKey = ck;
         else
            model.ColorKey = NoteColors.NoteColor.ThreeMYellow;

         model.FontFamily = note.FontFamily;
         model.FontSize = note.FontSize;

         // Create window
         var w = new NoteWindow(model);
         w.ShowInTaskbar = Preferences.ShowTaskbarIcon;
         w.ApplyPreferences(Preferences);

         w.SetStickyTarget(note.StickyTargetPersist);

         // Geometry first
         w.Width = note.Width <= 0 ? 380 : note.Width;
         w.Height = note.Height <= 0 ? 320 : note.Height;
         w.Left = note.Left;
         w.Top = note.Top;

         var restoredToMonitor = TryRestoreToSavedMonitor(note, w);
         if (!restoredToMonitor)
            ClampToVirtualScreen(w);
         ClampToPreferredArea(w);

         // Content (prefer RTF)
         w.SetRtf(note.Rtf ?? RtfCodec.FromPlainText(note.Text, note.FontSize));
         w.SetInkIsfBase64(note.InkIsfBase64);

         // Sticky + minimized
         w.SetStuckMode(note.StuckMode);

         w.Loaded += (_, __) =>
         {
            if (note.IsMinimized)
               w.SetIsMinimized(true);
         };

         // Autosave triggers
         w.LocationChanged += (_, __) => QueueSave(300);
         w.StateChanged += (_, __) => QueueSave(300);
         w.NoteTextChanged += (_, __) => QueueSave(700);
         w.NoteUndoRedoRequested += (_, __) => QueueSave(1300);
         w.Closed += (_, __) =>
         {
            if (_isShuttingDown)
               return; // shutdown path: don't touch _windows (and don't trigger saves)

            _windows.Remove(w);

            if (_suppressAutoSave)
               return;

            QueueSave(300);

            // Leave app running when last note is deleted.
            if (_windows.Count == 0)
            {
               NotifyStillRunningIfLastNoteClosed();

               // Optional: keep user from ever having “zero notes”
               // CreateNewNoteNear(null);
            }

         };

         _windows.Add(w);
         w.Show();
      }


      // 1. Add this new unified helper method
      private static void ClampWindowToRect(NoteWindow w, double left, double top, double width, double height, double margin)
      {
         double right = left + width;
         double bottom = top + height;

         if (w.Left + w.Width > right - margin)
            w.Left = right - w.Width - margin;
         if (w.Top + w.Height > bottom - margin)
            w.Top = bottom - w.Height - margin;

         if (w.Left < left + margin)
            w.Left = left + margin;
         if (w.Top < top + margin)
            w.Top = top + margin;

         if (w.Width > width - (margin * 2))
            w.Left = left + margin;
         if (w.Height > height - (margin * 2))
            w.Top = top + margin;
      }

      // 2. Replace your existing ClampToVirtualScreen
      private static void ClampToVirtualScreen(NoteWindow w)
      {
         ClampWindowToRect(w,
             SystemParameters.VirtualScreenLeft,
             SystemParameters.VirtualScreenTop,
             SystemParameters.VirtualScreenWidth,
             SystemParameters.VirtualScreenHeight,
             20);
      }

      // 3. Replace your existing ClampToWorkingArea
      private static void ClampToWorkingArea(NoteWindow w, System.Windows.Forms.Screen screen)
      {
         var wa = screen.WorkingArea;
         var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(w);
         double scaleX = Math.Max(0.01, dpi.DpiScaleX);
         double scaleY = Math.Max(0.01, dpi.DpiScaleY);

         ClampWindowToRect(w,
             wa.Left / scaleX,
             wa.Top / scaleY,
             wa.Width / scaleX,
             wa.Height / scaleY,
             20);
      }

      // 4. Replace your existing ClampToPreferredArea
      private bool ClampToPreferredArea(NoteWindow w)
      {
         if (!Preferences.KeepNotesInsideDesktopArea ||
             Preferences.DesktopAreaLeft is null || Preferences.DesktopAreaTop is null ||
             Preferences.DesktopAreaWidth is null || Preferences.DesktopAreaHeight is null)
            return false;

         double width = Preferences.DesktopAreaWidth.Value;
         double height = Preferences.DesktopAreaHeight.Value;

         if (width <= 0 || height <= 0)
            return false;

         ClampWindowToRect(w,
             Preferences.DesktopAreaLeft.Value,
             Preferences.DesktopAreaTop.Value,
             width,
             height,
             10);

         return true;
      }

      // GEMINI OUT


      private void QueueSave(int delayMs)
      {
         if (_suppressAutoSave)
            return;

         delayMs = Math.Max(50, delayMs);
         var due = DateTime.UtcNow.AddMilliseconds(delayMs);
         if (!_pendingSaveDueUtc.HasValue || due > _pendingSaveDueUtc.Value)
            _pendingSaveDueUtc = due;

         var remaining = (_pendingSaveDueUtc.Value - DateTime.UtcNow);
         if (remaining < TimeSpan.FromMilliseconds(10))
            remaining = TimeSpan.FromMilliseconds(10);

         _saveTimer.Interval = remaining;
         _saveTimer.Stop();
         _saveTimer.Start();
      }

      private static bool TryRestoreToSavedMonitor(NotePersist note, NoteWindow w)
      {
         var screen = MonitorAffinityService.TryResolveForPersist(note);
         if (screen == null)
            return false;

         var sameMonitor = !string.IsNullOrWhiteSpace(note.MonitorDeviceName)
            && string.Equals(note.MonitorDeviceName, screen.DeviceName, StringComparison.OrdinalIgnoreCase);

         if (sameMonitor)
         {
            w.Left = note.Left;
            w.Top = note.Top;
            ClampToWorkingArea(w, screen);
            return true;
         }

         if (note.MonitorWorkAreaWidth is null || note.MonitorWorkAreaHeight is null ||
            note.MonitorWorkAreaLeft is null || note.MonitorWorkAreaTop is null)
            return false;

         var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(w);
         double scaleX = Math.Max(0.01, dpi.DpiScaleX);
         double scaleY = Math.Max(0.01, dpi.DpiScaleY);

         double oldLeft = note.MonitorWorkAreaLeft.Value / scaleX;
         double oldTop = note.MonitorWorkAreaTop.Value / scaleY;
         double oldW = Math.Max(1.0, note.MonitorWorkAreaWidth.Value / scaleX);
         double oldH = Math.Max(1.0, note.MonitorWorkAreaHeight.Value / scaleY);
         double rx = (note.Left - oldLeft) / oldW;
         double ry = (note.Top - oldTop) / oldH;

         rx = Math.Max(0.0, Math.Min(1.0, rx));
         ry = Math.Max(0.0, Math.Min(1.0, ry));

         var wa = screen.WorkingArea;
         double waLeft = wa.Left / scaleX;
         double waTop = wa.Top / scaleY;
         double waWidth = wa.Width / scaleX;
         double waHeight = wa.Height / scaleY;

         w.Left = waLeft + (rx * Math.Max(1, waWidth - w.Width));
         w.Top = waTop + (ry * Math.Max(1, waHeight - w.Height));

         ClampToWorkingArea(w, screen);
         return true;
      }

      private void OnDisplaySettingsChanged(object? sender, EventArgs e)
      {
         Dispatcher.BeginInvoke(() =>
         {
            foreach (var w in _windows.Where(x => x.IsLoaded && x.WindowState != WindowState.Minimized))
            {
               if (!ClampToPreferredArea(w))
               {
                  var screen = MonitorAffinityService.GetScreenForWindow(w);
                  ClampToWorkingArea(w, screen);
               }
            }

            QueueSave(400);
         });
      }

      private void PersistAllWindows()
      {
         var windows = _windows.ToArray();

         _state.Notes = windows
             .Select(w => NotePersistMapper.FromWindow(
                 w,
                 w.GetStuckMode(),
                 DateTime.UtcNow))
             .ToList();

         JsonStore.Save(_state);
      }

      private StickItState BuildSyncState(bool includePreferences)
      {
         return new StickItState
         {
            Version = _state.Version,
            Notes = _state.Notes.ToList(),
            Preferences = includePreferences
               ? ClonePreferencesForSync(_state.Preferences)
               : new AppPreferences()
         };
      }

      private void ApplyRemoteSyncState(StickItState remoteState, bool includePreferences, SyncImportMode importMode)
      {
         var currentSyncSettings = CloneSyncSettings(_state.Preferences);

         _suppressAutoSave = true;
         try
         {
            foreach (var w in _windows.ToArray())
            {
               try { w.Close(); }
               catch { }
            }

            _windows.Clear();
            _state.Notes = ComposeImportedNotes(
               _state.Notes ?? new List<NotePersist>(),
               remoteState.Notes ?? new List<NotePersist>(),
               importMode);

            if (includePreferences)
               _state.Preferences = remoteState.Preferences ?? new AppPreferences();

            RestoreSyncSettings(_state.Preferences, currentSyncSettings);
            ApplyPreferences(_state.Preferences, persist: false);

            foreach (var note in _state.Notes)
               SpawnWindow(note);
         }
         finally
         {
            _suppressAutoSave = false;
         }
      }

      private static AppPreferences ClonePreferencesForSync(AppPreferences source)
      {
         return new AppPreferences
         {
            RunOnStartup = source.RunOnStartup,
            DarkMode = source.DarkMode,
            ShowTaskbarIcon = source.ShowTaskbarIcon,
            ShowTrayIcon = source.ShowTrayIcon,
            AlwaysStickNewNotesToDesktop = source.AlwaysStickNewNotesToDesktop,
            SnapNotesToGrid = source.SnapNotesToGrid,
            KeepNotesInsideDesktopArea = source.KeepNotesInsideDesktopArea,
            DesktopAreaLeft = source.DesktopAreaLeft,
            DesktopAreaTop = source.DesktopAreaTop,
            DesktopAreaWidth = source.DesktopAreaWidth,
            DesktopAreaHeight = source.DesktopAreaHeight,
            ConfirmOnDelete = source.ConfirmOnDelete,
            HideNotesOnShowDesktop = source.HideNotesOnShowDesktop,
            TreatNotesAsTopLevelWindows = source.TreatNotesAsTopLevelWindows,
            Mode2PreventManualMove = source.Mode2PreventManualMove,
            Mode2MinimizeWithHost = source.Mode2MinimizeWithHost,
            Mode2CloseNoteWhenHostCloses = source.Mode2CloseNoteWhenHostCloses,
            Mode2HostMissingAction = source.Mode2HostMissingAction,
            TitleFontFamily = source.TitleFontFamily,
            TitleFontSize = source.TitleFontSize,
            TitleFontBold = source.TitleFontBold,
            BodyFontFamily = source.BodyFontFamily,
            BodyFontSize = source.BodyFontSize,
            BodyLineHeightMultiplier = source.BodyLineHeightMultiplier,
            ShowDateAlongTitle = source.ShowDateAlongTitle,
            EnableDropShadow = source.EnableDropShadow,
            EnableNoteBorders = source.EnableNoteBorders,
            EnableExternalNoteImportExport = source.EnableExternalNoteImportExport,
            EnableAutoListFormatting = source.EnableAutoListFormatting,
            AutoListBulletSymbol = source.AutoListBulletSymbol,
            AutoListSpacesAfterMarker = source.AutoListSpacesAfterMarker,
            AutoListNumberSuffix = source.AutoListNumberSuffix,
            AutoListBulletTemplateRtf = source.AutoListBulletTemplateRtf,
            AutoListNumberTemplateRtf = source.AutoListNumberTemplateRtf,
            EnableTodoTitleTrigger = source.EnableTodoTitleTrigger,
            WarnBeforeReplaceOnPull = source.WarnBeforeReplaceOnPull
         };
      }

      private static AppPreferences CloneSyncSettings(AppPreferences source)
      {
         return new AppPreferences
         {
            SyncEnabled = source.SyncEnabled,
            SyncFilePath = source.SyncFilePath,
            SyncPreferences = source.SyncPreferences,
            SyncMode = source.SyncMode,
            SyncImportMode = source.SyncImportMode,
            SyncDeviceId = source.SyncDeviceId,
            LastSyncUtc = source.LastSyncUtc
         };
      }

      private static void RestoreSyncSettings(AppPreferences target, AppPreferences syncSettings)
      {
         target.SyncEnabled = syncSettings.SyncEnabled;
         target.SyncFilePath = syncSettings.SyncFilePath;
         target.SyncPreferences = syncSettings.SyncPreferences;
         target.SyncMode = syncSettings.SyncMode;
         target.SyncImportMode = syncSettings.SyncImportMode;
         target.SyncDeviceId = syncSettings.SyncDeviceId;
         target.LastSyncUtc = syncSettings.LastSyncUtc;
      }

      private static List<NotePersist> ComposeImportedNotes(List<NotePersist> localNotes, List<NotePersist> remoteNotes, SyncImportMode importMode)
      {
         if (importMode == SyncImportMode.ReplaceCurrentNotes)
            return remoteNotes.ToList();

         var byId = new Dictionary<string, NotePersist>(StringComparer.OrdinalIgnoreCase);

         foreach (var local in localNotes)
         {
            if (string.IsNullOrWhiteSpace(local.Id))
               local.Id = Guid.NewGuid().ToString("N");

            if (!byId.ContainsKey(local.Id))
               byId[local.Id] = local;
         }

         foreach (var remote in remoteNotes)
         {
            if (string.IsNullOrWhiteSpace(remote.Id))
               remote.Id = Guid.NewGuid().ToString("N");

            if (!byId.TryGetValue(remote.Id, out var existing))
            {
               byId[remote.Id] = remote;
               continue;
            }

            if (importMode == SyncImportMode.MergeByNoteIdNewestWins && remote.ModifiedUtc >= existing.ModifiedUtc)
               byId[remote.Id] = remote;
         }

         return byId.Values.ToList();
      }

      private enum SyncCommand
      {
         Auto,
         Pull,
         Push
      }



      private void PersistAllWindowsOnShutdown()
      {
         var windows = _windows.ToArray();

         _state.Notes = windows
            .Select(w => NotePersistMapper.FromWindow(
               w,
               w.GetStuckMode(),
               DateTime.UtcNow))
            .ToList();

         JsonStore.Save(_state);
      }





      public void CreateNewNoteNear(NoteWindow? anchor = null)
      {
         const double newW = 400;
         const double newH = 400;

         double desiredLeft, desiredTop;

         if (anchor != null)
         {
            desiredLeft = anchor.Left + 20;
            desiredTop = anchor.Top + 20;
         } else
         {
            desiredLeft = _nextSpawnLeft;
            desiredTop = _nextSpawnTop;

            _nextSpawnLeft += 24;
            _nextSpawnTop += 24;
         }

         var pos = FindNonOverlappingPosition(desiredLeft, desiredTop, newW, newH);


         var p = new NotePersist
         {
            Left = pos.X,
            Top = pos.Y,
            Width = newW,
            Height = newH,
            Title = "Untitled",
            Text = "",
            ColorKey = nameof(NoteColors.NoteColor.ThreeMYellow),
            StuckMode = 0,
            IsMinimized = false,
            CreatedUtc = DateTime.UtcNow,
            ModifiedUtc = DateTime.UtcNow,
            FontFamily = Preferences.BodyFontFamily,
            FontSize = Preferences.BodyFontSize,
         };

         if (Preferences.AlwaysStickNewNotesToDesktop)
         {
            var desktop = DesktopTargetService.TryGetDesktopTarget();
            if (desktop != null)
            {
               p.StuckMode = 2;
               p.StickyTargetPersist = new StickyTargetPersist
               {
                  ProcessId = desktop.ProcessId,
                  ProcessName = desktop.ProcessName,
                  WindowTitle = desktop.WindowTitle,
                  ClassName = desktop.ClassName,
                  CapturedUtc = desktop.CapturedUtc
               };
            }
         }


         // Add to state so it exists even if we crash before debounce tick
         _state.Notes.Add(p);
         JsonStore.Save(_state);

         SpawnWindow(p);
      }

   }
}
