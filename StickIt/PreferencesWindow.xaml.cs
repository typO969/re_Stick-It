using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

using Microsoft.Win32;

using StickIt.Persistence;
using StickIt.Services;

namespace StickIt
{
   public partial class PreferencesWindow : Window
   {
      private delegate bool SyncOperation(out string message);

      private readonly PreferencesViewModel _viewModel;
      private bool _suppressSandboxSync;

      public PreferencesWindow(AppPreferences preferences)
      {
         InitializeComponent();
         AppThemeService.ApplyDialogTheme(this);
         _viewModel = PreferencesViewModel.FromPreferences(preferences);
         DataContext = _viewModel;
         InitializeSandboxEditors();


      }

      private App AppInstance => (App)System.Windows.Application.Current;

      private void Ok_Click(object sender, RoutedEventArgs e)
      {
         ApplyChanges();
         Close();
      }

      private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

      private void DesktopArea_Click(object sender, RoutedEventArgs e)
      {
         var dlg = new DesktopAreaWindow(_viewModel.DesktopAreaLeft, _viewModel.DesktopAreaTop, _viewModel.DesktopAreaWidth, _viewModel.DesktopAreaHeight)
         {
            Owner = this
         };

         if (dlg.ShowDialog() != true || dlg.SelectedArea == null)
            return;

         _viewModel.DesktopAreaLeft = dlg.SelectedArea.Left;
         _viewModel.DesktopAreaTop = dlg.SelectedArea.Top;
         _viewModel.DesktopAreaWidth = dlg.SelectedArea.Width;
         _viewModel.DesktopAreaHeight = dlg.SelectedArea.Height;
         _viewModel.RefreshDesktopAreaSummary();
      }

      private void ApplyChanges()
      {
         CaptureSandboxEditors();

         var updated = _viewModel.ToPreferences();
         AppInstance.ApplyPreferences(updated, persist: true);

         // Force all currently open notes to instantly update their rotation!
         // (It pulls the live value from AppInstance.Preferences, which we just updated)
         foreach (var window in System.Windows.Application.Current.Windows.OfType<NoteWindow>())
         {
            window.ApplyRotation();
            window.ApplyAging();
         }
      }

      private void InitializeSandboxEditors()
      {
         _suppressSandboxSync = true;
         try
         {
            SetRichText(BulletListSandbox, _viewModel.AutoListBulletTemplateRtf, "• Task item");
            SetRichText(NumberListSandbox, _viewModel.AutoListNumberTemplateRtf, "1. Numbered item");

            if (FindName("TodoTemplateSandbox") is System.Windows.Controls.RichTextBox todoBox)
               SetRichText(todoBox, _viewModel.TodoTemplateRtf, "• [ ] Task 1\n• [ ] Task 2");
         }
         finally
         {
            _suppressSandboxSync = false;
         }
      }

      private void CaptureSandboxEditors()
      {
         _viewModel.AutoListBulletTemplateRtf = GetRichText(BulletListSandbox);
         _viewModel.AutoListNumberTemplateRtf = GetRichText(NumberListSandbox);

         if (FindName("TodoTemplateSandbox") is System.Windows.Controls.RichTextBox todoBox)
            _viewModel.TodoTemplateRtf = GetRichText(todoBox);
      }

      private void TodoTemplateSandbox_TextChanged(object sender, TextChangedEventArgs e)
      {
         if (_suppressSandboxSync) return;
         if (sender is System.Windows.Controls.RichTextBox todoBox)
            _viewModel.TodoTemplateRtf = GetRichText(todoBox);
      }

      private static void SetRichText(System.Windows.Controls.RichTextBox box, string? rtf, string fallback)
      {
         box.Document.Blocks.Clear();
         if (string.IsNullOrWhiteSpace(rtf))
         {
            ApplyFallbackRichText(box, fallback);
            return;
         }

         try
         {
            var bytes = System.Text.Encoding.UTF8.GetBytes(rtf);
            using var ms = new MemoryStream(bytes);
            new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Load(ms, System.Windows.DataFormats.Rtf);
         }
         catch
         {
            ApplyFallbackRichText(box, fallback);
         }
      }

