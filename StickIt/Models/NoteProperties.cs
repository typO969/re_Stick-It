using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

using StickIt.Services;

using static StickIt.Services.NoteColors;

namespace StickIt.Models
{
	public sealed class NoteProperties
	{
		// Identity
		public string Id { get; set; } = Guid.NewGuid().ToString("N");

		public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
		public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;


		// Appearance
		public string Title { get; set; } = "Untitled";
		public string FontFamily { get; set; } = "Segoe UI";
		public double FontSize { get; set; } = 14.0;
      public double LineHeightMultiplier { get; set; } = 1.0;
		public NoteColors.NoteColor ColorKey { get; set; } = NoteColors.NoteColor.ThreeMYellow;
		public FontWeight FontWeight { get; set; }
		public System.Windows.FontStyle FontStyle { get; set; }

		// Window state
		public double X { get; set; }
		public double Y { get; set; }
		public bool IsMinimized { get; set; }

      // Inside NoteProperties class, add:
      public bool IsLocked { get; set; }

      public double RotationAngle { get; set; }

      public bool HasSeenAnniversary { get; set; } = false;

      // Content metadata (not content itself)
      public int CharCount { get; set; }
		public int WordCount { get; set; }
	}

}
