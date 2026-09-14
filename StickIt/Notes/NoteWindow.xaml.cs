using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Ink;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

using StickIt.Converters;
using StickIt.Models;
using StickIt.Persistence;
using StickIt.Services;
using StickIt.Sticky.Services;

using WpfApplication = System.Windows.Application;
using WpfDataFormats = global::System.Windows.DataFormats;
using WpfDataObject = global::System.Windows.DataObject;

namespace StickIt
{
   public partial class NoteWindow : Window, INotifyPropertyChanged
   {
      private readonly NoteModel? _note;

      private int _noteStuckMode = 0;
      private double? _stickyOffsetXPx;
      private double? _stickyOffsetYPx;
      private double? _stickyTargetAnchorXPx;
      private double? _stickyTargetAnchorYPx;

      private readonly DispatcherTimer _followTimer = new DispatcherTimer();
      private double? _lastTargetX;
      private double? _lastTargetY;

      private StickIt.Sticky.Services.StickyWinEventHookService? _winEventHook;
      private bool _winEventSubscribed;

      private DispatcherTimer? _stickyCoalesceTimer;
      private bool _stickySnapPending;
      private bool _isShuttingDown;

      private IntPtr _prevWin32Owner = IntPtr.Zero;

      private static double PointsToDip(double pt) => pt * 96.0 / 72.0; // pt * 4/3
      private static double DipToPoints(double dip) => dip * 72.0 / 96.0;

      private static double NormalizeFontSize(double size, double fallback = 14.0)
         => size > 0 ? size : fallback;

      private static double NormalizeLineHeightMultiplier(double value, double fallback = 1.0)
         => value > 0 ? Math.Max(0.8, Math.Min(2.5, value)) : fallback;

      private static System.Windows.Media.FontFamily CreateSafeFontFamily(string? familyName, string fallback = "Segoe UI")
      {
         var preferred = string.IsNullOrWhiteSpace(familyName) ? fallback : familyName;
         try
         {
            return new System.Windows.Media.FontFamily(preferred);
         }
         catch
         {
            return new System.Windows.Media.FontFamily(fallback);
         }
      }

      private bool _suppressTextChanged;
      private bool _suppressAutoListHandling;
      private bool _dropShadowEnabled = true;
      private bool _noteBordersEnabled = true;
      private bool _snapToGridEnabled;
      private bool _externalNoteImportExportEnabled;
      private bool _autoListFormattingEnabled;
      private string _autoListBulletSymbol = "•";
      private int _autoListSpacesAfterMarker = 1;
      private string _autoListNumberSuffix = ".";
      private const int MaxConsecutiveEmptyListEnterBeforeStop = 2;
      private int _consecutiveEmptyListEnterCount;
      private string _autoListBulletTemplateRtf = string.Empty;
      private string _autoListNumberTemplateRtf = string.Empty;
      private bool _enableTodoTitleTrigger;
      private bool _todoTitleArmed;
      private bool _todoTemplateApplied;
      private AutoBulletPairState? _lastAutoBulletPair;
      private bool _snapGridAdjusting;
      private bool _mode2PreventManualMove = true;
      private bool _mode2MinimizeWithHost;
      private bool _mode2CloseNoteWhenHostCloses;
      private Mode2HostMissingAction _mode2HostMissingAction = Mode2HostMissingAction.SwitchToMode1;
      private bool _mode2LiftActive;
      private bool _autoMinimizedByHost;
      private bool _inkModeEnabled;
      private bool _inkEraseModeEnabled;
      private bool _suppressInkChanged;
      private double _inkThicknessLevel = 1.0;
      private System.Windows.Media.Color _inkColor = System.Windows.Media.Colors.Black;
      private bool _inkColorIsCustom;
      private System.Windows.Media.Brush _caretBrush = System.Windows.Media.Brushes.Black;
      private double _lineHeightMultiplier = 1.0;
      private InkToolbarWindow? _inkToolbarWindow;
      private bool _inkToolbarSnapEnabled = true;
      private bool _suppressToolbarMoveHandling;
      private InkToolbarDock _inkToolbarDock = InkToolbarDock.Right;
      private double _inkToolbarDockOffset = 8;
      private const double InkToolbarGap = 8.0;
      private const double InkToolbarSnapDistance = 24.0;
      private const double SnapGridSizeDefault = 20.0;
      private const double SnapGridSizeLowResolution = 16.0;
      private string _todoTemplateRtf = string.Empty;
      private static readonly Regex BulletTriggerRegex = new(@"^(\s*)([-*+])\s*(.*)$", RegexOptions.Compiled);
      private static readonly Regex NumberTriggerRegex = new(@"^(\s*)(\d+)([\.)])?\s*(.*)$", RegexOptions.Compiled);
      private static readonly Regex HashTriggerRegex = new(@"^(\s*)#\s*(.*)$", RegexOptions.Compiled);

      private enum InkToolbarDock
      {
         Left,
         Right,
         Top,
         Bottom
      }

      private void TxtNoteContent_TextChanged(object sender, TextChangedEventArgs e)
      {
         if (_suppressTextChanged)
            return;

         //CleanupStrikethroughOnEmptyParagraphs();

         if (_autoListFormattingEnabled)
            TryApplyAutomaticListFormatting();

         NoteTextChanged?.Invoke(this, EventArgs.Empty);
      }

      private void TxtNoteTitle_TextChanged(object sender, TextChangedEventArgs e)
      {
         if (!_enableTodoTitleTrigger || _note == null)
            return;

         var title = txtNoteTitle?.Text ?? string.Empty;
         var hasTodoKeyword = title.Contains("TODO", StringComparison.OrdinalIgnoreCase);

         if (!hasTodoKeyword)
         {
            _todoTemplateApplied = false;
            return;
         }

         if (_todoTemplateApplied)
            return;

         if (!string.IsNullOrWhiteSpace(GetText()))
            return;

         // Apply the custom RTF template if one exists
         if (!string.IsNullOrWhiteSpace(_todoTemplateRtf))
         {
            SetRtf(_todoTemplateRtf);
         } else
         {
            // Fallback just in case the template is completely empty
            var spaces = new string(' ', Math.Max(1, _autoListSpacesAfterMarker));
            SetText($"{_autoListBulletSymbol}{spaces}[ ] Task 1\n{_autoListBulletSymbol}{spaces}[ ] Task 2\n{_autoListBulletSymbol}{spaces}[ ] Task 3");
         }

         _todoTemplateApplied = true;
         AppInstance.QueueTextSaveFromWindow();
      }

      private sealed class AutoBulletPairState
      {
         public Paragraph? First { get; init; }
         public Paragraph? Second { get; init; }
         public char Marker { get; init; }
      }

      private sealed class ListTemplateStyle
      {
         public System.Windows.Media.FontFamily FontFamily { get; set; } = new System.Windows.Media.FontFamily("Segoe UI");
         public double FontSize { get; set; } = 14d;
         public FontWeight FontWeight { get; set; } = FontWeights.Normal;
         public System.Windows.FontStyle FontStyle { get; set; } = FontStyles.Normal;
         public TextDecorationCollection? TextDecorations { get; set; }
         public System.Windows.Media.Brush? Foreground { get; set; }
         public Thickness Margin { get; set; } = new Thickness(0);
         public double TextIndent { get; set; }
      }



      public event PropertyChangedEventHandler? PropertyChanged;
      private void OnPropertyChanged([CallerMemberName] string? name = null) =>
         PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

      public string NoteId { get; private set; } = Guid.NewGuid().ToString("N");
      public NoteColors.NoteColor GetColorKey() => _note?.ColorKey ?? NoteColors.NoteColor.ThreeMYellow;
      public string GetTitle() => _note?.Title ?? "Untitled";

      private System.Windows.Point GetNoteTopLeftPx()
      {
         if (!CanUseVisualTree())
            return new System.Windows.Point(0, 0);

         var hwnd = new WindowInteropHelper(this).Handle;
         if (hwnd != IntPtr.Zero && WindowRectService.TryGetWindowRect(hwnd, out var rect))
            return new System.Windows.Point(rect.X, rect.Y);

         var dip = PointToScreen(new System.Windows.Point(0, 0));
         var dpi = VisualTreeHelper.GetDpi(this);
         return new System.Windows.Point(dip.X * Math.Max(0.01, dpi.DpiScaleX), dip.Y * Math.Max(0.01, dpi.DpiScaleY));
      }

      private bool CanUseVisualTree()
         => !_isShuttingDown && IsLoaded && PresentationSource.FromVisual(this) != null;

      private System.Windows.Point GetNoteCenterPx()
      {
         var topLeft = GetNoteTopLeftPx();
         var dpi = VisualTreeHelper.GetDpi(this);
         var halfWidthPx = Math.Max(1, Width * Math.Max(0.01, dpi.DpiScaleX)) / 2.0;
         var halfHeightPx = Math.Max(1, Height * Math.Max(0.01, dpi.DpiScaleY)) / 2.0;
         return new System.Windows.Point(topLeft.X + halfWidthPx, topLeft.Y + halfHeightPx);
      }

      private void UpdateStickyTargetAnchorFromCurrentNote()
      {
         var center = GetNoteCenterPx();
         _stickyTargetAnchorXPx = center.X;
         _stickyTargetAnchorYPx = center.Y;
      }

      public string? GetInkIsfBase64()
      {
         if (inkLayer == null || inkLayer.Strokes.Count == 0)
            return null;

         try
         {
            using var ms = new MemoryStream();
            inkLayer.Strokes.Save(ms);
            if (ms.Length == 0)
               return null;

            return Convert.ToBase64String(ms.ToArray());
         }
         catch
         {
            return null;
         }
      }



      private bool RevertToNotStuck()
      {
         ApplyStuckMode(0);
         _stickyTarget = null;
         _stickyOffsetXPx = null;
         _stickyOffsetYPx = null;
         _stickyTargetAnchorXPx = null;
         _stickyTargetAnchorYPx = null;
         StopHook();
         AppInstance.QueueSaveFromWindow();
         return false;
      }

      [DllImport("user32.dll")]
      private static extern bool IsWindow(IntPtr hWnd);

      [DllImport("user32.dll")]
      private static extern bool IsIconic(IntPtr hWnd);

      private bool IsNoteVisibleOnAnyScreen()
      {
         var hwnd = new WindowInteropHelper(this).Handle;
         if (hwnd == IntPtr.Zero || !WindowRectService.TryGetWindowRect(hwnd, out var rect))
            return true;

         var noteRect = new System.Drawing.Rectangle(
            (int)Math.Round(rect.X),
            (int)Math.Round(rect.Y),
            (int)Math.Max(1, Math.Round(rect.Width)),
            (int)Math.Max(1, Math.Round(rect.Height)));

         return System.Windows.Forms.Screen.AllScreens.Any(s =>
            System.Drawing.Rectangle.Intersect(noteRect, s.Bounds).Width > 20
            && System.Drawing.Rectangle.Intersect(noteRect, s.Bounds).Height > 20);
      }

      private void CenterOnPrimaryScreen()
      {
         var wa = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea
                  ?? System.Windows.Forms.SystemInformation.VirtualScreen;

         var dpi = VisualTreeHelper.GetDpi(this);
         var scaleX = Math.Max(0.01, dpi.DpiScaleX);
         var scaleY = Math.Max(0.01, dpi.DpiScaleY);

         var waLeftDip = wa.Left / scaleX;
         var waTopDip = wa.Top / scaleY;
         var waWidthDip = wa.Width / scaleX;
         var waHeightDip = wa.Height / scaleY;

         Left = waLeftDip + ((waWidthDip - Width) / 2.0);
         Top = waTopDip + ((waHeightDip - Height) / 2.0);
      }

      public void SetInkIsfBase64(string? inkIsfBase64)
      {
         if (inkLayer == null)
            return;

         WithSuppressedInkChanged(() =>
         {
            inkLayer.Strokes.Clear();

            if (string.IsNullOrWhiteSpace(inkIsfBase64))
               return;

            try
            {
               var bytes = Convert.FromBase64String(inkIsfBase64);
               using var ms = new MemoryStream(bytes);
               inkLayer.Strokes = new StrokeCollection(ms);
            }
            catch
            {
               inkLayer.Strokes.Clear();
            }
         });
      }

      public int StuckMode
      {
         get => _noteStuckMode;
         private set
         {
            if (_noteStuckMode == value) return;
            _noteStuckMode = value;
            OnPropertyChanged();
         }
      }

      public void SetFontFamily(string family)
      {
         if (_note != null)
            _note.FontFamily = string.IsNullOrWhiteSpace(family) ? "Helvetica" : family;
      }
      public void SetFontSize(double size)
      {
         if (_note != null)
            _note.FontSize = size <= 0 ? 14.0 : size;
      }

      public bool GetIsLocked() => _note?.IsLocked ?? false;

      public double GetRotationAngle() => _note?.RotationAngle ?? 0;

      public string GetFontFamily() => _note?.FontFamily ?? "Helvetica";
      public double GetFontSize() => _note?.FontSize ?? 14.0;
      public double GetLineHeightMultiplier() => _note?.LineHeightMultiplier ?? _lineHeightMultiplier;
      public System.Windows.Media.Brush CaretBrush
      {
         get => _caretBrush;
         private set
         {
            if (Equals(_caretBrush, value))
               return;
            _caretBrush = value;
            OnPropertyChanged();
         }
      }

      public void SetTitle(string title)
      {
         if (_note != null)
            _note.Title = title ?? "untitled";
      }

      public static readonly RoutedUICommand CmdBold =
   new RoutedUICommand("Bold", "CmdBold", typeof(NoteWindow),
      new InputGestureCollection { new KeyGesture(Key.B, ModifierKeys.Control) });

      public static readonly RoutedUICommand CmdItalic =
         new RoutedUICommand("Italic", "CmdItalic", typeof(NoteWindow),
            new InputGestureCollection { new KeyGesture(Key.I, ModifierKeys.Control) });

      public static readonly RoutedUICommand CmdUnderline =
         new RoutedUICommand("Underline", "CmdUnderline", typeof(NoteWindow),
            new InputGestureCollection { new KeyGesture(Key.U, ModifierKeys.Control) });



      private void RequestStickySnap()
      {
         if (_isShuttingDown || !CanUseVisualTree())
            return;

         if (_stickyCoalesceTimer == null)
         {
            _stickyCoalesceTimer = new System.Windows.Threading.DispatcherTimer(
               System.Windows.Threading.DispatcherPriority.Background,
               Dispatcher);

            // ~60fps. If you want less CPU, change to 33ms (~30fps).
            _stickyCoalesceTimer.Interval = TimeSpan.FromMilliseconds(16);

            _stickyCoalesceTimer.Tick += (s, e) =>
            {
               _stickyCoalesceTimer.Stop();

               if (_isShuttingDown || !CanUseVisualTree()) return;
               if (!_stickySnapPending) return;
               _stickySnapPending = false;

               if (_noteStuckMode != 2) return;
               if (_stickyTarget == null || _stickyTarget.Hwnd == IntPtr.Zero) return;

               SnapToStickyTargetNow();
            };
         }

         _stickySnapPending = true;

         // restart timer (coalesces multiple requests)
         _stickyCoalesceTimer.Stop();
         _stickyCoalesceTimer.Start();
      }

      private bool CanFormat()
      {
         return IsLoaded
                && txtNoteContent != null
                && txtNoteContent.IsEnabled
                && txtNoteContent.IsVisible
                && !txtNoteContent.IsReadOnly;
      }


      public int GetStuckMode() => _noteStuckMode;        // see below
      public void SetStuckMode(int mode) => ApplyStuckMode(mode);

      public bool GetIsMinimized() => this.WindowState == WindowState.Minimized;
      public void SetIsMinimized(bool minimized)
      {
         WindowState = minimized ? WindowState.Minimized : WindowState.Normal;
      }

      public event EventHandler? NoteTextChanged;
      public event EventHandler? NoteUndoRedoRequested;

      public NoteWindow() : this(new NoteModel())
      {
      }