      private static void ApplyFallbackRichText(System.Windows.Controls.RichTextBox box, string fallback)
      {
         box.Document.Blocks.Clear();
         box.Document.Blocks.Add(new Paragraph(new Run(fallback)));
      }

      private static string GetRichText(System.Windows.Controls.RichTextBox box)
      {
         using var ms = new MemoryStream();
         new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Save(ms, System.Windows.DataFormats.Rtf);
         return System.Text.Encoding.UTF8.GetString(ms.ToArray());
      }

      private void BulletListSandbox_TextChanged(object sender, TextChangedEventArgs e)
      {
         if (_suppressSandboxSync)
            return;

         _viewModel.AutoListBulletTemplateRtf = GetRichText(BulletListSandbox);
      }

      private void NumberListSandbox_TextChanged(object sender, TextChangedEventArgs e)
      {
         if (_suppressSandboxSync)
            return;

         _viewModel.AutoListNumberTemplateRtf = GetRichText(NumberListSandbox);
      }

      private void BrowseSyncFile_Click(object sender, RoutedEventArgs e)
      {
         var dlg = new Microsoft.Win32.SaveFileDialog
         {
            Title = "Choose sync file",
            Filter = "StickIt sync file (*.3m)|*.3m|JSON file (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".3m",
            AddExtension = true,
            OverwritePrompt = false,
            FileName = string.IsNullOrWhiteSpace(_viewModel.SyncFilePath)
                ? "StickItSync.3m"
                : Path.GetFileName(_viewModel.SyncFilePath)
         };

         var initialDirectory = TryGetExistingDirectory(_viewModel.SyncFilePath) ?? GetPreferredSyncBrowseDirectory();
         if (!string.IsNullOrWhiteSpace(initialDirectory))
            dlg.InitialDirectory = initialDirectory;

         if (dlg.ShowDialog(this) == true)
            _viewModel.SyncFilePath = dlg.FileName;
      }

      private static string? TryGetExistingDirectory(string? filePath)
      {
         if (string.IsNullOrWhiteSpace(filePath))
            return null;

         try
         {
            var directory = Path.GetDirectoryName(filePath);
            return !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)
               ? directory
               : null;
         }
         catch
         {
            return null;
         }
      }


      private static string? GetPreferredSyncBrowseDirectory()
      {
         foreach (var candidate in GetCloudSyncFolderCandidates())
         {
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
               return candidate;
         }

         return null;
      }

      private static string?[] GetCloudSyncFolderCandidates()
      {
         var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
         var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
         var oneDriveConsumer = Environment.GetEnvironmentVariable("OneDriveConsumer");
         var oneDriveCommercial = Environment.GetEnvironmentVariable("OneDriveCommercial");

         return
         [
            oneDrive,
            oneDriveConsumer,
            oneDriveCommercial,
            Path.Combine(userProfile, "OneDrive"),
            Path.Combine(userProfile, "Dropbox"),
            Path.Combine(userProfile, "Google Drive"),
            Path.Combine(userProfile, "My Drive")
         ];
      }

      private void ExecuteSync(SyncOperation operation, string title)
      {
         ApplyChanges();
         var ok = operation(out var message);
         System.Windows.MessageBox.Show(
            this,
            message,
            title,
            MessageBoxButton.OK,
            ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

         _viewModel.LastSyncUtc = AppInstance.Preferences.LastSyncUtc;
      }

      private void SyncNow_Click(object sender, RoutedEventArgs e)
      {
         ExecuteSync(AppInstance.TrySyncNow, "Sync");
      }

      private void PullSync_Click(object sender, RoutedEventArgs e)
      {
         ExecuteSync(AppInstance.TryPullFromSync, "Sync");
      }

      private void PushSync_Click(object sender, RoutedEventArgs e)
      {
         ExecuteSync(AppInstance.TryPushToSync, "Sync");
      }



   }

