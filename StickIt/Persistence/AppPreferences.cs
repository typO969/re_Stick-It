namespace StickIt.Persistence
{
  public enum Mode2HostMissingAction
	{
		StickToDesktop = 0,
		SwitchToMode0 = 1,
		SwitchToMode1 = 2
	}

	public enum SyncMode
	{
		Smart = 0,
		PreferPullFromOtherDevice = 1,
		AlwaysPull = 2,
		AlwaysPush = 3
	}

	public enum SyncImportMode
	{
		ReplaceCurrentNotes = 0,
		AddMissingSyncedNotes = 1,
		MergeByNoteIdNewestWins = 2
	}

	public sealed class AppPreferences
	{
		public bool RunOnStartup { get; set; }
		public bool DarkMode { get; set; }
		public bool ShowTaskbarIcon { get; set; } = true;
		public bool ShowTrayIcon { get; set; } = true;

		public bool AlwaysStickNewNotesToDesktop { get; set; }
		public bool SnapNotesToGrid { get; set; }
		public bool KeepNotesInsideDesktopArea { get; set; }
		public double? DesktopAreaLeft { get; set; }
		public double? DesktopAreaTop { get; set; }
		public double? DesktopAreaWidth { get; set; }
		public double? DesktopAreaHeight { get; set; }
		public bool ConfirmOnDelete { get; set; }
		public bool HideNotesOnShowDesktop { get; set; }
		public bool TreatNotesAsTopLevelWindows { get; set; } = true;
		public bool Mode2PreventManualMove { get; set; } = true;
		public bool Mode2MinimizeWithHost { get; set; }
		public bool Mode2CloseNoteWhenHostCloses { get; set; }
		public Mode2HostMissingAction Mode2HostMissingAction { get; set; } = Mode2HostMissingAction.SwitchToMode1;

		public string TitleFontFamily { get; set; } = "Helvetica";
		public double TitleFontSize { get; set; } = 19.0;
    public bool TitleFontBold { get; set; } = true;
		public string BodyFontFamily { get; set; } = "Segoe UI";
		public double BodyFontSize { get; set; } = 14.0;
      public double BodyLineHeightMultiplier { get; set; } = 1.0;
		public bool ShowDateAlongTitle { get; set; }
		public bool EnableDropShadow { get; set; } = true;
     public bool EnableNoteBorders { get; set; } = true;
		public bool EnableExternalNoteImportExport { get; set; }
		public bool EnableAutoListFormatting { get; set; }
		public string AutoListBulletSymbol { get; set; } = "•";
		public int AutoListSpacesAfterMarker { get; set; } = 1;
		public string AutoListNumberSuffix { get; set; } = ".";
      public string AutoListBulletTemplateRtf { get; set; } = string.Empty;
		public string AutoListNumberTemplateRtf { get; set; } = string.Empty;
      // Add this right below AutoListNumberTemplateRtf
      public string TodoTemplateRtf { get; set; } = string.Empty;
      public bool EnableTodoTitleTrigger { get; set; }
		public bool WarnBeforeReplaceOnPull { get; set; } = true;

      // Default to true so people see the cool feature on update!
      public bool EnableNoteRotation { get; set; } = true;
      public double MaxNoteRotation { get; set; } = 4.0; // Default to your favorite value
      public bool EnableNoteAging { get; set; } = true;

      public bool SyncEnabled { get; set; }
		public string SyncFilePath { get; set; } = string.Empty;
		public bool SyncPreferences { get; set; } = true;
      public SyncMode SyncMode { get; set; } = SyncMode.PreferPullFromOtherDevice;
    public SyncImportMode SyncImportMode { get; set; } = SyncImportMode.ReplaceCurrentNotes;
		public string SyncDeviceId { get; set; } = string.Empty;
		public DateTime? LastSyncUtc { get; set; }
	}
}