      public NoteWindow(NoteModel note)
      {
         InitializeComponent();

         txtNoteContent.IsInactiveSelectionHighlightEnabled = true;
         WpfDataObject.AddPastingHandler(txtNoteContent, TxtNoteContent_Pasting);

         Loaded += (_, __) => EnsureStickyTargetOnLoad();

         Loaded += (_, __) =>
         {
            // best-effort: resolves HWND once, no hooks, no timers
            TryRebindStickyTarget();
         };

         _followTimer.Interval = TimeSpan.FromMilliseconds(150);
         _followTimer.Tick += (_, __) =>
         {
            if (_noteStuckMode == 2)
               TickMode2Follow();
         };
         _followTimer.Start();

         txtNoteContent.SelectionChanged += txtNoteContent_SelectionChanged;
         LocationChanged += NoteWindow_LocationChanged;
         SizeChanged += NoteWindow_SizeChanged;
         StateChanged += NoteWindow_StateChanged;
         Closing += NoteWindow_Closing;

         CommandBindings.Add(new CommandBinding(CmdBold,
   (_, __) => ToggleBold(),
   (_, e) => e.CanExecute = CanFormat()));

         CommandBindings.Add(new CommandBinding(CmdItalic,
            (_, __) => ToggleItalic(),
            (_, e) => e.CanExecute = CanFormat()));

         CommandBindings.Add(new CommandBinding(CmdUnderline,
            (_, __) => ToggleUnderline(),
            (_, e) => e.CanExecute = CanFormat()));




         txtNoteContent.PreviewKeyDown += (s, e) =>
         {
            // Undo / Redo (Ctrl+Z / Ctrl+Y)
            if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.Z || e.Key == Key.Y))
            {
               NoteUndoRedoRequested?.Invoke(this, EventArgs.Empty);
               // Don't set e.Handled here unless your custom undo completely overrides the default one
            }

            // Soft line break (Shift+Enter)
            if (Keyboard.Modifiers == ModifierKeys.Shift && e.Key == Key.Enter)
            {
               EditingCommands.EnterParagraphBreak.Execute(null, txtNoteContent);
               e.Handled = true;
               return;
            }