   public sealed class PreferencesViewModel : INotifyPropertyChanged
   {
      private static System.Windows.Media.FontFamily ResolveFontFamily(string? source, string fallback)
      {
         var candidate = string.IsNullOrWhiteSpace(source) ? fallback : source;
         try
         {
            return new System.Windows.Media.FontFamily(candidate);
         }
         catch
         {
            return new System.Windows.Media.FontFamily(fallback);
         }
      }

      public ObservableCollection<System.Windows.Media.FontFamily> FontFamilies { get; }
      public ObservableCollection<double> FontSizes { get; }
      public ObservableCollection<Mode2ActionOption> Mode2HostMissingActions { get; }

      public bool RunOnStartup { get => _runOnStartup; set => SetField(ref _runOnStartup, value); }
      public bool DarkMode { get => _darkMode; set => SetField(ref _darkMode, value); }
      public bool ShowTaskbarIcon { get => _showTaskbarIcon; set => SetField(ref _showTaskbarIcon, value); }
      public bool ShowTrayIcon { get => _showTrayIcon; set => SetField(ref _showTrayIcon, value); }

      public bool AlwaysStickNewNotesToDesktop { get => _alwaysStickNewNotesToDesktop; set => SetField(ref _alwaysStickNewNotesToDesktop, value); }
      public bool SnapNotesToGrid { get => _snapNotesToGrid; set => SetField(ref _snapNotesToGrid, value); }
      public bool KeepNotesInsideDesktopArea { get => _keepNotesInsideDesktopArea; set => SetField(ref _keepNotesInsideDesktopArea, value); }
      public bool ConfirmOnDelete { get => _confirmOnDelete; set => SetField(ref _confirmOnDelete, value); }
      public bool HideNotesOnShowDesktop { get => _hideNotesOnShowDesktop; set => SetField(ref _hideNotesOnShowDesktop, value); }
      public bool TreatNotesAsTopLevelWindows { get => _treatNotesAsTopLevelWindows; set => SetField(ref _treatNotesAsTopLevelWindows, value); }
      public bool Mode2PreventManualMove { get => _mode2PreventManualMove; set => SetField(ref _mode2PreventManualMove, value); }
      public bool Mode2MinimizeWithHost { get => _mode2MinimizeWithHost; set => SetField(ref _mode2MinimizeWithHost, value); }

      public bool EnableNoteRotation { get => _enableNoteRotation; set => SetField(ref _enableNoteRotation, value); }
      public double MaxNoteRotation { get => _maxNoteRotation; set => SetField(ref _maxNoteRotation, value); }
      public bool EnableNoteAging { get => _enableNoteAging; set => SetField(ref _enableNoteAging, value); }

      public bool Mode2CloseNoteWhenHostCloses { get => _mode2CloseNoteWhenHostCloses; set => SetField(ref _mode2CloseNoteWhenHostCloses, value); }
      public Mode2HostMissingAction Mode2HostMissingAction
      {
         get => _mode2HostMissingAction;
         set
         {
            if (Equals(_mode2HostMissingAction, value))
               return;

            SetField(ref _mode2HostMissingAction, value);
            OnPropertyChanged(nameof(SelectedMode2HostMissingAction));
         }
      }

      public Mode2ActionOption SelectedMode2HostMissingAction
      {
         get => Mode2HostMissingActions.FirstOrDefault(o => o.Value == Mode2HostMissingAction) ?? Mode2HostMissingActions.First();
         set => Mode2HostMissingAction = value?.Value ?? Mode2HostMissingAction.SwitchToMode1;
      }

      public System.Windows.Media.FontFamily TitleFontFamily { get => _titleFontFamily; set => SetField(ref _titleFontFamily, value); }
      public double TitleFontSize { get => _titleFontSize; set => SetField(ref _titleFontSize, value); }
      public bool TitleFontBold { get => _titleFontBold; set => SetField(ref _titleFontBold, value); }
      public System.Windows.Media.FontFamily BodyFontFamily { get => _bodyFontFamily; set => SetField(ref _bodyFontFamily, value); }
      public double BodyFontSize { get => _bodyFontSize; set => SetField(ref _bodyFontSize, value); }
      public double BodyLineHeightMultiplier { get => _bodyLineHeightMultiplier; set => SetField(ref _bodyLineHeightMultiplier, value); }
      public bool ShowDateAlongTitle { get => _showDateAlongTitle; set => SetField(ref _showDateAlongTitle, value); }
      public bool EnableDropShadow { get => _enableDropShadow; set => SetField(ref _enableDropShadow, value); }
      public bool EnableNoteBorders { get => _enableNoteBorders; set => SetField(ref _enableNoteBorders, value); }
      public bool EnableExternalNoteImportExport { get => _enableExternalNoteImportExport; set => SetField(ref _enableExternalNoteImportExport, value); }
      public bool EnableAutoListFormatting { get => _enableAutoListFormatting; set => SetField(ref _enableAutoListFormatting, value); }
      public string AutoListBulletSymbol { get => _autoListBulletSymbol; set => SetField(ref _autoListBulletSymbol, value); }
      public int AutoListSpacesAfterMarker { get => _autoListSpacesAfterMarker; set => SetField(ref _autoListSpacesAfterMarker, value); }
      public string AutoListNumberSuffix { get => _autoListNumberSuffix; set => SetField(ref _autoListNumberSuffix, value); }
      public string AutoListBulletTemplateRtf { get => _autoListBulletTemplateRtf; set => SetField(ref _autoListBulletTemplateRtf, value); }
      public string AutoListNumberTemplateRtf { get => _autoListNumberTemplateRtf; set => SetField(ref _autoListNumberTemplateRtf, value); }
      public string TodoTemplateRtf { get => _todoTemplateRtf; set => SetField(ref _todoTemplateRtf, value); }
      public bool EnableTodoTitleTrigger { get => _enableTodoTitleTrigger; set => SetField(ref _enableTodoTitleTrigger, value); }
      public ObservableCollection<string> AutoListBulletSymbols { get; }
      public ObservableCollection<int> AutoListSpacingOptions { get; }
      public ObservableCollection<string> AutoListNumberSuffixes { get; }
      public string AutoListSample { get => _autoListSample; private set => SetField(ref _autoListSample, value); }
      public bool SyncEnabled { get => _syncEnabled; set => SetField(ref _syncEnabled, value); }
      public string SyncFilePath { get => _syncFilePath; set => SetField(ref _syncFilePath, value); }
      public bool SyncPreferences { get => _syncPreferences; set => SetField(ref _syncPreferences, value); }
      public bool WarnBeforeReplaceOnPull { get => _warnBeforeReplaceOnPull; set => SetField(ref _warnBeforeReplaceOnPull, value); }
      public ObservableCollection<SyncModeOption> SyncModes { get; }
      public ObservableCollection<SyncImportModeOption> SyncImportModes { get; }
      public SyncMode SyncMode { get => _syncMode; set => SetField(ref _syncMode, value); }
      public SyncImportMode SyncImportMode { get => _syncImportMode; set => SetField(ref _syncImportMode, value); }
      private string SyncDeviceId { get => _syncDeviceId; set => SetField(ref _syncDeviceId, value); }
      public SyncModeOption SelectedSyncMode
      {
         get => SyncModes.FirstOrDefault(o => o.Value == SyncMode) ?? SyncModes.First();
         set => SyncMode = value?.Value ?? SyncMode.PreferPullFromOtherDevice;
      }