            // Strikethrough (Ctrl+Shift+X) - Accepted exception to the Global rule!
            if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.X)
            {
               ToggleStrikethrough();
               e.Handled = true;
               return;
            }
         };



         UpdateStickyVisuals();

         _note = note;
         DataContext = _note;
         _lineHeightMultiplier = _note?.LineHeightMultiplier ?? _lineHeightMultiplier;
         ConfigureInkLayer();
         UpdateCaretBrush();
         UpdateLineSpacing();

         // Keep autosave behavior consistent regardless of constructor use
         txtNoteContent.TextChanged += TxtNoteContent_TextChanged;
         txtNoteTitle.TextChanged += TxtNoteTitle_TextChanged;


         ControlBar.MouseLeftButtonDown += ControlBar_MouseLeftButtonDown;
         btnMinimize.PreviewMouseLeftButtonDown += BtnMinimize_PreviewMouseLeftButtonDown;


         // NEW KEYOBARD SHORTCUTS CODE-BEHIND SECTION!! 
         KeyDown += (_, e) =>
         {
            // 1. Check for Ctrl + Alt + Shift combinations first
            if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift))
            {
               if (e.Key == Key.S)
               {
                  if (_noteStuckMode == 2)
                     Sticky_NotStuck_Click(this, new RoutedEventArgs());
                  else
                     Sticky_Auto_Click(this, new RoutedEventArgs());
                  e.Handled = true;
               }
               return;
            }
            // 2. Check for Ctrl + Shift combinations (GLOBAL ACTIONS)
            if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
               switch (e.Key)
               {
                  case Key.S: // Your existing Pin Cycle
                     btnPinCycle_Click(this, new RoutedEventArgs());
                     e.Handled = true;
                     return;
                  case Key.M: // Minimize All Notes
                     Menu_MinimizeAll(this, new RoutedEventArgs());
                     e.Handled = true;
                     return;
                  case Key.P: // Open Preferences
                     Menu_Preferences(this, new RoutedEventArgs());
                     e.Handled = true;
                     return;
               }
            }
            // 3. Check for Ctrl-Only combinations (LOCAL ACTIONS)
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
               switch (e.Key)
               {
                  case Key.N: // New Note
                     AppInstance.CreateNewNoteNear(this);
                     e.Handled = true;
                     return;
                  case Key.W: // Close / Delete Note
                     Close();
                     e.Handled = true;
                     return;
                  case Key.M: // Minimize Current Note
                     if (_noteStuckMode == 2) return;
                     WindowState = WindowState.Minimized;
                     AppInstance.QueueSaveFromWindow();
                     e.Handled = true;
                     return;
                  case Key.L: // Toggle Lock
                     Menu_ToggleLock(this, new RoutedEventArgs());
                     e.Handled = true;
                     return;
                  case Key.F: // Font Settings
                     Menu_FontSettings(this, new RoutedEventArgs());
                     e.Handled = true;
                     return;
                  case Key.K: // Ink Mode
                     Menu_InkMode(this, new RoutedEventArgs());
                     e.Handled = true;
                     return;
               }
            }
            // 4. Standalone Keys
            if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.F12)
            {
               new DebugColorsWindow(_note!) { Owner = this }.Show();
               e.Handled = true;
            }
         };       // ENDS KEYBOARD SHORTCUT SECTION



         Loaded += (s, e) =>
         {
            ApplyLockState();
            ApplyRotation();
            ApplyAging();
         };

      }

      public void SetNoteId(string id) => NoteId = string.IsNullOrWhiteSpace(id) ? NoteId : id;

      public void SetText(string text)
      {
         WithSuppressedTextChanged(() =>
         {
            txtNoteContent.Document.Blocks.Clear();
            txtNoteContent.Document.Blocks.Add(new Paragraph(new Run(text ?? "")));

            InitializeTypingDefaultsFromNote(); // typing defaults only
         });
      }


      protected override void OnClosing(CancelEventArgs e)
      {
         _isShuttingDown = true;

         try
         {
            _stickySnapPending = false;
            _stickyCoalesceTimer?.Stop();
            StopHook();
         }
         catch { }

         base.OnClosing(e);
      }

      protected override void OnClosed(EventArgs e)
      {
         WpfDataObject.RemovePastingHandler(txtNoteContent, TxtNoteContent_Pasting);

         try
         {
            if (_inkToolbarWindow != null)
            {
               _inkToolbarWindow.PenRequested -= InkToolbarWindow_PenRequested;
               _inkToolbarWindow.EraseRequested -= InkToolbarWindow_EraseRequested;
               _inkToolbarWindow.ClearRequested -= InkToolbarWindow_ClearRequested;
               _inkToolbarWindow.DoneRequested -= InkToolbarWindow_DoneRequested;
               _inkToolbarWindow.ThicknessChanged -= InkToolbarWindow_ThicknessChanged;
               _inkToolbarWindow.ColorChanged -= InkToolbarWindow_ColorChanged;
               _inkToolbarWindow.LocationChanged -= InkToolbarWindow_LocationChanged;
               _inkToolbarWindow.Close();
               _inkToolbarWindow = null;
            }
         }
         catch
         {
            // best-effort
         }

         try { StopHook(); } catch { }
         try { ClearLocalAotOwner(); } catch { }
         base.OnClosed(e);
      }



      private void Menu_ToggleLock(object sender, RoutedEventArgs e)
      {
         if (_note == null) return;

         _note.IsLocked = !_note.IsLocked;
         ApplyLockState();

         AppInstance.QueueSaveFromWindow();
      }

      private void Menu_StraightenNote(object sender, RoutedEventArgs e)
      {
         if (_note == null) return;

         // Use a microscopic non-zero value so it doesn't get re-randomized on load
         _note.RotationAngle = 0.001;
         ApplyRotation();

         AppInstance.QueueSaveFromWindow();
      }




      public void ApplyLockState()
      {
         if (_note == null) return;
         bool locked = _note.IsLocked;

         // 1. Lock text boxes
         if (txtNoteContent != null)
         {
            txtNoteContent.IsReadOnly = locked || _inkModeEnabled;
            txtNoteContent.CaretBrush = locked ? System.Windows.Media.Brushes.Transparent : CaretBrush;
         }
         if (txtNoteTitle != null) txtNoteTitle.IsReadOnly = locked;

         // 2. Disable ink
         if (inkLayer != null) inkLayer.IsHitTestVisible = !locked && _inkModeEnabled;

         // 3. Update context menu
         if (miLockNote != null) miLockNote.IsChecked = locked;

         // 4. HIDE THE CLOSE BUTTON SO IT CANNOT BE CLICKED!
         if (btnClose != null)
         {
            btnClose.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
         }
      }

      // Make this public so we can force live-updates from the Preferences window
      public void ApplyRotation()
      {
         if (_note == null || NoteRotation == null) return;

         // 1. If disabled, force visual angle to 0
         if (!AppInstance.Preferences.EnableNoteRotation)
         {
            NoteRotation.Angle = 0;
            return;
         }

         // Grab the slider value (fallback to 4 if something goes wrong)
         double maxRot = AppInstance.Preferences.MaxNoteRotation;
         if (maxRot < 1.0) maxRot = 4.0;

         // 2. If it's a new note, randomize it using the slider bounds
         if (_note.RotationAngle == 0)
         {
            Random rnd = new Random();
            _note.RotationAngle = (rnd.NextDouble() * (maxRot * 2)) - maxRot;
         } else
         {
            // 3. If it's an existing note, clamp it! 
            // (If you turn the slider down, existing notes snap to the new limit)
            if (_note.RotationAngle > maxRot) _note.RotationAngle = maxRot;
            if (_note.RotationAngle < -maxRot) _note.RotationAngle = -maxRot;
         }

         // 4. Apply the final angle
         NoteRotation.Angle = _note.RotationAngle;
      }

      public void ApplyAging()
      {
         if (_note == null || NoteChrome == null) return;

         AgingCanvas.Children.Clear();
         NoteChrome.Clip = null;
         StickyOverlay.Clip = null;

         if (!AppInstance.Preferences.EnableNoteAging) return;

         double ageInDays = (DateTime.UtcNow - _note.Props.CreatedUtc).TotalDays;

         // --- 1 YEAR EASTER EGG ---
         if (ageInDays >= 365.0 && !_note.Props.HasSeenAnniversary)
         {
            if (NoteRotation != null) NoteRotation.Angle += 180;
            this.PreviewMouseLeftButtonDown -= AnniversaryClickHandler;
            this.PreviewMouseLeftButtonDown += AnniversaryClickHandler;
         }

         // Determine the Milestone Stage (1 through 8)
         int stage = 0;
         if (ageInDays >= 60.0) stage = 8;
         else if (ageInDays >= 53.0) stage = 7;
         else if (ageInDays >= 45.0) stage = 6;
         else if (ageInDays >= 38.0) stage = 5;
         else if (ageInDays >= 30.0) stage = 4;
         else if (ageInDays >= 21.0) stage = 3;
         else if (ageInDays >= 14.0) stage = 2;
         else if (ageInDays >= 7.0) stage = 1;

         if (stage == 0) return; // Note is fresh!

         // Feed the Fold Engine based on logical, progressive wear-and-tear!
         if (stage == 1) ApplyFolds("4ar");
         else if (stage == 2) ApplyFolds("4cvr");
         else if (stage == 3) ApplyFolds("4chl", "1al");
         else if (stage == 4) ApplyFolds("4ar", "1chr");
         else if (stage == 5) ApplyFolds("1cvl", "4chl");
         else if (stage == 6) ApplyFolds("1al", "2dl", "3br"); // Updated to your custom combo!
         else if (stage == 7) ApplyFolds("1chr", "4cvr", "2dl", "3br"); // Valid paired tear
         else if (stage == 8) ApplyFolds("4chl", "1cvl", "3dr", "2bl"); // Max wear
      }
      private void ApplyFolds(params string[] folds)
      {
         var perimeter = new System.Windows.Media.PointCollection();

         // Start top-left and top-right
         perimeter.Add(new System.Windows.Point(0, 0));
         perimeter.Add(new System.Windows.Point(400, 0));

         // --- SECTION 4 (Right Edge & Bottom-Right Corner) ---
         if (folds.Contains("4cvr"))
         {
            perimeter.Add(new System.Windows.Point(400, 308));
            perimeter.Add(new System.Windows.Point(377, 400));
            DrawFlap(new System.Windows.Point(400, 308), new System.Windows.Point(377, 400), new System.Windows.Point(357, 391));
         } else if (folds.Contains("4ar"))
         {
            perimeter.Add(new System.Windows.Point(400, 360));
            perimeter.Add(new System.Windows.Point(360, 400));
            DrawFlap(new System.Windows.Point(400, 360), new System.Windows.Point(360, 400), new System.Windows.Point(360, 360));
         } else if (folds.Contains("4chl"))
         {
            perimeter.Add(new System.Windows.Point(400, 380));
            perimeter.Add(new System.Windows.Point(311, 400));
            DrawFlap(new System.Windows.Point(400, 380), new System.Windows.Point(311, 400), new System.Windows.Point(390, 360));
         } else
         {
            perimeter.Add(new System.Windows.Point(400, 400));
         }

         // --- SECTION 3 (Right-Middle Bottom Edge) ---
         if (folds.Contains("3dr"))
         {
            perimeter.Add(new System.Windows.Point(306, 400));
            // We use X=161 to perfectly meet 2bl's peak! Y=375 to fix the transposed height.
            perimeter.Add(new System.Windows.Point(161, 375));
            if (!folds.Contains("2bl")) perimeter.Add(new System.Windows.Point(161, 400));

            // Flap tip correctly folds UP to 172, 354
            DrawFlap(new System.Windows.Point(306, 400), new System.Windows.Point(161, 375), new System.Windows.Point(172, 354));
         } else if (folds.Contains("3br"))
         {
            perimeter.Add(new System.Windows.Point(298, 400));
            perimeter.Add(new System.Windows.Point(234, 377)); // Meets 2dl perfectly
            if (!folds.Contains("2dl")) perimeter.Add(new System.Windows.Point(254, 400));

            DrawFlap(new System.Windows.Point(298, 400), new System.Windows.Point(234, 377), new System.Windows.Point(250, 360));
         }

         // --- SECTION 2 (Left-Middle Bottom Edge) ---
         if (folds.Contains("2dl"))
         {
            if (!folds.Contains("3br")) perimeter.Add(new System.Windows.Point(254, 400));
            perimeter.Add(new System.Windows.Point(234, 377)); // Peak
            perimeter.Add(new System.Windows.Point(96, 400));

            DrawFlap(new System.Windows.Point(234, 377), new System.Windows.Point(96, 400), new System.Windows.Point(228, 353));
         } else if (folds.Contains("2bl"))
         {
            if (!folds.Contains("3dr")) perimeter.Add(new System.Windows.Point(161, 400));
            perimeter.Add(new System.Windows.Point(161, 375)); // Peak
            perimeter.Add(new System.Windows.Point(99, 400));

            DrawFlap(new System.Windows.Point(161, 375), new System.Windows.Point(99, 400), new System.Windows.Point(157, 356));
         }

         // --- SECTION 1 (Bottom-Left Corner & Left Edge) ---
         if (folds.Contains("1chr"))
         {
            // Perfect horizontal mirror of 4chl (400 - X)
            perimeter.Add(new System.Windows.Point(89, 400));
            perimeter.Add(new System.Windows.Point(0, 380));
            DrawFlap(new System.Windows.Point(89, 400), new System.Windows.Point(0, 380), new System.Windows.Point(10, 360));
         } else if (folds.Contains("1al"))
         {
            perimeter.Add(new System.Windows.Point(40, 400));
            perimeter.Add(new System.Windows.Point(0, 360));
            DrawFlap(new System.Windows.Point(40, 400), new System.Windows.Point(0, 360), new System.Windows.Point(40, 360));
         } else if (folds.Contains("1cvl"))
         {
            perimeter.Add(new System.Windows.Point(22, 400));
            perimeter.Add(new System.Windows.Point(0, 306));
            DrawFlap(new System.Windows.Point(22, 400), new System.Windows.Point(0, 306), new System.Windows.Point(41, 390));
         } else
         {
            perimeter.Add(new System.Windows.Point(0, 400));
         }

         // --- EXECUTE THE SCISSOR CUT ---

         var paperClip = new System.Windows.Media.StreamGeometry();
         using (var ctx = paperClip.Open())
         {
            ctx.BeginFigure(perimeter[0], true, true);
            for (int i = 1; i < perimeter.Count; i++) ctx.LineTo(perimeter[i], true, false);
         }
         paperClip.Freeze();
         NoteChrome.Clip = paperClip;

         var overlayClip = new System.Windows.Media.StreamGeometry();
         using (var ctx = overlayClip.Open())
         {
            ctx.BeginFigure(ScalePointForOverlay(perimeter[0]), true, true);
            for (int i = 1; i < perimeter.Count; i++) ctx.LineTo(ScalePointForOverlay(perimeter[i]), true, false);
         }
         overlayClip.Freeze();
         StickyOverlay.Clip = overlayClip;
      }
      // Helper to mathematically push the clip outward for the red Sticky border
      private System.Windows.Point ScalePointForOverlay(System.Windows.Point p)
      {
         double x = p.X;
         double y = p.Y;
         if (x == 0) x = -3;
         else if (x == 400) x = 403; // Note: if you change width, adjust this!
         if (y == 0) y = -3;
         else if (y == 400) y = 403;
         return new System.Windows.Point(x, y);
      }
      private void DrawFlap(System.Windows.Point p1, System.Windows.Point p2, System.Windows.Point p3)
      {
         var paperBrush = _note?.PaperBrush ?? System.Windows.Media.Brushes.Yellow;

         // 1. The folded paper colored piece
         var paperFlap = new System.Windows.Shapes.Polygon
         {
            Points = new System.Windows.Media.PointCollection { p1, p2, p3 },
            Fill = paperBrush,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
               BlurRadius = 5,
               ShadowDepth = 2,
               Opacity = 0.5,
               Direction = 90
            }
         };

         // 2. The darkened "back side of paper" shading
         var shadeFlap = new System.Windows.Shapes.Polygon
         {
            Points = new System.Windows.Media.PointCollection { p1, p2, p3 },
            Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 0, 0, 0))
         };

         AgingCanvas.Children.Add(paperFlap);
         AgingCanvas.Children.Add(shadeFlap);
      }
      private void AnniversaryClickHandler(object sender, System.Windows.Input.MouseButtonEventArgs e)
      {
         // 1. Remove the handler immediately so we don't spam the user with popups on every click!
         this.PreviewMouseLeftButtonDown -= AnniversaryClickHandler;

         // 2. Show your custom message
         System.Windows.MessageBox.Show(this,
             "Your note has lasted a year! Impressive! To celebrate, the note will be 'dancing on the ceiling' until you quit and restart the app.",
             "Happy Anniversary!",
             MessageBoxButton.OK,
             MessageBoxImage.Information);

         // 3. Mark the Easter egg as 'seen'. 
         // It stays upside down for now, but on the NEXT app launch, it will skip the flip!
         _note!.Props.HasSeenAnniversary = true;

         // (Optional) Force a save so it doesn't forget if they crash
         AppInstance.QueueSaveFromWindow();
      }


      public string GetStuckToWindowName()
      {
         // If we aren't in Mode 2, it's not stuck to anything
         if (_noteStuckMode != 2) return "None";

         if (_stickyTarget != null)
         {
            // Check if it's the desktop shell
            var cls = _stickyTarget.ClassName?.Trim();
            if (string.Equals(cls, "Progman", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cls, "WorkerW", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cls, "#32769", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cls, "Desktop", StringComparison.OrdinalIgnoreCase))
            {
               return "[Desktop]";
            }

            // Otherwise, return the normal window title or process name
            return !string.IsNullOrWhiteSpace(_stickyTarget.WindowTitle)
                ? _stickyTarget.WindowTitle
                : _stickyTarget.ProcessName ?? "Unknown Host";
         }

         return "Unknown Host";
      }





      private void TxtNoteContent_Pasting(object sender, DataObjectPastingEventArgs e)
      {
         if (e?.SourceDataObject == null)
            return;

         if (!string.Equals(e.FormatToApply, WpfDataFormats.Xaml, StringComparison.Ordinal))
            return;

         try
         {
            var xaml = e.SourceDataObject.GetData(WpfDataFormats.Xaml) as string;
            if (!string.IsNullOrWhiteSpace(xaml)
               && (xaml.Contains("Microsoft.VisualStudio.PlatformUI", StringComparison.Ordinal)
                   || xaml.Contains("DelegateCommand", StringComparison.Ordinal)))
            {
               if (e.SourceDataObject.GetDataPresent(WpfDataFormats.UnicodeText))
               {
                  e.FormatToApply = WpfDataFormats.UnicodeText;
                  return;
               }

               if (e.SourceDataObject.GetDataPresent(WpfDataFormats.Text))
               {
                  e.FormatToApply = WpfDataFormats.Text;
                  return;
               }

               e.CancelCommand();
            }
         }
         catch
         {
            if (e.SourceDataObject.GetDataPresent(WpfDataFormats.UnicodeText))
               e.FormatToApply = WpfDataFormats.UnicodeText;
         }
      }

      private void Menu_ToggleBold(object sender, RoutedEventArgs e) => ToggleBold();
      private void Menu_ToggleItalic(object sender, RoutedEventArgs e) => ToggleItalic();
      private void Menu_ToggleUnderline(object sender, RoutedEventArgs e) => ToggleUnderline();
      private void Menu_ToggleStrikethrough(object sender, RoutedEventArgs e) => ToggleStrikethrough();


      private void GetSelectionFontInfoWpf(out string family, out double sizeDip, out bool bold, out bool italic)
      {
         var sel = txtNoteContent.Selection;

         object ff = sel.GetPropertyValue(TextElement.FontFamilyProperty);
         object fs = sel.GetPropertyValue(TextElement.FontSizeProperty);
         object fw = sel.GetPropertyValue(TextElement.FontWeightProperty);
         object fst = sel.GetPropertyValue(TextElement.FontStyleProperty);

         family = (ff is System.Windows.Media.FontFamily fam) ? fam.Source : (string.IsNullOrWhiteSpace(_note?.FontFamily) ? "Segoe UI" : _note.FontFamily);
         sizeDip = (fs is double d && d > 0) ? d : ((_note?.FontSize ?? 0) > 0 ? _note!.FontSize : 16.0);

         bold = (fw is FontWeight w) && (w == FontWeights.Bold);
         italic = (fst is System.Windows.FontStyle s) && (s == System.Windows.FontStyles.Italic);

      }

      private static TextDecorationCollection GetDecorationsOrEmpty(object v)
      {
         if (v is TextDecorationCollection col)
         {
            // clone so we don't mutate a shared instance
            return [.. col.Select(d => d.Clone())];
         }
         return new TextDecorationCollection();
      }

      private static bool HasDecorationLocation(TextDecorationCollection col, TextDecorationLocation loc)
         => col.Any(d => d.Location == loc);

      private static void RemoveDecorationLocation(TextDecorationCollection col, TextDecorationLocation loc)
      {
         for (int i = col.Count - 1; i >= 0; i--)
         {
            if (col[i].Location == loc)
               col.RemoveAt(i);
         }
      }

      private void ApplyFontToSelectionWpf(string familyName, double sizeDip, bool bold, bool italic)
      {
         var sel = txtNoteContent.Selection;
         var range = new TextRange(sel.Start, sel.End); // empty selection => typing style only

         var fontFamily = CreateSafeFontFamily(familyName);
         var normalizedSize = NormalizeFontSize(sizeDip, _note?.FontSize > 0 ? _note.FontSize : 14.0);

         range.ApplyPropertyValue(TextElement.FontFamilyProperty, fontFamily);
         range.ApplyPropertyValue(TextElement.FontSizeProperty, normalizedSize);
         range.ApplyPropertyValue(TextElement.FontWeightProperty, bold ? FontWeights.Bold : FontWeights.Normal);
         range.ApplyPropertyValue(TextElement.FontStyleProperty, italic ? FontStyles.Italic : FontStyles.Normal);

         // Persist per-note defaults (store DIPs)
         if (_note != null)
         {
            _note.FontFamily = fontFamily.Source;
            _note.FontSize = normalizedSize;
         }

         UpdateLineSpacing();
         UpdateCaretBrush();

         NoteTextChanged?.Invoke(this, EventArgs.Empty);
      }



      private void WithSuppressedTextChanged(Action action)
      {
         _suppressTextChanged = true;
         try { action(); }
         finally { _suppressTextChanged = false; }
      }

      private void WithSuppressedInkChanged(Action action)
      {
         _suppressInkChanged = true;
         try { action(); }
         finally { _suppressInkChanged = false; }
      }

      private void ConfigureInkLayer()
      {
         if (inkLayer == null)
            return;

         inkLayer.EditingMode = InkCanvasEditingMode.Ink;
         _inkColor = GetDefaultTextColor();
         ApplyInkDrawingAttributes();

         inkLayer.StrokeCollected += (_, __) =>
         {
            if (_suppressInkChanged)
               return;
            NoteTextChanged?.Invoke(this, EventArgs.Empty);
         };

         inkLayer.Strokes.StrokesChanged += (_, __) =>
         {
            if (_suppressInkChanged)
               return;
            NoteTextChanged?.Invoke(this, EventArgs.Empty);
         };

         ApplyInkMode();
      }

      private void ApplyInkMode()
      {
         if (inkLayer == null || txtNoteContent == null)
            return;

         inkLayer.IsHitTestVisible = _inkModeEnabled;
         inkLayer.EditingMode = _inkModeEnabled
            ? (_inkEraseModeEnabled ? InkCanvasEditingMode.EraseByStroke : InkCanvasEditingMode.Ink)
            : InkCanvasEditingMode.None;

         txtNoteContent.IsReadOnly = _inkModeEnabled;

         if (miInkMode != null)
            miInkMode.IsChecked = _inkModeEnabled;
         if (miInkEraseMode != null)
         {
            miInkEraseMode.IsChecked = _inkModeEnabled && _inkEraseModeEnabled;
            miInkEraseMode.IsEnabled = _inkModeEnabled;
         }

         if (_inkModeEnabled && WindowState != WindowState.Minimized)
         {
            EnsureInkToolbar();
            if (_inkToolbarWindow != null)
            {
               _inkToolbarWindow.SetMode(_inkModeEnabled, _inkEraseModeEnabled);
               _inkToolbarWindow.SetThickness(_inkThicknessLevel);
               _inkToolbarWindow.SetColor(_inkColor);

               if (!_inkToolbarWindow.IsVisible)
                  _inkToolbarWindow.Show();

               if (_inkToolbarSnapEnabled)
                  PositionInkToolbar();
            }
         } else
         {
            if (_inkToolbarWindow?.IsVisible == true)
               _inkToolbarWindow.Hide();
         }
      }

      private void EnsureInkToolbar()
      {
         if (_inkToolbarWindow != null)
            return;

         _inkToolbarWindow = new InkToolbarWindow
         {
            Owner = this,
            ShowInTaskbar = false
         };

         _inkToolbarWindow.PenRequested += InkToolbarWindow_PenRequested;
         _inkToolbarWindow.EraseRequested += InkToolbarWindow_EraseRequested;
         _inkToolbarWindow.ClearRequested += InkToolbarWindow_ClearRequested;
         _inkToolbarWindow.DoneRequested += InkToolbarWindow_DoneRequested;
         _inkToolbarWindow.ThicknessChanged += InkToolbarWindow_ThicknessChanged;
         _inkToolbarWindow.ColorChanged += InkToolbarWindow_ColorChanged;
         _inkToolbarWindow.LocationChanged += InkToolbarWindow_LocationChanged;
         _inkToolbarWindow.SetThickness(_inkThicknessLevel);
         _inkToolbarWindow.SetColor(_inkColor);
      }

      private void InkToolbarWindow_PenRequested(object? sender, EventArgs e)
      {
         _inkModeEnabled = true;
         _inkEraseModeEnabled = false;
         ApplyInkMode();
      }

      private void InkToolbarWindow_EraseRequested(object? sender, EventArgs e)
      {
         _inkModeEnabled = true;
         _inkEraseModeEnabled = true;
         ApplyInkMode();
      }

      private void InkToolbarWindow_ClearRequested(object? sender, EventArgs e)
      {
         Menu_ClearInk(this, new RoutedEventArgs());
      }

      private void InkToolbarWindow_DoneRequested(object? sender, EventArgs e)
      {
         _inkModeEnabled = false;
         _inkEraseModeEnabled = false;
         ApplyInkMode();
      }

      private void InkToolbarWindow_ThicknessChanged(double thickness)
      {
         _inkThicknessLevel = Math.Max(1.0, Math.Min(10.0, thickness));
         ApplyInkDrawingAttributes();
      }

      private void InkToolbarWindow_ColorChanged(System.Windows.Media.Color color)
      {
         _inkColor = color;
         _inkColorIsCustom = true;
         ApplyInkDrawingAttributes();
      }

      private void InkToolbarWindow_LocationChanged(object? sender, EventArgs e)
      {
         if (_inkToolbarWindow == null || _suppressToolbarMoveHandling)
            return;

         TrySnapInkToolbarToNote();
      }

      private void PositionInkToolbar()
      {
         if (_inkToolbarWindow == null)
            return;

         var noteRect = new Rect(Left, Top, Width, Height);
         var toolbarWidth = _inkToolbarWindow.ActualWidth > 0 ? _inkToolbarWindow.ActualWidth : _inkToolbarWindow.Width;
         var toolbarHeight = _inkToolbarWindow.ActualHeight > 0 ? _inkToolbarWindow.ActualHeight : _inkToolbarWindow.Height;

         double left;
         double top;

         switch (_inkToolbarDock)
         {
            case InkToolbarDock.Left:
               left = noteRect.Left - toolbarWidth - InkToolbarGap;
               top = noteRect.Top + _inkToolbarDockOffset;
               break;
            case InkToolbarDock.Top:
               left = noteRect.Left + _inkToolbarDockOffset;
               top = noteRect.Top - toolbarHeight - InkToolbarGap;
               break;
            case InkToolbarDock.Bottom:
               left = noteRect.Left + _inkToolbarDockOffset;
               top = noteRect.Bottom + InkToolbarGap;
               break;
            default:
               left = noteRect.Right + InkToolbarGap;
               top = noteRect.Top + _inkToolbarDockOffset;
               break;
         }

         _suppressToolbarMoveHandling = true;
         try
         {
            _inkToolbarWindow.Left = left;
            _inkToolbarWindow.Top = top;
         }
         finally
         {
            _suppressToolbarMoveHandling = false;
         }
      }

      private void TrySnapInkToolbarToNote()
      {
         if (_inkToolbarWindow == null)
            return;

         var noteRect = new Rect(Left, Top, Width, Height);
         var toolbarRect = new Rect(_inkToolbarWindow.Left, _inkToolbarWindow.Top,
            _inkToolbarWindow.ActualWidth > 0 ? _inkToolbarWindow.ActualWidth : _inkToolbarWindow.Width,
            _inkToolbarWindow.ActualHeight > 0 ? _inkToolbarWindow.ActualHeight : _inkToolbarWindow.Height);

         var rightDistance = Math.Abs(toolbarRect.Left - (noteRect.Right + InkToolbarGap));
         var leftDistance = Math.Abs(toolbarRect.Right - (noteRect.Left - InkToolbarGap));
         var topDistance = Math.Abs(toolbarRect.Bottom - (noteRect.Top - InkToolbarGap));
         var bottomDistance = Math.Abs(toolbarRect.Top - (noteRect.Bottom + InkToolbarGap));

         var minDistance = new[] { rightDistance, leftDistance, topDistance, bottomDistance }.Min();

         if (minDistance > InkToolbarSnapDistance)
         {
            _inkToolbarSnapEnabled = false;
            return;
         }

         _inkToolbarSnapEnabled = true;
         if (minDistance == rightDistance)
         {
            _inkToolbarDock = InkToolbarDock.Right;
            _inkToolbarDockOffset = toolbarRect.Top - noteRect.Top;
         } else if (minDistance == leftDistance)
         {
            _inkToolbarDock = InkToolbarDock.Left;
            _inkToolbarDockOffset = toolbarRect.Top - noteRect.Top;
         } else if (minDistance == topDistance)
         {
            _inkToolbarDock = InkToolbarDock.Top;
            _inkToolbarDockOffset = toolbarRect.Left - noteRect.Left;
         } else
         {
            _inkToolbarDock = InkToolbarDock.Bottom;
            _inkToolbarDockOffset = toolbarRect.Left - noteRect.Left;
         }

         PositionInkToolbar();
      }

      private static bool IsUnset(object v) => v == DependencyProperty.UnsetValue || v == null;

      private bool GetSelectionBold()
      {
         var v = txtNoteContent.Selection.GetPropertyValue(TextElement.FontWeightProperty);
         return (v is FontWeight w) && (w == FontWeights.Bold);
      }

      private bool GetSelectionItalic()
      {
         var v = txtNoteContent.Selection.GetPropertyValue(TextElement.FontStyleProperty);
         return (v is System.Windows.FontStyle s) && (s == FontStyles.Italic);
      }

      private static bool HasDecoration(object v, TextDecorationLocation loc)
      {
         if (v is not TextDecorationCollection col) return false;
         return col.Any(d => d.Location == loc);
      }


      private void ToggleBold()
      {
         GetSelectionFontInfoWpf(out var family, out var sizeDip, out var bold, out var italic);
         ApplyFontToSelectionWpf(family, sizeDip, !bold, italic);
      }

      private void ToggleItalic()
      {
         GetSelectionFontInfoWpf(out var family, out var sizeDip, out var bold, out var italic);
         ApplyFontToSelectionWpf(family, sizeDip, bold, !italic);
      }

      private void ToggleUnderline()
   => ToggleDecoration(TextDecorationLocation.Underline, TextDecorations.Underline);

      private void ToggleStrikethrough()
         => ToggleDecoration(TextDecorationLocation.Strikethrough, TextDecorations.Strikethrough);


      private static bool HasDecoration(object v, TextDecorationCollection deco)
      {
         if (v == null || v == DependencyProperty.UnsetValue) return false;
         if (v is not TextDecorationCollection col) return false;

         // reference equality works here for the standard sets (Underline / Strikethrough)
         return col == deco;
      }

      private void ToggleDecoration(TextDecorationLocation loc, TextDecorationCollection addSet)
      {
         var sel = txtNoteContent.Selection;

         object v = sel.GetPropertyValue(Inline.TextDecorationsProperty);
         var col = GetDecorationsOrEmpty(v);

         if (HasDecorationLocation(col, loc))
            RemoveDecorationLocation(col, loc);
         else
            foreach (var d in addSet) col.Add(d.Clone());

         var range = new TextRange(sel.Start, sel.End);
         range.ApplyPropertyValue(Inline.TextDecorationsProperty, col.Count == 0 ? null : col);

         NoteTextChanged?.Invoke(this, EventArgs.Empty);
      }




      private void txtNoteContent_SelectionChanged(object sender, RoutedEventArgs e)
      {
         miBold.IsChecked =
            txtNoteContent.Selection.GetPropertyValue(TextElement.FontWeightProperty) is FontWeight w
            && w == FontWeights.Bold;

         miItalic.IsChecked =
            txtNoteContent.Selection.GetPropertyValue(TextElement.FontStyleProperty) is System.Windows.FontStyle s
            && s == System.Windows.FontStyles.Italic;

         var deco = txtNoteContent.Selection.GetPropertyValue(Inline.TextDecorationsProperty);

         miUnderline.IsChecked = HasDecoration(deco, TextDecorationLocation.Underline);

         UpdateCaretBrush();

         // if you have a Strikethrough menu item:
         // miStrike.IsChecked = HasDecoration(deco, TextDecorationLocation.Strikethrough);
      }





      public string GetText()
      {
         var range = new TextRange(
            txtNoteContent.Document.ContentStart,
            txtNoteContent.Document.ContentEnd);

         return range.Text.TrimEnd('\r', '\n');
      }

      public void SetColorKey(string keyName)
      {
         if (_note != null && Enum.TryParse(keyName, out NoteColors.NoteColor key))
         {
            _note.ColorKey = key;
            ApplyDefaultInkAttributes();
         }
      }

      private void ColorMenuItem_Click(object sender, RoutedEventArgs e)
      {
         if (_note == null)
            return;

         if (sender is not MenuItem menuItem)
            return;

         if (menuItem.Tag is string tag && Enum.TryParse(tag, out NoteColors.NoteColor color))
         {
            _note.ColorKey = color;
            ApplyDefaultInkAttributes();
         }
      }

      private void btnClose_Click(object sender, RoutedEventArgs e)
      {
         Close();
         _followTimer.Stop();

      }

      private void btnMinimize_Click(object sender, RoutedEventArgs e)
      {
         if (_noteStuckMode == 2)
            return;

         WindowState = WindowState.Minimized;
      }


      //GEMINI IN

      private void btnPinCycle_Click(object sender, RoutedEventArgs e)
      {
         // Cycle: 0 -> 1 -> 2 -> 0
         var next = (_noteStuckMode + 1) % 3;

         if (next == 2)
         {
            StickToWindowUnderMe(); // Automatically handles fallbacks and reverts
            return;
         }

         ApplyStuckMode(next);
         StopHook();

         if (next == 0)
         {
            _stickyTarget = null;
            _stickyOffsetXPx = null;
            _stickyOffsetYPx = null;
         }

         AppInstance.QueueSaveFromWindow();
      }

      private void Sticky_Auto_Click(object sender, RoutedEventArgs e)
      {
         StickToWindowUnderMe();
      }

      public void EnsureStickyTargetOnLoad()
      {
         if (_noteStuckMode != 2)
            return;

         // 1) Try persisted target rebind
         if (TryRebindStickyTarget() && SnapToStickyTargetNow() && IsNoteVisibleOnAnyScreen())
            return;

         // 2) If that fails, execute the full pipeline (Window -> Fallbacks -> Desktop -> Mode 0)
         StickToWindowUnderMe();
      }

      //GEMINI OUT



      private void InitializeTypingDefaultsFromNote()
      {

         if (_note == null) return;

         // fallbacks
         string familyName = string.IsNullOrWhiteSpace(_note.FontFamily) ? "Segoe UI" : _note.FontFamily;
         double sizePt = NormalizeFontSize(_note.FontSize, 12.0);

         var rtb = txtNoteContent;

         // IMPORTANT: apply to an *empty* range at the caret/document start
         // so existing content is not reformatted.
         TextPointer start = rtb.CaretPosition ?? rtb.Document.ContentStart;
         if (start == null) start = rtb.Document.ContentStart;

         // ensure we're at an insertion position
         start = start.GetInsertionPosition(LogicalDirection.Forward) ?? rtb.Document.ContentStart;

         var range = new TextRange(start, start); // empty range == typing style only
         range.ApplyPropertyValue(TextElement.FontFamilyProperty, CreateSafeFontFamily(familyName));
         range.ApplyPropertyValue(TextElement.FontSizeProperty, sizePt);
         range.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(GetDefaultTextColor()));
         ApplyDefaultInkAttributes();
         UpdateLineSpacing();
         UpdateCaretBrush();

      }

      public string? GetRtf()
      {
         try
         {
            var range = new TextRange(
                txtNoteContent.Document.ContentStart,
                txtNoteContent.Document.ContentEnd);

            using var ms = new MemoryStream();
            range.Save(ms, System.Windows.DataFormats.Rtf);

            // Avoid writing meaningless empty RTF blobs
            if (ms.Length == 0)
               return null;

            return Encoding.UTF8.GetString(ms.ToArray());
         }
         catch
         {
            return null;
         }
      }

      public void SetRtf(string? rtf)
      {
         WithSuppressedTextChanged(() =>
         {
            txtNoteContent.Document.Blocks.Clear();

            if (string.IsNullOrWhiteSpace(rtf))
            {
               InitializeTypingDefaultsFromNote(); // still set defaults for new typing
               return;
            }

            var bytes = Encoding.UTF8.GetBytes(rtf);

            using var ms = new MemoryStream(bytes);
            var range = new TextRange(txtNoteContent.Document.ContentStart, txtNoteContent.Document.ContentEnd);

            try
            {
               range.Load(ms, System.Windows.DataFormats.Rtf);
            }
            catch
            {
               txtNoteContent.Document.Blocks.Clear();
            }

            InitializeTypingDefaultsFromNote(); // typing defaults only
         });
      }



      private void txtNoteContent_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
      {
         txtNoteContent.Focus();

         var clickPoint = e.GetPosition(txtNoteContent);
         var textPosition = txtNoteContent.GetPositionFromPoint(clickPoint, true);
         if (textPosition != null)
         {
            var selection = txtNoteContent.Selection;
            var selectionStart = selection.Start;
            var selectionEnd = selection.End;

            var clickedInsideSelection = selectionStart != null
               && selectionEnd != null
               && textPosition.CompareTo(selectionStart) >= 0
               && textPosition.CompareTo(selectionEnd) <= 0;

            if (!clickedInsideSelection)
            {
               txtNoteContent.CaretPosition = textPosition;
               txtNoteContent.Selection.Select(textPosition, textPosition);
            }
         }

         e.Handled = true;

         if (NoteChrome?.ContextMenu != null)
         {
            NoteChrome.ContextMenu.PlacementTarget = txtNoteContent;
            NoteChrome.ContextMenu.IsOpen = true;
         }
      }



      public DateTime GetCreatedUtc() => _note?.Props.CreatedUtc ?? default;
      public DateTime GetModifiedUtc() => _note?.Props.ModifiedUtc ?? default;

      private void TouchModifiedUtc()
      {
         if (_note != null)
            _note.Props.ModifiedUtc = DateTime.UtcNow;
      }

      private App AppInstance => (App)System.Windows.Application.Current;

      private void Menu_NewNote(object sender, RoutedEventArgs e)
      {
         AppInstance.CreateNewNoteNear(this);
      }

      private void Menu_Color(object sender, RoutedEventArgs e)
      {
         if (sender is not MenuItem mi || mi.Tag is not string keyName) return;

         if (Enum.TryParse(keyName, out NoteColors.NoteColor key))
         {
            if (_note != null)
               _note.ColorKey = key;
            ApplyDefaultInkAttributes();
            UpdateCaretBrush();
            AppInstance.QueueSaveFromWindow(); // we’ll add this tiny helper in App
         }
      }

      private void Menu_Minimize(object sender, RoutedEventArgs e)
      {
         if (_noteStuckMode == 2)
            return;

         SetIsMinimized(true);
         AppInstance.QueueSaveFromWindow();
      }

      private void Menu_Restore(object sender, RoutedEventArgs e)
      {
         SetIsMinimized(false);
         AppInstance.QueueSaveFromWindow();
      }

      private void Menu_Debug(object sender, RoutedEventArgs e)
      {
         new DebugColorsWindow(_note!) { Owner = this }.Show();
      }

      private void Menu_NoteProperties(object sender, RoutedEventArgs e)
      {
         var dlg = new NotePropertiesWindow(this) { Owner = this };
         dlg.ShowDialog();
      }

      private void Menu_Delete(object sender, RoutedEventArgs e)
      {
         Close(); // close = delete per your rule
         StopHook();
      }

      private void Menu_About(object sender, RoutedEventArgs e)
      {
         var aboutDlg = new aboutWindow
         {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Topmost = true
         };

         // Force exact center of the Primary Windows Monitor
         aboutDlg.Left = (SystemParameters.PrimaryScreenWidth - aboutDlg.Width) / 2;
         aboutDlg.Top = (SystemParameters.PrimaryScreenHeight - aboutDlg.Height) / 2;

         aboutDlg.ShowDialog();
      }

      private void Menu_Exit(object sender, RoutedEventArgs e)
      {
         AppInstance.ShutdownRequested();
      }


      private void Menu_Cut(object sender, RoutedEventArgs e) => txtNoteContent.Cut();
      private void Menu_Copy(object sender, RoutedEventArgs e) => txtNoteContent.Copy();
      private void Menu_Paste(object sender, RoutedEventArgs e) => txtNoteContent.Paste();

      private void Menu_TaskCompleted(object sender, RoutedEventArgs e)
      {
         // ✅ REPLACE the entire method body with this single line:
         ToggleStrikethrough();

         //var paragraph = txtNoteContent.CaretPosition?.Paragraph ?? txtNoteContent.Selection.Start?.Paragraph;
         //if (paragraph == null)
         //{
         //   ToggleStrikethrough();
         //   return;
         //}

         //var range = GetParagraphContentRange(paragraph);
         //var decorations = GetDecorationsOrEmpty(range.GetPropertyValue(Inline.TextDecorationsProperty));

         //if (HasDecorationLocation(decorations, TextDecorationLocation.Strikethrough))
         //   RemoveDecorationLocation(decorations, TextDecorationLocation.Strikethrough);
         //else
         //{
         //   foreach (var d in TextDecorations.Strikethrough)
         //      decorations.Add(d.Clone());
         //}

         //range.ApplyPropertyValue(Inline.TextDecorationsProperty, decorations.Count == 0 ? null : decorations);

         //var caret = range.End.GetInsertionPosition(LogicalDirection.Forward) ?? range.End;
         //txtNoteContent.Selection.Select(caret, caret);
         //new TextRange(caret, caret).ApplyPropertyValue(Inline.TextDecorationsProperty, null);

         //NoteTextChanged?.Invoke(this, EventArgs.Empty);
      }

      private void NoteContextMenu_Opened(object sender, RoutedEventArgs e)
      {
         if (miExportNote != null)
            miExportNote.IsEnabled = _externalNoteImportExportEnabled;

         if (miImportNote != null)
            miImportNote.IsEnabled = _externalNoteImportExportEnabled;

         if (LeadingSlider != null)
            LeadingSlider.Value = _lineHeightMultiplier;

      }

      private void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
      {
         if (txtNoteContent == null)
            return;

         var size = NormalizeFontSize(e.NewValue, _note?.FontSize ?? 14.0);
         if (_note != null)
            _note.FontSize = size;

         ApplyFontToSelectionWpf(_note?.FontFamily ?? "Segoe UI", size, GetSelectionBold(), GetSelectionItalic());
         UpdateLineSpacing();
      }

      private void LeadingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
      {
         if (_note == null)
            return;

         _note.LineHeightMultiplier = NormalizeLineHeightMultiplier(e.NewValue);
         UpdateLineSpacing();
      }

      public void ExportNoteFromManager()
      {
         Menu_ExportNote(this, new RoutedEventArgs());
      }

      public void ImportNoteFromManager()
      {
         Menu_ImportNote(this, new RoutedEventArgs());
      }

      private void Menu_SaveNow(object sender, RoutedEventArgs e)
      {
         AppInstance.QueueSaveFromWindow(); // triggers debounce
      }

      private void Menu_SyncNow(object sender, RoutedEventArgs e)
      {
         if (AppInstance.TrySyncNow(out var message))
            System.Windows.MessageBox.Show(this, message, "Sync", MessageBoxButton.OK, MessageBoxImage.Information);
         else
            System.Windows.MessageBox.Show(this, message, "Sync", MessageBoxButton.OK, MessageBoxImage.Warning);
      }

      private void Menu_SyncPullNow(object sender, RoutedEventArgs e)
      {
         if (AppInstance.TryPullFromSync(out var message))
            System.Windows.MessageBox.Show(this, message, "Sync Pull", MessageBoxButton.OK, MessageBoxImage.Information);
         else
            System.Windows.MessageBox.Show(this, message, "Sync Pull", MessageBoxButton.OK, MessageBoxImage.Warning);
      }

      private void Menu_SyncPushNow(object sender, RoutedEventArgs e)
      {
         if (AppInstance.TryPushToSync(out var message))
            System.Windows.MessageBox.Show(this, message, "Sync Push", MessageBoxButton.OK, MessageBoxImage.Information);
         else
            System.Windows.MessageBox.Show(this, message, "Sync Push", MessageBoxButton.OK, MessageBoxImage.Warning);
      }

      private void Menu_ExportNote(object sender, RoutedEventArgs e)
      {
         if (!_externalNoteImportExportEnabled)
         {
            System.Windows.MessageBox.Show(this, "Enable external note import/export in Preferences > Note first.", "Export note", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
         }

         var dlg = new Microsoft.Win32.SaveFileDialog
         {
            Title = "Export note",
            Filter = "StickIt note (*.3m)|*.3m|JSON file (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".3m",
            AddExtension = true,
            FileName = $"{GetTitle().Trim().Replace(' ', '_')}.3m"
         };

         if (dlg.ShowDialog(this) != true)
            return;

         try
         {
            var note = NotePersistMapper.FromWindow(this, GetStuckMode(), DateTime.UtcNow);
            ExternalNoteStore.Save(dlg.FileName, note);
            System.Windows.MessageBox.Show(this, "Note exported successfully.", "Export note", MessageBoxButton.OK, MessageBoxImage.Information);
         }
         catch (Exception ex)
         {
            System.Windows.MessageBox.Show(this, $"Export failed: {ex.Message}", "Export note", MessageBoxButton.OK, MessageBoxImage.Warning);
         }
      }

      private void Menu_ImportNote(object sender, RoutedEventArgs e)
      {
         if (!_externalNoteImportExportEnabled)
         {
            System.Windows.MessageBox.Show(this, "Enable external note import/export in Preferences > Note first.", "Load note", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
         }

         var dlg = new Microsoft.Win32.OpenFileDialog
         {
            Title = "Load external note",
            Filter = "StickIt note (*.3m;*.json)|*.3m;*.json|All files (*.*)|*.*",
            CheckFileExists = true
         };

         if (dlg.ShowDialog(this) != true)
            return;

         try
         {
            var imported = ExternalNoteStore.Load(dlg.FileName);
            if (AppInstance.TryImportExternalNote(imported, AppInstance.Preferences.SyncImportMode, this, out var message))
               System.Windows.MessageBox.Show(this, message, "Load note", MessageBoxButton.OK, MessageBoxImage.Information);
            else
               System.Windows.MessageBox.Show(this, message, "Load note", MessageBoxButton.OK, MessageBoxImage.Warning);
         }
         catch (Exception ex)
         {
            System.Windows.MessageBox.Show(this, $"Load failed: {ex.Message}", "Load note", MessageBoxButton.OK, MessageBoxImage.Warning);
         }
      }

      private void Menu_MinimizeAll(object sender, RoutedEventArgs e)
      {
         AppInstance.MinimizeAllNotes();
      }

      private void Menu_RestoreAll(object sender, RoutedEventArgs e)
      {
         AppInstance.RestoreHiddenNotes();
      }

      // Placeholders (disabled now; handler exists in case you enable later)
      private void Menu_FontSettings(object sender, RoutedEventArgs e)
      {
         var settings = GetSelectionFontSettings();
         var dlg = new FontSettingsWindow(settings) { Owner = this };
         if (dlg.ShowDialog() != true || dlg.Settings == null)
            return;

         ApplyFontSettings(dlg.Settings);

         if (dlg.Settings.SetAsDefaultForNewNotes)
            AppInstance.UpdateDefaultBodyFont(dlg.Settings.FontFamily, dlg.Settings.FontSize);

         if (dlg.Settings.ApplyToAllOpenNotes)
            AppInstance.ApplyFontSettingsToAllOpenNotes(dlg.Settings, this);
      }


      private void Menu_InkMode(object sender, RoutedEventArgs e)
      {
         _inkModeEnabled = !_inkModeEnabled;
         if (!_inkModeEnabled)
            _inkEraseModeEnabled = false;

         ApplyInkMode();
      }

      private void Menu_InkEraseMode(object sender, RoutedEventArgs e)
      {
         if (!_inkModeEnabled)
            return;

         _inkEraseModeEnabled = !_inkEraseModeEnabled;
         ApplyInkMode();
      }

      private void Menu_ClearInk(object sender, RoutedEventArgs e)
      {
         if (inkLayer == null || inkLayer.Strokes.Count == 0)
            return;

         WithSuppressedInkChanged(() => inkLayer.Strokes.Clear());
         NoteTextChanged?.Invoke(this, EventArgs.Empty);
      }

      private void Menu_Preferences(object sender, RoutedEventArgs e)
      {
         AppInstance.ShowPreferences();
      }
      private void Menu_NoteManager(object sender, RoutedEventArgs e)
      {
         AppInstance.ShowNoteManager();
      }

      private FontSettingsData GetSelectionFontSettings()
      {
         var sel = txtNoteContent.Selection;

         object ff = sel.GetPropertyValue(TextElement.FontFamilyProperty);
         object fs = sel.GetPropertyValue(TextElement.FontSizeProperty);
         object fw = sel.GetPropertyValue(TextElement.FontWeightProperty);
         object fst = sel.GetPropertyValue(TextElement.FontStyleProperty);
         object fg = sel.GetPropertyValue(TextElement.ForegroundProperty);

         string family =
            (ff is System.Windows.Media.FontFamily fam) ? fam.Source :
            (!string.IsNullOrWhiteSpace(_note?.FontFamily) ? _note.FontFamily : "Segoe UI");

         double sizeDip =
            (fs is double d && d > 0) ? d :
            (_note?.FontSize > 0 ? _note.FontSize : 14.0);

         bool bold = (fw is FontWeight w) && (w == FontWeights.Bold);
         bool italic = (fst is System.Windows.FontStyle s) && (s == System.Windows.FontStyles.Italic);

         var deco = sel.GetPropertyValue(Inline.TextDecorationsProperty);
         bool underline = HasDecorationLocation(GetDecorationsOrEmpty(deco), TextDecorationLocation.Underline);
         bool strikethrough = HasDecorationLocation(GetDecorationsOrEmpty(deco), TextDecorationLocation.Strikethrough);

         System.Windows.Media.Color color = System.Windows.Media.Colors.Black;
         if (fg is SolidColorBrush brush)
            color = brush.Color;
         else if (txtNoteContent.Foreground is SolidColorBrush fallback)
            color = fallback.Color;

         return new FontSettingsData
         {
            FontFamily = family,
            FontSize = sizeDip,
            IsBold = bold,
            IsItalic = italic,
            IsUnderline = underline,
            IsStrikeThrough = strikethrough,
            Color = color,
            LineHeightMultiplier = _note?.LineHeightMultiplier ?? _lineHeightMultiplier,
            ApplyToSelection = true
         };
      }

      private void ApplyFontSettings(FontSettingsData settings)
      {
         if (settings == null)
            return;

         var fontFamilyName = string.IsNullOrWhiteSpace(settings.FontFamily)
            ? (_note?.FontFamily ?? "Segoe UI")
            : settings.FontFamily;
         var fontFamily = CreateSafeFontFamily(fontFamilyName);

         var fontSize = settings.FontSize > 0
            ? settings.FontSize
            : (_note?.FontSize > 0 ? _note.FontSize : 14.0);
         var normalizedFontSize = NormalizeFontSize(fontSize, 14.0);

         TextRange range;

         if (settings.ApplyToSelection)
         {
            range = new TextRange(txtNoteContent.Selection.Start, txtNoteContent.Selection.End);
         } else if (settings.ApplyToEntireNote)
         {
            range = new TextRange(txtNoteContent.Document.ContentStart, txtNoteContent.Document.ContentEnd);
         } else
         {
            range = new TextRange(txtNoteContent.Document.ContentStart, txtNoteContent.Document.ContentEnd);

            if (_note != null)
            {
               _note.FontFamily = fontFamily.Source;
               _note.FontSize = normalizedFontSize;
            }
         }

         range.ApplyPropertyValue(TextElement.FontFamilyProperty, fontFamily);
         range.ApplyPropertyValue(TextElement.FontSizeProperty, normalizedFontSize);
         range.ApplyPropertyValue(TextElement.FontWeightProperty, settings.IsBold ? FontWeights.Bold : FontWeights.Normal);
         range.ApplyPropertyValue(TextElement.FontStyleProperty, settings.IsItalic ? FontStyles.Italic : FontStyles.Normal);
         TextDecorationCollection? decorations = null;
         if (settings.IsUnderline || settings.IsStrikeThrough)
         {
            decorations = new TextDecorationCollection();
            if (settings.IsUnderline)
               foreach (var d in TextDecorations.Underline) decorations.Add(d.Clone());
            if (settings.IsStrikeThrough)
               foreach (var d in TextDecorations.Strikethrough) decorations.Add(d.Clone());
         }
         range.ApplyPropertyValue(Inline.TextDecorationsProperty, decorations);
         range.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(settings.Color));

         if (_note != null)
            _note.LineHeightMultiplier = NormalizeLineHeightMultiplier(settings.LineHeightMultiplier);

         UpdateLineSpacing();
         UpdateCaretBrush();

         NoteTextChanged?.Invoke(this, EventArgs.Empty);
      }

      public void ApplyFontSettingsToEntireNote(FontSettingsData settings)
      {
         if (settings == null)
            return;

         ApplyFontSettings(new FontSettingsData
         {
            FontFamily = settings.FontFamily,
            FontSize = settings.FontSize,
            IsBold = settings.IsBold,
            IsItalic = settings.IsItalic,
            IsUnderline = settings.IsUnderline,
            Color = settings.Color,
            ApplyToSelection = false,
            ApplyToEntireNote = true,
            SetAsDefaultForNewNotes = false,
            ApplyToAllOpenNotes = false
         });
      }




      private void txtNoteTitle_select(object sender, MouseButtonEventArgs e)
      {
         // Ensure we have a note and the TextBox exists
         if (_note == null || txtNoteTitle == null) return;

         // Clear only if the current title is still the default
         // Prefer checking the TextBox to reflect the UI state; fallback to model if needed
         var isDefaultTitle =
            string.Equals(txtNoteTitle.Text, "Untitled", StringComparison.Ordinal) ||
            string.Equals(_note.Title, "Untitled", StringComparison.Ordinal);

         if (!isDefaultTitle) return;

         // Clear the title and focus the TextBox
         txtNoteTitle.Clear();
         txtNoteTitle.Focus();

         // Update the model in case binding isn't immediate
         _note.Title = string.Empty;

         // Mark as handled so the click doesn't re-trigger other handlers
         e.Handled = true;
      }

      private void NoteColors_SubmenuOpened(object sender, RoutedEventArgs e)
      {
         if (_note == null) return;

         var current = _note.ColorKey;

         foreach (var item in miNoteColors.Items.OfType<MenuItem>())
         {
            if (item.Tag is not string tag) continue;
            if (!Enum.TryParse(tag, out NoteColors.NoteColor key)) continue;

            var isCurrent = (key == current);

            item.IsEnabled = !isCurrent;

            // optional, but nice UX:
            item.IsCheckable = true;
            item.IsChecked = isCurrent;
         }
      }

      private void Menu_StickToWindow_Picker(object sender, RoutedEventArgs e)
      {
         var dlg = new StickIt.Sticky.StickyTargetPickerWindow { Owner = this };
         if (dlg.ShowDialog() != true || dlg.SelectedTarget == null) return;

         if (!EnterStuckMode2WithTarget(dlg.SelectedTarget, allowDesktopFallback: true))
            TryEnterMode2DesktopFallback();
      }



      public bool SnapToStickyTargetNow()
      {
         if (_isShuttingDown || !CanUseVisualTree()) return false;
         if (_noteStuckMode != 2) return false;

         // Ensure we have a live hwnd
         if (_stickyTarget == null || _stickyTarget.Hwnd == IntPtr.Zero)
         {
            if (!TryRebindStickyTarget()) return false;
         }
         if (_stickyTarget == null || _stickyTarget.Hwnd == IntPtr.Zero)
            return false;

         bool isDesktop = IsDesktopLikeTarget(_stickyTarget);
         var myHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

         // If it's the desktop, the target physically cannot move. 
         // Bypassing GetWindowRect here prevents DPI virtualization bugs from 
         // hallucinating desktop movement when snapping/maximizing other scaled windows.
         if (isDesktop)
         {
            var notePx = GetNoteTopLeftPx();
            int currentX = (int)Math.Round(notePx.X);
            int currentY = (int)Math.Round(notePx.Y);

            // Enforce Z-order without moving it
            StickIt.Sticky.Services.WindowMoveService.MoveWindow(myHwnd, currentX, currentY, IntPtr.Zero);
            return true;
         }

         // --- LOGIC FOR NON-DESKTOP TARGETS BELOW ---

         // Target rect (pixels)
         if (!StickIt.Sticky.Services.WindowRectService.TryGetWindowRect(_stickyTarget.Hwnd, out var tr))
            return false;

         // Skip work if target didn’t move (pixel compare)
         if (_lastTargetX.HasValue && _lastTargetY.HasValue &&
             _lastTargetX.Value == tr.X && _lastTargetY.Value == tr.Y)
         {
            return true;
         }

         _lastTargetX = tr.X;
         _lastTargetY = tr.Y;

         // Cache offset once (note-top-left relative to target-top-left, in pixels)
         if (!_stickyOffsetXPx.HasValue || !_stickyOffsetYPx.HasValue)
         {
            var notePx = GetNoteTopLeftPx();
            _stickyOffsetXPx = notePx.X - tr.X;
            _stickyOffsetYPx = notePx.Y - tr.Y;
         }

         int newX = (int)(tr.X + (int)Math.Round(_stickyOffsetXPx.Value));
         int newY = (int)(tr.Y + (int)Math.Round(_stickyOffsetYPx.Value));

         var dpi = VisualTreeHelper.GetDpi(this);
         var noteWidthPx = (int)Math.Round(Math.Max(1, Width * Math.Max(0.01, dpi.DpiScaleX)));
         var noteHeightPx = (int)Math.Round(Math.Max(1, Height * Math.Max(0.01, dpi.DpiScaleY)));

         // Keep some overlap with target so note cannot drift/stick outside the host bounds.
         int overlapMinX = (int)Math.Round(tr.X) - noteWidthPx + 1;
         int overlapMaxX = (int)Math.Round(tr.X) + (int)Math.Max(1, Math.Round(tr.Width)) - 1;
         int overlapMinY = (int)Math.Round(tr.Y) - noteHeightPx + 1;
         int overlapMaxY = (int)Math.Round(tr.Y) + (int)Math.Max(1, Math.Round(tr.Height)) - 1;

         if (newX < overlapMinX) newX = overlapMinX;
         if (newX > overlapMaxX) newX = overlapMaxX;
         if (newY < overlapMinY) newY = overlapMinY;
         if (newY > overlapMaxY) newY = overlapMaxY;

         // IMPORTANT: insert-after = target hwnd, so we stay above it without being Topmost
         StickIt.Sticky.Services.WindowMoveService.MoveWindow(myHwnd, newX, newY, _stickyTarget.Hwnd);

         return true;
      }



      private static bool IsDesktopLikeTarget(StickIt.Sticky.StickyTargetInfo? target)
      {
         if (target == null)
            return false;

         var cls = target.ClassName?.Trim();
         return string.Equals(cls, "Progman", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cls, "WorkerW", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cls, "#32769", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cls, "Desktop", StringComparison.OrdinalIgnoreCase);
      }




      #region LOCAL_AOT (Owned window)

      private void ApplyLocalAotOwner(IntPtr targetHwnd)
      {
         var myHwnd = new WindowInteropHelper(this).Handle;
         if (myHwnd == IntPtr.Zero || targetHwnd == IntPtr.Zero) return;

         // save previous owner once per "enter mode 2"
         if (_prevWin32Owner == IntPtr.Zero)
            _prevWin32Owner = StickIt.Services.LocalAotOwnerService.GetOwner(myHwnd);

         StickIt.Services.LocalAotOwnerService.SetOwner(myHwnd, targetHwnd);
      }

      private void ClearLocalAotOwner()
      {
         var myHwnd = new WindowInteropHelper(this).Handle;
         if (myHwnd == IntPtr.Zero) return;

         // restore previous owner (usually zero)
         StickIt.Services.LocalAotOwnerService.SetOwner(myHwnd, _prevWin32Owner);
         _prevWin32Owner = IntPtr.Zero;
      }

      #endregion




      private StickIt.Sticky.StickyTargetInfo? _stickyTarget;

      public void SetStickyTarget(StickIt.Persistence.StickyTargetPersist? p)
      {
         if (p == null)
         {
            _stickyTarget = null;
            _stickyOffsetXPx = null;
            _stickyOffsetYPx = null;
            _stickyTargetAnchorXPx = null;
            _stickyTargetAnchorYPx = null;
            return;
         }

         _stickyTarget = new StickIt.Sticky.StickyTargetInfo
         {
            Hwnd = IntPtr.Zero,
            ProcessId = p.ProcessId,
            ProcessName = p.ProcessName,
            WindowTitle = p.WindowTitle,
            ClassName = p.ClassName,
            CapturedUtc = p.CapturedUtc
         };

         _stickyOffsetXPx = p.OffsetX;
         _stickyOffsetYPx = p.OffsetY;
         _stickyTargetAnchorXPx = p.TargetAnchorX;
         _stickyTargetAnchorYPx = p.TargetAnchorY;
      }


      public StickIt.Persistence.StickyTargetPersist? GetStickyTargetPersist()
      {
         if (_stickyTarget == null) return null;

         return new StickIt.Persistence.StickyTargetPersist
         {
            ProcessId = _stickyTarget.ProcessId,
            ProcessName = _stickyTarget.ProcessName,
            WindowTitle = _stickyTarget.WindowTitle,
            ClassName = _stickyTarget.ClassName,
            CapturedUtc = _stickyTarget.CapturedUtc,
            OffsetX = _stickyOffsetXPx,
            OffsetY = _stickyOffsetYPx,
            TargetAnchorX = _stickyTargetAnchorXPx,
            TargetAnchorY = _stickyTargetAnchorYPx
         };
      }

      public void ClearStickyTarget()
      {
         StuckMode = 0;
         Topmost = false;
         _stickyTarget = null;
         _stickyOffsetXPx = null;
         _stickyOffsetYPx = null;
         _stickyTargetAnchorXPx = null;
         _stickyTargetAnchorYPx = null;
      }



      public bool TryRebindStickyTarget()
      {
         // Only meaningful in mode 2
         if (_noteStuckMode != 2) return false;

         // Nothing to rebind
         if (_stickyTarget == null) return false;

         // Already bound
         if (_stickyTarget.Hwnd != IntPtr.Zero) return true;

         // Build a persist-hints object from what we know (PID first, then title/class)
         var p = new StickIt.Persistence.StickyTargetPersist
         {
            ProcessId = _stickyTarget.ProcessId,
            ProcessName = _stickyTarget.ProcessName,
            WindowTitle = _stickyTarget.WindowTitle,
            ClassName = _stickyTarget.ClassName,
            CapturedUtc = _stickyTarget.CapturedUtc,

            // offsets are irrelevant to rebind, but keep them if present
            OffsetX = _stickyOffsetXPx,
            OffsetY = _stickyOffsetYPx,
            TargetAnchorX = _stickyTargetAnchorXPx,
            TargetAnchorY = _stickyTargetAnchorYPx
         };

         var resolved = StickIt.Sticky.StickyTargetResolver.TryResolve(p);
         if (resolved == null || resolved.Hwnd == IntPtr.Zero)
            return false;

         _stickyTarget = resolved;

         // Force next snap to actually move (don’t short-circuit on cached target coords)
         _lastTargetX = null;
         _lastTargetY = null;

         // Ensure hook is running for follow (once we add it fully, this becomes important)
         EnsureHookForStickyTarget();

         return true;
      }



      //GEMINI IN

      public bool StickToWindowUnderMe()
      {
         try
         {
            var myHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var center = GetNoteCenterPx();

            // 1, 2, 3) Get filtered candidates (already sorted in Z-order top-to-bottom)
            var candidates = StickIt.Sticky.Services.StickyHitTestService.GetValidTargetsUnderNote(center, myHwnd);

            // 4) Try primary target. If it fails, loop automatically tries the next valid window in Z-order.
            foreach (var target in candidates)
            {
               if (TryAttachToTarget(target))
                  return true;
            }

            // 4) Fallback to desktop if the candidate list is exhausted or empty
            if (TryEnterMode2DesktopFallback())
               return true;

            // 5) VERIFICATION FEEDBACK: If all fallbacks fail, revert to Mode 0
            return RevertToNotStuck();
         }
         catch
         {
            return RevertToNotStuck();
         }
      }

      private bool TryAttachToTarget(StickIt.Sticky.StickyTargetInfo target)
      {
         if (target == null || target.Hwnd == IntPtr.Zero) return false;

         try
         {
            _stickyTarget = target;
            UpdateStickyTargetAnchorFromCurrentNote();

            var isDesktopTarget = IsDesktopLikeTarget(target);

            if (isDesktopTarget)
               ClearLocalAotOwner();
            else
               ApplyLocalAotOwner(target.Hwnd);

            StuckMode = 2;
            Topmost = false;

            _stickyOffsetXPx = null;
            _stickyOffsetYPx = null;
            _lastTargetX = null;
            _lastTargetY = null;

            if (isDesktopTarget)
               StopHook();
            else
               EnsureHookForStickyTarget();

            UpdateStickyVisuals();

            // 5) VERIFICATION FEEDBACK: Ensure the snap actually worked and didn't throw the note off-screen
            var ok = SnapToStickyTargetNow();
            if (ok) ok = IsNoteVisibleOnAnyScreen();

            if (miSticky_SnapNow != null)
               miSticky_SnapNow.Header = ok ? "Snap to target now (OK)" : "Snap to target now (FAILED)";

            if (ok)
            {
               AppInstance.QueueSaveFromWindow();
               return true;
            }

            // Cleanup state if attachment failed so the next loop iteration starts fresh
            ClearLocalAotOwner();
            return false;
         }
         catch
         {
            ClearLocalAotOwner();
            return false;
         }
      }

      private bool EnterStuckMode2WithTarget(StickIt.Sticky.StickyTargetInfo target, bool allowDesktopFallback)
      {
         // Used primarily by Path 3 (The Picker Window)
         if (TryAttachToTarget(target))
            return true;

         if (allowDesktopFallback && TryEnterMode2DesktopFallback())
            return true;

         CenterOnPrimaryScreen();
         return RevertToNotStuck();
      }

      private bool TryEnterMode2DesktopFallback()
      {
         var center = GetNoteCenterPx();
         var desk = StickIt.Sticky.Services.DesktopTargetService.TryGetDesktopTargetAtPoint(
            (int)Math.Round(center.X),
            (int)Math.Round(center.Y));

         if (desk != null && desk.Hwnd != IntPtr.Zero)
         {
            if (TryAttachToTarget(desk))
               return true;
         }

         CenterOnPrimaryScreen();
         return RevertToNotStuck();
      }

      //GEMINI OUT







      private void Sticky_Pick_Click(object sender, RoutedEventArgs e)
      {
         var dlg = new StickIt.Sticky.StickyTargetPickerWindow { Owner = this };
         if (dlg.ShowDialog() != true || dlg.SelectedTarget == null)
            return;

         if (EnterStuckMode2WithTarget(dlg.SelectedTarget, allowDesktopFallback: true))
            return;

         TryEnterMode2DesktopFallback();
      }







      private void ApplyStuckMode(int mode)
      {
         if (mode < 0 || mode > 2) mode = 0;

         // if we are leaving mode 2, undo local AOT owner
         if (_noteStuckMode == 2 && mode != 2)
            ClearLocalAotOwner();

         StuckMode = mode;
         Topmost = (mode == 1);
         UpdateStickyVisuals();
      }

      public void ApplyLeadingPreference(double multiplier)
      {
         _lineHeightMultiplier = NormalizeLineHeightMultiplier(multiplier);

         if (_note != null)
            _note.LineHeightMultiplier = _lineHeightMultiplier;

         UpdateLineSpacing();
         txtNoteContent?.UpdateLayout();
      }

      private System.Windows.Media.Color GetDefaultTextColor()
      {
         if (_note == null)
            return System.Windows.Media.Colors.Black;

         var baseHex = NoteColors.Hex[_note.ColorKey];
         return ColorSchemeConverter.GetColor(_note.ColorKey.ToString(), baseHex, ColorComponent.Text);
      }

      private void ApplyDefaultInkAttributes()
      {
         if (inkLayer?.DefaultDrawingAttributes == null || _inkColorIsCustom)
            return;

         _inkColor = GetDefaultTextColor();
         ApplyInkDrawingAttributes();
         UpdateCaretBrush();
      }

      private void ApplyInkDrawingAttributes()
      {
         if (inkLayer == null)
            return;

         var size = Math.Max(0.5, _inkThicknessLevel) * 2.0;

         inkLayer.DefaultDrawingAttributes = new DrawingAttributes
         {
            Color = _inkColor,
            Width = size,
            Height = size,
            FitToCurve = true,
            IgnorePressure = false
         };

         _inkToolbarWindow?.SetColor(_inkColor);
         _inkToolbarWindow?.SetThickness(_inkThicknessLevel);
      }

      private void SetLiftVisualState(bool active)
      {
         if (btnPin == null)
            return;

         if (!active)
         {
            btnPin.ClearValue(System.Windows.Controls.Button.BackgroundProperty);
            btnPin.ClearValue(System.Windows.Controls.Button.BorderBrushProperty);
            btnPin.ClearValue(System.Windows.Controls.Button.ForegroundProperty);
            return;
         }

         var baseColor = GetCurrentNotePaperColor();
         var highlight = GetContrastingLiftColor(baseColor);
         var fg = GetPerceivedLuminance(highlight) >= 140 ? Colors.Black : Colors.White;

         btnPin.Background = new SolidColorBrush(highlight);
         btnPin.BorderBrush = new SolidColorBrush(highlight);
         btnPin.Foreground = new SolidColorBrush(fg);
      }

      private System.Windows.Media.Color GetCurrentNotePaperColor()
      {
         if (_note == null)
            return System.Windows.Media.Colors.Yellow;

         var baseHex = NoteColors.Hex[_note.ColorKey];
         try
         {
            return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(baseHex);
         }
         catch
         {
            return System.Windows.Media.Colors.Yellow;
         }
      }

      private static System.Windows.Media.Color GetContrastingLiftColor(System.Windows.Media.Color baseColor)
      {
         int r = 255 - baseColor.R;
         int g = 255 - baseColor.G;
         int b = 255 - baseColor.B;

         var max = Math.Max(r, Math.Max(g, b));
         if (max > 0 && max < 180)
         {
            var scale = 180.0 / max;
            r = (int)Math.Min(255, r * scale);
            g = (int)Math.Min(255, g * scale);
            b = (int)Math.Min(255, b * scale);
         }

         var candidate = System.Windows.Media.Color.FromRgb((byte)r, (byte)g, (byte)b);
         if (Math.Abs(GetPerceivedLuminance(candidate) - GetPerceivedLuminance(baseColor)) < 70)
            return GetPerceivedLuminance(baseColor) >= 140
               ? System.Windows.Media.Color.FromRgb(0, 120, 255)
               : System.Windows.Media.Color.FromRgb(255, 220, 0);

         return candidate;
      }

      private static double GetPerceivedLuminance(System.Windows.Media.Color color)
      {
         return (0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B);
      }

      private void ControlBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
      {
         if (e.ChangedButton != MouseButton.Left)
            return;

         if (_noteStuckMode == 2 && _mode2PreventManualMove && !_mode2LiftActive)
            return;

         DragMove();
      }

      private void BtnMinimize_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
      {
         if (_noteStuckMode != 2)
            return;

         e.Handled = true;

         _mode2LiftActive = true;
         SetLiftVisualState(true);
         try
         {
            DragMove();
         }
         finally
         {
            _mode2LiftActive = false;
            SetLiftVisualState(false);
            RecalculateStickyOffsetFromCurrentPosition();
            SnapToStickyTargetNow();
         }
      }

      private void TickMode2Follow()
      {
         if (_noteStuckMode != 2)
            return;

         if (_stickyTarget == null || _stickyTarget.Hwnd == IntPtr.Zero)
         {
            if (!TryRebindStickyTarget())
               HandleMode2Failure(hostMissing: true);
            return;
         }

         if (!IsWindow(_stickyTarget.Hwnd))
         {
            HandleMode2Failure(hostMissing: true);
            return;
         }

         if (_mode2MinimizeWithHost && IsIconic(_stickyTarget.Hwnd))
         {
            if (WindowState != WindowState.Minimized)
            {
               _autoMinimizedByHost = true;
               WindowState = WindowState.Minimized;
            }
            return;
         }

         if (_autoMinimizedByHost && WindowState == WindowState.Minimized && !IsIconic(_stickyTarget.Hwnd))
         {
            WindowState = WindowState.Normal;
            _autoMinimizedByHost = false;
         }

         if (!SnapToStickyTargetNow())
            HandleMode2Failure(hostMissing: false);
      }

      private void RecalculateStickyOffsetFromCurrentPosition()
      {
         if (_stickyTarget == null || _stickyTarget.Hwnd == IntPtr.Zero)
            return;

         if (!WindowRectService.TryGetWindowRect(_stickyTarget.Hwnd, out var tr))
            return;

         var notePx = GetNoteTopLeftPx();
         _stickyOffsetXPx = notePx.X - tr.X;
         _stickyOffsetYPx = notePx.Y - tr.Y;
         UpdateStickyTargetAnchorFromCurrentNote();
         _lastTargetX = null;
         _lastTargetY = null;
      }

      private void ClampMode2LiftToHostBounds()
      {
         if (_stickyTarget == null || _stickyTarget.Hwnd == IntPtr.Zero)
            return;

         if (IsDesktopLikeTarget(_stickyTarget))
         {
            ClampMode2LiftToVirtualDesktop();
            return;
         }

         if (!WindowRectService.TryGetWindowRect(_stickyTarget.Hwnd, out var tr))
            return;

         var dpi = VisualTreeHelper.GetDpi(this);
         var sx = Math.Max(0.01, dpi.DpiScaleX);
         var sy = Math.Max(0.01, dpi.DpiScaleY);

         var hostLeft = tr.X / sx;
         var hostTop = tr.Y / sy;
         var hostWidth = tr.Width / sx;
         var hostHeight = tr.Height / sy;

         var minLeft = hostLeft - Width + 1;
         var minTop = hostTop - Height + 1;
         var maxLeft = hostLeft + Math.Max(1, hostWidth) - 1;
         var maxTop = hostTop + Math.Max(1, hostHeight) - 1;

         Left = Math.Max(minLeft, Math.Min(maxLeft, Left));
         Top = Math.Max(minTop, Math.Min(maxTop, Top));
      }

      private void ClampMode2LiftToVirtualDesktop()
      {
         var dpi = VisualTreeHelper.GetDpi(this);
         var sx = Math.Max(0.01, dpi.DpiScaleX);
         var sy = Math.Max(0.01, dpi.DpiScaleY);
         var vs = System.Windows.Forms.SystemInformation.VirtualScreen;

         var minLeft = vs.Left / sx;
         var minTop = vs.Top / sy;
         var maxLeft = (vs.Right / sx) - Width;
         var maxTop = (vs.Bottom / sy) - Height;

         Left = Math.Max(minLeft, Math.Min(maxLeft, Left));
         Top = Math.Max(minTop, Math.Min(maxTop, Top));
      }

      private void HandleMode2Failure(bool hostMissing)
      {
         if (hostMissing && _mode2CloseNoteWhenHostCloses)
         {
            Close();
            return;
         }

         switch (_mode2HostMissingAction)
         {
            case Mode2HostMissingAction.StickToDesktop:
               if (TryEnterMode2DesktopFallback())
                  return;
               break;
            case Mode2HostMissingAction.SwitchToMode0:
               RevertToNotStuck();
               return;
            case Mode2HostMissingAction.SwitchToMode1:
            default:
               ApplyStuckMode(1);
               _stickyTarget = null;
               _stickyOffsetXPx = null;
               _stickyOffsetYPx = null;
               StopHook();
               AppInstance.QueueSaveFromWindow();
               return;
         }

         RevertToNotStuck();
      }

      private void Sticky_NotStuck_Click(object sender, RoutedEventArgs e)
      {
         ApplyStuckMode(0);
         _stickyTarget = null;
         _stickyOffsetXPx = null;
         _stickyOffsetYPx = null;
         _stickyTargetAnchorXPx = null;
         _stickyTargetAnchorYPx = null;
         AppInstance.QueueSaveFromWindow();
      }

      private void Sticky_AOT_Click(object sender, RoutedEventArgs e)
      {
         ApplyStuckMode(1);
         // Leaving mode 2 -> clear target/offset
         _stickyTarget = null;
         _stickyOffsetXPx = null;
         _stickyOffsetYPx = null;
         _stickyTargetAnchorXPx = null;
         _stickyTargetAnchorYPx = null;
         AppInstance.QueueSaveFromWindow();
         StopHook();
      }




      private void Sticky_SnapNow_Click(object sender, RoutedEventArgs e)
      {
         var ok = SnapToStickyTargetNow();
         miSticky_SnapNow.Header = ok ? "Snap to target now (OK)" : "Snap to target now (FAILED)";
         if (ok) AppInstance.QueueSaveFromWindow();
      }



      private void Sticky_SubmenuOpened(object sender, RoutedEventArgs e)
      {
         // Checkmarks
         miSticky_NotStuck.IsCheckable = true;
         miSticky_AOT.IsCheckable = true;
         miSticky_Auto.IsCheckable = true;
         miSticky_Pick.IsCheckable = true;

         miSticky_Auto.IsChecked = (_noteStuckMode == 2);
         miSticky_Pick.IsChecked = false; // picker is an action, not a state
         miSticky_NotStuck.IsChecked = (_noteStuckMode == 0);
         miSticky_AOT.IsChecked = (_noteStuckMode == 1);

         // When stuck-to-window, make “Not stuck” read as “Un-stick note”
         miSticky_NotStuck.Header = (_noteStuckMode == 2) ? "Un-stick note" : "Not stuck";

         // Optional: disable AOT checkbox while in mode 2? (your call)
         // I’d leave it enabled; it becomes an explicit mode switch.
         miSticky_SnapNow.IsEnabled = (_noteStuckMode == 2);

      }
      private void UpdateStickyVisuals()
      {
         if (NoteChrome == null) return;

         if (_dropShadowEnabled)
            NoteChrome.ClearValue(Border.EffectProperty);
         else
            NoteChrome.Effect = null;

         if (StickyAccent != null)
         {
            StickyAccent.ClearValue(Border.BorderBrushProperty);
            StickyAccent.ClearValue(Border.BorderThicknessProperty);
         }

         if (StickyOverlay != null)
         {
            StickyOverlay.ClearValue(Border.BorderBrushProperty);
            StickyOverlay.ClearValue(Border.BorderThicknessProperty);
         }

         if (!_noteBordersEnabled)
         {
            NoteChrome.BorderThickness = new Thickness(1);
            NoteChrome.BorderBrush = NoteChrome.Background;

            if (StickyAccent != null)
            {
               StickyAccent.BorderThickness = new Thickness(0);
               StickyAccent.BorderBrush = System.Windows.Media.Brushes.Transparent;
            }

            if (StickyOverlay != null)
            {
               StickyOverlay.BorderThickness = new Thickness(0);
               StickyOverlay.BorderBrush = System.Windows.Media.Brushes.Transparent;
            }
         } else
         {
            NoteChrome.BorderThickness = new Thickness(2);

            var converter = TryFindResource("NoteComponentBrush") as System.Windows.Data.IValueConverter;
            if (converter != null)
            {
               var borderBinding = new System.Windows.Data.Binding("ColorKey")
               {
                  Converter = converter,
                  ConverterParameter = "Buttons"
               };
               NoteChrome.SetBinding(Border.BorderBrushProperty, borderBinding);
            }
         }
      }

      private void TryApplyAutomaticListFormatting()
      {
         if (_suppressAutoListHandling || txtNoteContent == null)
            return;

         var current = txtNoteContent.CaretPosition?.Paragraph;
         var previous = current?.PreviousBlock as Paragraph;
         if (current == null || previous == null)
            return;

         if (TryFormatBulletPair(previous, current))
            return;

         TryFormatNumberPair(previous, current);
      }

      private bool TryPropagateListMarkerOnEnter()
      {
         if (!_autoListFormattingEnabled || txtNoteContent == null)
            return false;

         if (!txtNoteContent.Selection.IsEmpty)
         {
            _consecutiveEmptyListEnterCount = 0;
            return false;
         }

         var current = txtNoteContent.CaretPosition?.Paragraph;
         if (current == null)
         {
            _consecutiveEmptyListEnterCount = 0;
            return false;
         }

         var text = GetParagraphText(current);
         var spacing = new string(' ', Math.Max(1, _autoListSpacesAfterMarker));

         string? nextPrefix = null;
         bool isNumbered = false;
         string currentContent = string.Empty;

         if (TryParseFormattedBulletLine(text, out var bulletIndent, out var bulletContent))
         {
            nextPrefix = $"{bulletIndent}{_autoListBulletSymbol}{spacing}";
            currentContent = bulletContent;
         } else if (TryParseFormattedNumberLine(text, out var numberIndent, out var currentNumber, out var numberContent))
         {
            nextPrefix = $"{numberIndent}{currentNumber + 1}{_autoListNumberSuffix}{spacing}";
            isNumbered = true;
            currentContent = numberContent;
         }

         if (nextPrefix == null)
         {
            _consecutiveEmptyListEnterCount = 0;
            return false;
         }

         if (!IsCaretAtParagraphEnd(current))
         {
            _consecutiveEmptyListEnterCount = 0;
            return false;
         }

         if (string.IsNullOrWhiteSpace(currentContent))
         {
            _consecutiveEmptyListEnterCount++;
            if (_consecutiveEmptyListEnterCount >= MaxConsecutiveEmptyListEnterBeforeStop)
            {
               _consecutiveEmptyListEnterCount = 0;
               return false;
            }
         } else
         {
            _consecutiveEmptyListEnterCount = 0;
         }

         var caretLineHeight = GetLineHeightDip(_note?.FontSize ?? txtNoteContent.FontSize);
         _suppressAutoListHandling = true;
         try
         {
            EditingCommands.EnterParagraphBreak.Execute(null, txtNoteContent);

            var next = txtNoteContent.CaretPosition?.Paragraph;
            if (next == null)
               return true;

            var carriedText = GetParagraphText(next);
            var nextText = string.IsNullOrWhiteSpace(carriedText)
               ? nextPrefix
               : $"{nextPrefix}{carriedText}";

            SetParagraphText(next, nextText, isNumbered);
            next.LineHeight = caretLineHeight;

            var caret = GetCaretAfterPrefix(next, nextPrefix.Length)
               ?? next.ContentEnd.GetInsertionPosition(LogicalDirection.Backward)
               ?? next.ContentEnd;

            txtNoteContent.Selection.Select(caret, caret);
            new TextRange(caret, caret).ApplyPropertyValue(Inline.TextDecorationsProperty, null);
         }
         finally
         {
            _suppressAutoListHandling = false;
         }

         return true;
      }

      private bool IsCaretAtParagraphEnd(Paragraph paragraph)
      {
         if (txtNoteContent?.CaretPosition == null)
            return false;

         var paragraphEnd = paragraph.ContentEnd.GetInsertionPosition(LogicalDirection.Backward) ?? paragraph.ContentEnd;
         return txtNoteContent.CaretPosition.CompareTo(paragraphEnd) >= 0;
      }

      private static TextPointer? GetCaretAfterPrefix(Paragraph paragraph, int prefixLength)
      {
         var run = paragraph.Inlines.OfType<Run>().FirstOrDefault();
         if (run == null)
            return paragraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward) ?? paragraph.ContentStart;

         var runStart = run.ContentStart.GetInsertionPosition(LogicalDirection.Forward) ?? run.ContentStart;
         var safeOffset = Math.Max(0, Math.Min(prefixLength, run.Text?.Length ?? 0));
         var target = runStart.GetPositionAtOffset(safeOffset, LogicalDirection.Forward);

         return target?.GetInsertionPosition(LogicalDirection.Forward)
            ?? run.ContentEnd.GetInsertionPosition(LogicalDirection.Backward)
            ?? run.ContentEnd;
      }

      private bool TryHandleBackspaceOnEmptyListItem()
      {
         if (!_autoListFormattingEnabled || txtNoteContent == null || !txtNoteContent.Selection.IsEmpty)
            return false;

         var paragraph = txtNoteContent.CaretPosition?.Paragraph;
         if (paragraph == null)
            return false;

         if (!IsCaretAtParagraphEnd(paragraph))
            return false;

         var text = GetParagraphText(paragraph);
         var isEmptyBullet = TryParseFormattedBulletLine(text, out _, out var bulletContent) && string.IsNullOrWhiteSpace(bulletContent);
         var isEmptyNumber = TryParseFormattedNumberLine(text, out _, out _, out var numberContent) && string.IsNullOrWhiteSpace(numberContent);

         if (!isEmptyBullet && !isEmptyNumber)
            return false;

         SetParagraphText(paragraph, string.Empty, isNumbered: false);
         var caret = paragraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward) ?? paragraph.ContentStart;
         txtNoteContent.Selection.Select(caret, caret);
         return true;
      }

      private bool TryHandleBulletRevert(Paragraph previous, Paragraph current)
      {
         if (_lastAutoBulletPair?.First != previous || _lastAutoBulletPair.Second != current)
            return false;

         var currentText = GetParagraphText(current);
         if (currentText.StartsWith(_autoListBulletSymbol, StringComparison.Ordinal))
            return false;

         var marker = _lastAutoBulletPair.Marker;
         var spacing = new string(' ', Math.Max(1, _autoListSpacesAfterMarker));
         SetParagraphText(previous, $"{marker}{spacing}{StripBulletPrefix(GetParagraphText(previous))}", isNumbered: false);
         SetParagraphText(current, $"{marker}{spacing}{StripBulletPrefix(currentText)}", isNumbered: false);
         _lastAutoBulletPair = null;
         return true;
      }

      private bool TryFormatBulletPair(Paragraph previous, Paragraph current)
      {
         var previousText = GetParagraphText(previous);
         var currentText = GetParagraphText(current);
         var caretInCurrent = txtNoteContent?.CaretPosition?.Paragraph == current;

         if (TryParseFormattedBulletLine(previousText, out var existingPrevIndent, out _)
            && TryParseFormattedBulletLine(currentText, out var existingCurrIndent, out _)
            && string.Equals(existingPrevIndent, existingCurrIndent, StringComparison.Ordinal))
            return false;

         if (!TryParseBulletTrigger(previousText, out var prevIndent, out var prevMarker, out var prevContent))
            return false;

         if (!TryParseBulletTrigger(currentText, out var currIndent, out var currMarker, out var currContent))
            return false;

         if (prevMarker != currMarker || !string.Equals(prevIndent, currIndent, StringComparison.Ordinal))
            return false;

         var spacing = new string(' ', Math.Max(1, _autoListSpacesAfterMarker));
         var p1 = $"{prevIndent}{_autoListBulletSymbol}{spacing}{prevContent}";
         var p2 = $"{currIndent}{_autoListBulletSymbol}{spacing}{currContent}";

         SetParagraphText(previous, p1, isNumbered: false);
         SetParagraphText(current, p2, isNumbered: false);
         _lastAutoBulletPair = new AutoBulletPairState { First = previous, Second = current, Marker = prevMarker };
         if (caretInCurrent && txtNoteContent != null)
         {
            var caret = current.ContentEnd.GetInsertionPosition(LogicalDirection.Backward)
               ?? current.ContentEnd;
            txtNoteContent.Selection.Select(caret, caret);
            new TextRange(caret, caret).ApplyPropertyValue(Inline.TextDecorationsProperty, null);
         }
         if (_todoTitleArmed)
         {
            _todoTemplateApplied = true;
            _todoTitleArmed = false;
         }
         return true;
      }

      private bool TryFormatNumberPair(Paragraph previous, Paragraph current)
      {
         var prevText = GetParagraphText(previous);
         var currText = GetParagraphText(current);
         var caretInCurrent = txtNoteContent?.CaretPosition?.Paragraph == current;

         if (TryParseFormattedNumberLine(prevText, out var existingPrevIndent, out var existingPrevNumber, out _)
            && TryParseFormattedNumberLine(currText, out var existingCurrIndent, out var existingCurrNumber, out _)
            && string.Equals(existingPrevIndent, existingCurrIndent, StringComparison.Ordinal)
            && existingCurrNumber == existingPrevNumber + 1)
            return false;

         if (TryParseHashTrigger(prevText, out var prevHashIndent, out var prevHashContent)
            && TryParseHashTrigger(currText, out var currHashIndent, out var currHashContent)
            && string.Equals(prevHashIndent, currHashIndent, StringComparison.Ordinal))
         {
            var spacingHash = new string(' ', Math.Max(1, _autoListSpacesAfterMarker));
            SetParagraphText(previous, $"{prevHashIndent}1{_autoListNumberSuffix}{spacingHash}{prevHashContent}", isNumbered: true);
            SetParagraphText(current, $"{currHashIndent}2{_autoListNumberSuffix}{spacingHash}{currHashContent}", isNumbered: true);
            if (caretInCurrent && txtNoteContent != null)
            {
               var caret = current.ContentEnd.GetInsertionPosition(LogicalDirection.Backward)
                  ?? current.ContentEnd;
               txtNoteContent.Selection.Select(caret, caret);
               new TextRange(caret, caret).ApplyPropertyValue(Inline.TextDecorationsProperty, null);
            }
            if (_todoTitleArmed)
            {
               _todoTemplateApplied = true;
               _todoTitleArmed = false;
            }
            return true;
         }

         if (!TryParseNumberTrigger(prevText, out var prevIndent, out var prevNumber, out var prevContent))
            return false;

         if (!TryParseNumberTrigger(currText, out var currIndent, out var currNumber, out var currContent))
            return false;

         if (currNumber != prevNumber + 1 || !string.Equals(prevIndent, currIndent, StringComparison.Ordinal))
            return false;

         var spacing = new string(' ', Math.Max(1, _autoListSpacesAfterMarker));
         SetParagraphText(previous, $"{prevIndent}{prevNumber}{_autoListNumberSuffix}{spacing}{prevContent}", isNumbered: true);
         SetParagraphText(current, $"{currIndent}{currNumber}{_autoListNumberSuffix}{spacing}{currContent}", isNumbered: true);
         if (caretInCurrent && txtNoteContent != null)
         {
            var caret = current.ContentEnd.GetInsertionPosition(LogicalDirection.Backward)
               ?? current.ContentEnd;
            txtNoteContent.Selection.Select(caret, caret);
            new TextRange(caret, caret).ApplyPropertyValue(Inline.TextDecorationsProperty, null);
         }
         if (_todoTitleArmed)
         {
            _todoTemplateApplied = true;
            _todoTitleArmed = false;
         }
         return true;
      }

      private bool TryParseBulletTrigger(string text, out string indent, out char marker, out string content)
      {
         indent = string.Empty;
         marker = '\0';
         content = string.Empty;
         if (string.IsNullOrWhiteSpace(text))
            return false;

         var match = BulletTriggerRegex.Match(text);
         if (!match.Success)
            return false;

         indent = match.Groups[1].Value;
         marker = match.Groups[2].Value[0];
         content = match.Groups[3].Value ?? string.Empty;
         return true;
      }

      private bool TryParseNumberTrigger(string text, out string indent, out int number, out string content)
      {
         indent = string.Empty;
         number = 0;
         content = string.Empty;
         if (string.IsNullOrWhiteSpace(text))
            return false;

         var match = NumberTriggerRegex.Match(text);
         if (!match.Success)
            return false;

         indent = match.Groups[1].Value;
         if (!int.TryParse(match.Groups[2].Value, out number))
            return false;

         content = match.Groups[4].Value ?? string.Empty;
         return true;
      }

      private static bool TryParseHashTrigger(string text, out string indent, out string content)
      {
         indent = string.Empty;
         content = string.Empty;
         if (string.IsNullOrWhiteSpace(text))
            return false;

         var match = HashTriggerRegex.Match(text);
         if (!match.Success)
            return false;

         indent = match.Groups[1].Value;
         content = match.Groups[2].Value ?? string.Empty;
         return true;
      }

      private bool TryParseFormattedBulletLine(string text, out string indent, out string content)
      {
         indent = string.Empty;
         content = string.Empty;
         if (string.IsNullOrWhiteSpace(_autoListBulletSymbol))
            return false;

         var escapedBullet = Regex.Escape(_autoListBulletSymbol);
         var match = Regex.Match(text, $@"^(\s*){escapedBullet}\s+(.*)$");
         if (!match.Success)
            return false;

         indent = match.Groups[1].Value;
         content = match.Groups[2].Value ?? string.Empty;
         return true;
      }

      private bool TryParseFormattedNumberLine(string text, out string indent, out int number, out string content)
      {
         indent = string.Empty;
         number = 0;
         content = string.Empty;

         var suffix = Regex.Escape(string.IsNullOrWhiteSpace(_autoListNumberSuffix) ? "." : _autoListNumberSuffix);
         var match = Regex.Match(text, $@"^(\s*)(\d+){suffix}\s+(.*)$");
         if (!match.Success)
            return false;

         indent = match.Groups[1].Value;
         if (!int.TryParse(match.Groups[2].Value, out number))
            return false;

         content = match.Groups[3].Value ?? string.Empty;
         return true;
      }

      private static TextRange GetParagraphContentRange(Paragraph paragraph)
      {
         var end = paragraph.ContentEnd.GetInsertionPosition(LogicalDirection.Backward) ?? paragraph.ContentEnd;
         return new TextRange(paragraph.ContentStart, end);
      }

      //private void CleanupStrikethroughOnEmptyParagraphs()
      //{
      //   if (txtNoteContent?.Document == null)
      //      return;

      //   bool changed = false;

      //   foreach (var paragraph in txtNoteContent.Document.Blocks.OfType<Paragraph>())
      //   {
      //      if (!string.IsNullOrWhiteSpace(GetParagraphText(paragraph)))
      //         continue;

      //      var range = GetParagraphContentRange(paragraph);
      //      var decorations = GetDecorationsOrEmpty(range.GetPropertyValue(Inline.TextDecorationsProperty));
      //      if (!HasDecorationLocation(decorations, TextDecorationLocation.Strikethrough))
      //         continue;

      //      RemoveDecorationLocation(decorations, TextDecorationLocation.Strikethrough);
      //      range.ApplyPropertyValue(Inline.TextDecorationsProperty, decorations.Count == 0 ? null : decorations);
      //      changed = true;
      //   }

      //   if (changed)
      //   {
      //      var caret = txtNoteContent.CaretPosition;
      //      if (caret != null)
      //         new TextRange(caret, caret).ApplyPropertyValue(Inline.TextDecorationsProperty, null);
      //   }
      //}

      private string GetParagraphText(Paragraph paragraph)
      {
         return new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.TrimEnd('\r', '\n');
      }

      private void SetParagraphText(Paragraph paragraph, string text, bool isNumbered)
      {
         _suppressAutoListHandling = true;
         try
         {
            ApplyParagraphTemplateStyle(paragraph, isNumbered ? _autoListNumberTemplateRtf : _autoListBulletTemplateRtf, text);
         }
         finally
         {
            _suppressAutoListHandling = false;
         }
      }

      private void ApplyParagraphTemplateStyle(Paragraph paragraph, string? templateRtf, string text)
      {
         var style = ExtractTemplateStyle(paragraph, templateRtf);
         paragraph.Margin = style.Margin;
         paragraph.TextIndent = style.TextIndent;
         paragraph.LineHeight = GetLineHeightDip(style.FontSize);
         paragraph.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;

         paragraph.Inlines.Clear();
         var run = new Run(text)
         {
            FontFamily = style.FontFamily,
            FontSize = style.FontSize,
            FontWeight = style.FontWeight,
            FontStyle = style.FontStyle
         };

         if (style.TextDecorations != null)
            run.TextDecorations = style.TextDecorations;

         if (style.Foreground != null)
            run.Foreground = style.Foreground;

         paragraph.Inlines.Add(run);
      }

      private double GetLineHeightDip(double fontSizeDip)
      {
         var multiplier = NormalizeLineHeightMultiplier(_note?.LineHeightMultiplier ?? _lineHeightMultiplier);
         return Math.Max(1.0, fontSizeDip * multiplier);
      }

      private void UpdateLineSpacing()
      {
         if (txtNoteContent?.Document == null)
            return;

         var fontSize = _note?.FontSize > 0 ? _note.FontSize : txtNoteContent.FontSize;
         txtNoteContent.Document.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;

         foreach (var paragraph in txtNoteContent.Document.Blocks.OfType<Paragraph>())
         {
            var run = paragraph.Inlines.OfType<Run>().FirstOrDefault();
            var paragraphFontSize = run?.FontSize ?? paragraph.FontSize;
            if (double.IsNaN(paragraphFontSize) || paragraphFontSize <= 0)
               paragraphFontSize = fontSize > 0 ? fontSize : 14.0;

            var lineHeight = GetLineHeightDip(paragraphFontSize);
            paragraph.LineHeight = lineHeight;
            paragraph.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
         }

         txtNoteContent.UpdateLayout();
      }

      private void UpdateCaretBrush()
      {
         if (txtNoteContent == null)
            return;

         var color = GetCurrentCaretColor();
         var brush = new SolidColorBrush(color);
         brush.Freeze();
         CaretBrush = brush;
      }

      private System.Windows.Media.Color GetCurrentCaretColor()
      {
         var selection = txtNoteContent?.Selection;
         if (selection != null)
         {
            var fg = selection.GetPropertyValue(TextElement.ForegroundProperty);
            if (fg is SolidColorBrush solid)
               return solid.Color;
         }

         if (txtNoteContent?.Foreground is SolidColorBrush fallback)
            return fallback.Color;

         return GetDefaultTextColor();
      }

      private ListTemplateStyle ExtractTemplateStyle(Paragraph paragraph, string? templateRtf)
      {
         var style = ExtractCurrentParagraphStyle(paragraph);
         if (string.IsNullOrWhiteSpace(templateRtf))
            return style;

         try
         {
            var doc = new FlowDocument();
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(templateRtf));
            new TextRange(doc.ContentStart, doc.ContentEnd).Load(ms, System.Windows.DataFormats.Rtf);

            var para = doc.Blocks.OfType<Paragraph>().FirstOrDefault();
            if (para == null)
               return style;

            style.Margin = para.Margin;
            style.TextIndent = para.TextIndent;

            var sampleRun = para.Inlines.OfType<Run>().FirstOrDefault();
            if (sampleRun == null)
               return style;

            style.FontFamily = sampleRun.FontFamily;
            style.FontSize = sampleRun.FontSize;
            style.FontWeight = sampleRun.FontWeight;
            style.FontStyle = sampleRun.FontStyle;
            style.TextDecorations = sampleRun.TextDecorations;
            style.Foreground = sampleRun.Foreground;
         }
         catch
         {
            // keep defaults
         }

         return style;
      }

      private static ListTemplateStyle ExtractCurrentParagraphStyle(Paragraph paragraph)
      {
         var style = new ListTemplateStyle
         {
            Margin = paragraph.Margin,
            TextIndent = paragraph.TextIndent
         };

         var sampleRun = paragraph.Inlines.OfType<Run>().FirstOrDefault();
         if (sampleRun == null)
            return style;

         style.FontFamily = sampleRun.FontFamily;
         style.FontSize = sampleRun.FontSize;
         style.FontWeight = sampleRun.FontWeight;
         style.FontStyle = sampleRun.FontStyle;
         style.TextDecorations = sampleRun.TextDecorations;
         style.Foreground = sampleRun.Foreground;
         return style;
      }

      private string StripBulletPrefix(string text)
      {
         var trimmed = text.TrimStart();
         if (trimmed.StartsWith(_autoListBulletSymbol, StringComparison.Ordinal))
            return trimmed[_autoListBulletSymbol.Length..].TrimStart();

         return text.TrimStart();
      }

      public void ApplyPreferences(StickIt.Persistence.AppPreferences prefs)
      {
         _dropShadowEnabled = prefs.EnableDropShadow;
         _noteBordersEnabled = prefs.EnableNoteBorders;
         _snapToGridEnabled = prefs.SnapNotesToGrid;
         _externalNoteImportExportEnabled = prefs.EnableExternalNoteImportExport;
         _autoListFormattingEnabled = prefs.EnableAutoListFormatting;
         _autoListBulletSymbol = string.IsNullOrWhiteSpace(prefs.AutoListBulletSymbol) ? "•" : prefs.AutoListBulletSymbol;
         _autoListSpacesAfterMarker = Math.Max(1, Math.Min(4, prefs.AutoListSpacesAfterMarker));
         _autoListNumberSuffix = string.IsNullOrWhiteSpace(prefs.AutoListNumberSuffix) ? "." : prefs.AutoListNumberSuffix;
         _autoListBulletTemplateRtf = prefs.AutoListBulletTemplateRtf;
         _autoListNumberTemplateRtf = prefs.AutoListNumberTemplateRtf;
         _todoTemplateRtf = prefs.TodoTemplateRtf;
         _enableTodoTitleTrigger = prefs.EnableTodoTitleTrigger;
         if (!_enableTodoTitleTrigger)
         {
            _todoTitleArmed = false;
            _todoTemplateApplied = false;
         }
         _mode2PreventManualMove = prefs.Mode2PreventManualMove;
         _mode2MinimizeWithHost = prefs.Mode2MinimizeWithHost;
         _mode2CloseNoteWhenHostCloses = prefs.Mode2CloseNoteWhenHostCloses;
         _mode2HostMissingAction = prefs.Mode2HostMissingAction;

         if (txtNoteTitle != null)
         {
            try
            {
               txtNoteTitle.FontFamily = new System.Windows.Media.FontFamily(prefs.TitleFontFamily);
            }
            catch
            {
               txtNoteTitle.FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
            }

            txtNoteTitle.FontSize = prefs.TitleFontSize;
            txtNoteTitle.FontWeight = prefs.TitleFontBold ? FontWeights.Bold : FontWeights.Normal;
         }

         if (_note != null)
            _note.LineHeightMultiplier = NormalizeLineHeightMultiplier(prefs.BodyLineHeightMultiplier); // No-op placeholder

         UpdateLineSpacing();
         UpdateCaretBrush();

         UpdateStickyVisuals();
      }

      private void NoteWindow_Closing(object? sender, CancelEventArgs e)
      {
         var app = AppInstance;
         if (app.IsShuttingDown)
            return;

         if (!app.Preferences.ConfirmOnDelete)
            return;

         var result = System.Windows.MessageBox.Show(
            "Delete this note?",
            "Confirm delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

         if (result != MessageBoxResult.Yes)
            e.Cancel = true;
      }

      private void NoteWindow_LocationChanged(object? sender, EventArgs e)
      {
         if (_noteStuckMode == 2 && _mode2PreventManualMove)
         {
            if (_mode2LiftActive)
            {
               ClampMode2LiftToHostBounds();
               RecalculateStickyOffsetFromCurrentPosition();
            } else
            {
               SnapToStickyTargetNow();
            }
         }

         if (_inkToolbarSnapEnabled && _inkModeEnabled && WindowState != WindowState.Minimized)
            PositionInkToolbar();

         if (_snapToGridEnabled && !_snapGridAdjusting && _noteStuckMode != 2)
         {
            double snapGridSize = GetSnapGridSize();
            double snappedLeft = Math.Round(Left / snapGridSize) * snapGridSize;
            double snappedTop = Math.Round(Top / snapGridSize) * snapGridSize;

            if (Math.Abs(snappedLeft - Left) >= 0.5 || Math.Abs(snappedTop - Top) >= 0.5)
            {
               _snapGridAdjusting = true;
               try
               {
                  Left = snappedLeft;
                  Top = snappedTop;
               }
               finally
               {
                  _snapGridAdjusting = false;
               }
            }
         }
      }

      private void NoteWindow_SizeChanged(object sender, SizeChangedEventArgs e)
      {
         if (_inkToolbarSnapEnabled && _inkModeEnabled && WindowState != WindowState.Minimized)
            PositionInkToolbar();
      }

      private void NoteWindow_StateChanged(object? sender, EventArgs e)
      {
         ApplyInkMode();
      }

      private static double GetSnapGridSize()
      {
         return SystemParameters.PrimaryScreenWidth <= 1920 || SystemParameters.PrimaryScreenHeight <= 1080
            ? SnapGridSizeLowResolution
            : SnapGridSizeDefault;
      }



      private void EnsureHookForStickyTarget()
      {
         // Only needed in Mode 2 and only if we have a target
         if (_noteStuckMode != 2) { StopHook(); return; }
         if (_stickyTarget == null) { StopHook(); return; }

         if (_winEventHook == null)
            _winEventHook = new StickIt.Sticky.Services.StickyWinEventHookService();

         if (!_winEventSubscribed)
         {
            _winEventHook.TargetMoved += WinEvent_TargetMoved;
            _winEventSubscribed = true;
         }

         _winEventHook.Hook();
      }

      private void WinEvent_TargetMoved(IntPtr hwnd)
      {
         if (_isShuttingDown) return;
         if (_noteStuckMode != 2) return;
         if (_stickyTarget == null) return;
         if (_stickyTarget.Hwnd == IntPtr.Zero) return;

         // Must marshal to UI thread
         Dispatcher.BeginInvoke(new Action(() =>
         {
            if (_isShuttingDown || !CanUseVisualTree()) return;
            if (_noteStuckMode != 2) return;
            if (_stickyTarget == null) return;
            if (_stickyTarget.Hwnd == IntPtr.Zero) return;

            // Foreground/reorder events can arrive with hwnd != target.
            // For sticky mode 2, always re-assert our position just above the target.
            if (hwnd == _stickyTarget.Hwnd)
               RequestStickySnap();
            else
               SnapToStickyTargetNow();
         }));
      }

      private void StopHook()
      {
         if (_winEventHook == null) return;

         if (_winEventSubscribed)
         {
            _winEventHook.TargetMoved -= WinEvent_TargetMoved;
            _winEventSubscribed = false;
         }

         _winEventHook.Dispose();
         _winEventHook = null;
      }








      // +++++++++ +++++++++++++ BEGIN TEMP STICKY TARGET DEBUGGING ++++++++++++++++++++
      private void Menu_RebindStickyTarget(object sender, RoutedEventArgs e)
      {
         var ok = TryRebindStickyTarget();
         // Optional: you can show a tiny MessageBox if you want.
         // If you do, keep it subtle (or just do nothing).
         if (ok) AppInstance.QueueSaveFromWindow();
      }

      // +++++++++ +++++++++++++ END TEMP STICKY TARGET DEBUGGING ++++++++++++++++++++

   }
}