      public SyncImportModeOption SelectedSyncImportMode
      {
         get => SyncImportModes.FirstOrDefault(o => o.Value == SyncImportMode) ?? SyncImportModes.First();
         set => SyncImportMode = value?.Value ?? SyncImportMode.ReplaceCurrentNotes;
      }
      public DateTime? LastSyncUtc { get => _lastSyncUtc; set => SetField(ref _lastSyncUtc, value); }

      public double? DesktopAreaLeft { get => _desktopAreaLeft; set => SetField(ref _desktopAreaLeft, value); }
      public double? DesktopAreaTop { get => _desktopAreaTop; set => SetField(ref _desktopAreaTop, value); }
      public double? DesktopAreaWidth { get => _desktopAreaWidth; set => SetField(ref _desktopAreaWidth, value); }
      public double? DesktopAreaHeight { get => _desktopAreaHeight; set => SetField(ref _desktopAreaHeight, value); }

      public string DesktopAreaSummary { get => _desktopAreaSummary; private set => SetField(ref _desktopAreaSummary, value); }
      public string LastSyncSummary { get => _lastSyncSummary; private set => SetField(ref _lastSyncSummary, value); }

      private bool _runOnStartup;
      private bool _darkMode;
      private bool _showTaskbarIcon = true;
      private bool _showTrayIcon = true;
      private bool _alwaysStickNewNotesToDesktop;
      private bool _snapNotesToGrid;
      private bool _keepNotesInsideDesktopArea;
      private bool _confirmOnDelete;
      private bool _hideNotesOnShowDesktop;
      private bool _treatNotesAsTopLevelWindows = true;
      private bool _mode2PreventManualMove = true;
      private bool _mode2MinimizeWithHost;
      private bool _mode2CloseNoteWhenHostCloses;
      private Mode2HostMissingAction _mode2HostMissingAction = Mode2HostMissingAction.SwitchToMode1;
      private System.Windows.Media.FontFamily _titleFontFamily = new("Helvetica");
      private double _titleFontSize = 19.0;
      private bool _titleFontBold = true;
      private System.Windows.Media.FontFamily _bodyFontFamily = new("Segoe UI");
      private double _bodyFontSize = 14.0;
      private double _bodyLineHeightMultiplier = 1.0;
      private bool _showDateAlongTitle;
      private bool _enableDropShadow = true;
      private bool _enableNoteBorders = true;
      private bool _enableExternalNoteImportExport;
      private bool _enableAutoListFormatting;
      private string _autoListBulletSymbol = "•";
      private int _autoListSpacesAfterMarker = 1;
      private string _autoListNumberSuffix = ".";
      private string _autoListBulletTemplateRtf = string.Empty;
      private string _autoListNumberTemplateRtf = string.Empty;
      private bool _enableTodoTitleTrigger;
      private string _autoListSample = "• Task item\n1. Numbered item";
      private bool _syncEnabled;
      private string _syncFilePath = string.Empty;
      private bool _syncPreferences = true;
      private bool _warnBeforeReplaceOnPull = true;
      private SyncMode _syncMode = SyncMode.PreferPullFromOtherDevice;
      private SyncImportMode _syncImportMode = SyncImportMode.ReplaceCurrentNotes;
      private string _syncDeviceId = string.Empty;
      private DateTime? _lastSyncUtc;
      private double? _desktopAreaLeft;
      private double? _desktopAreaTop;
      private double? _desktopAreaWidth;
      private double? _desktopAreaHeight;
      private string _desktopAreaSummary = "Not set";
      private string _lastSyncSummary = "Never";
      private string _todoTemplateRtf = string.Empty;
      private bool _enableNoteRotation = true;
      private double _maxNoteRotation = 4.0;
      private bool _enableNoteAging = true;

      public event PropertyChangedEventHandler? PropertyChanged;

      private PreferencesViewModel()
      {
         FontFamilies = new ObservableCollection<System.Windows.Media.FontFamily>(Fonts.SystemFontFamilies.OrderBy(f => f.Source));
         FontSizes = new ObservableCollection<double>(new[] { 8d, 9d, 10d, 11d, 12d, 13d, 14d, 15d, 16d, 18d, 19d, 20d, 22d, 24d, 28d, 32d });
         SyncModes = new ObservableCollection<SyncModeOption>
         {
            new(SyncMode.Smart, "Smart (timestamp-based)"),
            new(SyncMode.PreferPullFromOtherDevice, "Prefer pull when sync file came from another device"),
            new(SyncMode.AlwaysPull, "Always pull from sync file"),
            new(SyncMode.AlwaysPush, "Always push local notes to sync file")
         };

         AutoListBulletSymbols = new ObservableCollection<string>(new[] { "•", "?", "?", "-", "*", "+" });
         AutoListSpacingOptions = new ObservableCollection<int>(new[] { 1, 2, 3, 4 });
         AutoListNumberSuffixes = new ObservableCollection<string>(new[] { ".", ")", ":" });

         SyncImportModes = new ObservableCollection<SyncImportModeOption>
         {
            new(SyncImportMode.ReplaceCurrentNotes, "Replace current notes with synced notes"),
            new(SyncImportMode.AddMissingSyncedNotes, "Add synced notes to current notes"),
            new(SyncImportMode.MergeByNoteIdNewestWins, "Merge by note id (newest note wins)")
         };

         Mode2HostMissingActions = new ObservableCollection<Mode2ActionOption>
         {
            new(Mode2HostMissingAction.StickToDesktop, "Stick note to desktop"),
            new(Mode2HostMissingAction.SwitchToMode0, "Unstick note (switch to mode 0)"),
            new(Mode2HostMissingAction.SwitchToMode1, "Make note always on top (switch to mode 1)")
         };
      }

      public static PreferencesViewModel FromPreferences(AppPreferences prefs)
      {
         prefs ??= new AppPreferences();

         var vm = new PreferencesViewModel
         {
            RunOnStartup = prefs.RunOnStartup,
            DarkMode = prefs.DarkMode,
            ShowTaskbarIcon = prefs.ShowTaskbarIcon,
            ShowTrayIcon = prefs.ShowTrayIcon,
            AlwaysStickNewNotesToDesktop = prefs.AlwaysStickNewNotesToDesktop,
            SnapNotesToGrid = prefs.SnapNotesToGrid,
            KeepNotesInsideDesktopArea = prefs.KeepNotesInsideDesktopArea,
            ConfirmOnDelete = prefs.ConfirmOnDelete,
            HideNotesOnShowDesktop = prefs.HideNotesOnShowDesktop,
            TreatNotesAsTopLevelWindows = prefs.TreatNotesAsTopLevelWindows,
            Mode2PreventManualMove = prefs.Mode2PreventManualMove,
            Mode2MinimizeWithHost = prefs.Mode2MinimizeWithHost,
            Mode2CloseNoteWhenHostCloses = prefs.Mode2CloseNoteWhenHostCloses,
            Mode2HostMissingAction = prefs.Mode2HostMissingAction,
            TitleFontFamily = ResolveFontFamily(prefs.TitleFontFamily, "Helvetica"),
            TitleFontSize = prefs.TitleFontSize > 0 ? prefs.TitleFontSize : 19.0,
            TitleFontBold = prefs.TitleFontBold,
            BodyFontFamily = ResolveFontFamily(prefs.BodyFontFamily, "Segoe UI"),
            BodyFontSize = prefs.BodyFontSize > 0 ? prefs.BodyFontSize : 14.0,
            BodyLineHeightMultiplier = prefs.BodyLineHeightMultiplier > 0 ? prefs.BodyLineHeightMultiplier : 1.0,
            ShowDateAlongTitle = prefs.ShowDateAlongTitle,
            EnableDropShadow = prefs.EnableDropShadow,
            EnableNoteBorders = prefs.EnableNoteBorders,
            EnableExternalNoteImportExport = prefs.EnableExternalNoteImportExport,
            EnableAutoListFormatting = prefs.EnableAutoListFormatting,
            AutoListBulletSymbol = prefs.AutoListBulletSymbol,
            AutoListSpacesAfterMarker = prefs.AutoListSpacesAfterMarker,
            AutoListNumberSuffix = prefs.AutoListNumberSuffix,
            AutoListBulletTemplateRtf = prefs.AutoListBulletTemplateRtf,
            AutoListNumberTemplateRtf = prefs.AutoListNumberTemplateRtf,
            EnableTodoTitleTrigger = prefs.EnableTodoTitleTrigger,
            SyncEnabled = prefs.SyncEnabled,
            SyncFilePath = prefs.SyncFilePath,
            SyncPreferences = prefs.SyncPreferences,
            WarnBeforeReplaceOnPull = prefs.WarnBeforeReplaceOnPull,
            SyncMode = prefs.SyncMode,
            SyncImportMode = prefs.SyncImportMode,
            SyncDeviceId = prefs.SyncDeviceId,
            LastSyncUtc = prefs.LastSyncUtc,
            DesktopAreaLeft = prefs.DesktopAreaLeft,
            DesktopAreaTop = prefs.DesktopAreaTop,
            DesktopAreaWidth = prefs.DesktopAreaWidth,
            DesktopAreaHeight = prefs.DesktopAreaHeight,
            EnableNoteRotation = prefs.EnableNoteRotation,
            MaxNoteRotation = prefs.MaxNoteRotation >= 1.0 ? prefs.MaxNoteRotation : 4.0,
            EnableNoteAging = prefs.EnableNoteAging

         };

         vm.RefreshDesktopAreaSummary();
         vm.RefreshLastSyncSummary();
         vm.RefreshAutoListSample();
         vm.TodoTemplateRtf = prefs.TodoTemplateRtf;


         return vm;
      }

      public AppPreferences ToPreferences()
      {
         return new AppPreferences
         {
            RunOnStartup = RunOnStartup,
            DarkMode = DarkMode,
            ShowTaskbarIcon = ShowTaskbarIcon,
            ShowTrayIcon = ShowTrayIcon,
            AlwaysStickNewNotesToDesktop = AlwaysStickNewNotesToDesktop,
            SnapNotesToGrid = SnapNotesToGrid,
            KeepNotesInsideDesktopArea = KeepNotesInsideDesktopArea,
            ConfirmOnDelete = ConfirmOnDelete,
            HideNotesOnShowDesktop = HideNotesOnShowDesktop,
            TreatNotesAsTopLevelWindows = TreatNotesAsTopLevelWindows,
            Mode2PreventManualMove = Mode2PreventManualMove,
            Mode2MinimizeWithHost = Mode2MinimizeWithHost,
            Mode2CloseNoteWhenHostCloses = Mode2CloseNoteWhenHostCloses,
            Mode2HostMissingAction = Mode2HostMissingAction,
            TitleFontFamily = string.IsNullOrWhiteSpace(TitleFontFamily?.Source) ? "Helvetica" : TitleFontFamily.Source,
            TitleFontSize = TitleFontSize > 0 ? TitleFontSize : 19.0,
            TitleFontBold = TitleFontBold,
            BodyFontFamily = string.IsNullOrWhiteSpace(BodyFontFamily?.Source) ? "Segoe UI" : BodyFontFamily.Source,
            BodyFontSize = BodyFontSize > 0 ? BodyFontSize : 14.0,
            BodyLineHeightMultiplier = BodyLineHeightMultiplier > 0 ? BodyLineHeightMultiplier : 1.0,
            ShowDateAlongTitle = ShowDateAlongTitle,
            EnableDropShadow = EnableDropShadow,
            EnableNoteBorders = EnableNoteBorders,
            EnableExternalNoteImportExport = EnableExternalNoteImportExport,
            EnableAutoListFormatting = EnableAutoListFormatting,
            AutoListBulletSymbol = string.IsNullOrWhiteSpace(AutoListBulletSymbol) ? "•" : AutoListBulletSymbol,
            AutoListSpacesAfterMarker = Math.Max(1, Math.Min(4, AutoListSpacesAfterMarker)),
            AutoListNumberSuffix = string.IsNullOrWhiteSpace(AutoListNumberSuffix) ? "." : AutoListNumberSuffix,
            AutoListBulletTemplateRtf = AutoListBulletTemplateRtf,
            AutoListNumberTemplateRtf = AutoListNumberTemplateRtf,
            EnableTodoTitleTrigger = EnableTodoTitleTrigger,
            SyncEnabled = SyncEnabled,
            SyncFilePath = SyncFilePath?.Trim() ?? string.Empty,
            SyncPreferences = SyncPreferences,
            WarnBeforeReplaceOnPull = WarnBeforeReplaceOnPull,
            SyncMode = SyncMode,
            SyncImportMode = SyncImportMode,
            SyncDeviceId = SyncDeviceId,
            LastSyncUtc = LastSyncUtc,
            DesktopAreaLeft = DesktopAreaLeft,
            DesktopAreaTop = DesktopAreaTop,
            DesktopAreaWidth = DesktopAreaWidth,
            DesktopAreaHeight = DesktopAreaHeight,
            TodoTemplateRtf = TodoTemplateRtf,
            EnableNoteRotation = EnableNoteRotation,
            MaxNoteRotation = MaxNoteRotation,
            EnableNoteAging = EnableNoteAging
         };
      }

      public void RefreshDesktopAreaSummary()
      {
         if (DesktopAreaLeft is null || DesktopAreaTop is null || DesktopAreaWidth is null || DesktopAreaHeight is null)
         {
            DesktopAreaSummary = "Not set";
            return;
         }

         DesktopAreaSummary = $"Left {DesktopAreaLeft:0}, Top {DesktopAreaTop:0}, {DesktopAreaWidth:0} x {DesktopAreaHeight:0}";
      }

      public void RefreshLastSyncSummary()
      {
         LastSyncSummary = LastSyncUtc is null
            ? "Never"
            : LastSyncUtc.Value.ToLocalTime().ToString("g");
      }

      public void RefreshAutoListSample()
      {
         var spaces = new string(' ', Math.Max(1, Math.Min(4, AutoListSpacesAfterMarker)));
         var bullet = string.IsNullOrWhiteSpace(AutoListBulletSymbol) ? "•" : AutoListBulletSymbol;
         var suffix = string.IsNullOrWhiteSpace(AutoListNumberSuffix) ? "." : AutoListNumberSuffix;
         AutoListSample = $"{bullet}{spaces}Task item{Environment.NewLine}1{suffix}{spaces}Numbered item";
      }

      private void OnPropertyChanged([CallerMemberName] string? name = null) =>
         PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

      private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
      {
         if (Equals(field, value))
            return;

         field = value;
         OnPropertyChanged(name);

         if (name == nameof(DesktopAreaLeft) || name == nameof(DesktopAreaTop) || name == nameof(DesktopAreaWidth) || name == nameof(DesktopAreaHeight))
            RefreshDesktopAreaSummary();

         if (name == nameof(LastSyncUtc))
            RefreshLastSyncSummary();

         if (name == nameof(AutoListBulletSymbol) || name == nameof(AutoListSpacesAfterMarker) || name == nameof(AutoListNumberSuffix))
            RefreshAutoListSample();
      }
   }

   public sealed record Mode2ActionOption(Mode2HostMissingAction Value, string Label);
   public sealed record SyncModeOption(SyncMode Value, string Label);
   public sealed record SyncImportModeOption(SyncImportMode Value, string Label);
}
