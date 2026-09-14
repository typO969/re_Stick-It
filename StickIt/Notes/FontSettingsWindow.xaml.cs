using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

using StickIt.Converters;
using StickIt.Services;

namespace StickIt
{
   public partial class FontSettingsWindow : Window
   {
      private readonly FontSettingsViewModel _viewModel;

      public FontSettingsData? Settings { get; private set; }

      public FontSettingsWindow(FontSettingsData initial)
      {
         InitializeComponent();
         AppThemeService.ApplyDialogTheme(this);
         _viewModel = FontSettingsViewModel.FromSettings(initial ?? new FontSettingsData());
         DataContext = _viewModel;
      }

      private void Ok_Click(object sender, RoutedEventArgs e)
      {
         Settings = _viewModel.ToSettings();
         DialogResult = true;
         Close();
      }

      private void Cancel_Click(object sender, RoutedEventArgs e)
      {
         DialogResult = false;
         Close();
      }
   }

   public sealed class FontSettingsData
   {
      public string FontFamily { get; set; } = "Segoe UI";
      public double FontSize { get; set; } = 14.0;
      public bool IsBold { get; set; }
      public bool IsItalic { get; set; }
      public bool IsUnderline { get; set; }
      public bool IsStrikeThrough { get; set; }
      public System.Windows.Media.Color Color { get; set; } = System.Windows.Media.Colors.Black;
      public bool ApplyToSelection { get; set; } = true;
      public bool ApplyToEntireNote { get; set; }
      public double LineHeightMultiplier { get; set; } = 1.0;
      public bool SetAsDefaultForNewNotes { get; set; }
      public bool ApplyToAllOpenNotes { get; set; }
   }

   public sealed class FontSettingsViewModel : INotifyPropertyChanged
   {
      public ObservableCollection<System.Windows.Media.FontFamily> FontFamilies { get; }
      public ObservableCollection<double> FontSizes { get; }
      public ObservableCollection<ColorItem> Colors { get; }

      private readonly List<System.Windows.Media.FontFamily> _allFontFamilies;
      private readonly List<HandwrittenFontEntry> _handwrittenFontFamilies;

      public System.Windows.Media.FontFamily FontFamily { get => _fontFamily; set => SetField(ref _fontFamily, value); }
      public double FontSize { get => _fontSize; set => SetField(ref _fontSize, value); }
      public bool IsBold { get => _isBold; set => SetField(ref _isBold, value); }
      public bool IsItalic { get => _isItalic; set => SetField(ref _isItalic, value); }
      public bool IsUnderline { get => _isUnderline; set => SetField(ref _isUnderline, value); }
      public bool IsStrikeThrough { get => _isStrikeThrough; set => SetField(ref _isStrikeThrough, value); }
      public ColorItem SelectedColor { get => _selectedColor; set => SetField(ref _selectedColor, value); }
      public double LineHeightMultiplier { get => _lineHeightMultiplier; set => SetField(ref _lineHeightMultiplier, value); }

      public bool ApplyToSelection
      {
         get => _applyToSelection;
         set
         {
            if (SetField(ref _applyToSelection, value))
            {
               if (value)
                  _applyToEntireNote = false;
               OnPropertyChanged(nameof(ApplyToDefaults));
               OnPropertyChanged(nameof(ApplyToEntireNote));
            }
         }
      } // Fixed bracket placement

      public bool SetAsDefaultForNewNotes { get => _setAsDefaultForNewNotes; set => SetField(ref _setAsDefaultForNewNotes, value); }
      public bool ApplyToAllOpenNotes { get => _applyToAllOpenNotes; set => SetField(ref _applyToAllOpenNotes, value); }
      public bool HandwrittenNoteLook
      {
         get => _handwrittenNoteLook;
         set
         {
            if (!SetField(ref _handwrittenNoteLook, value))
               return;

            ApplyFontFamilyFilter();
         }
      }

      public bool ApplyToEntireNote
      {
         get => _applyToEntireNote;
         set
         {
            if (SetField(ref _applyToEntireNote, value))
            {
               if (value)
                  _applyToSelection = false;
               OnPropertyChanged(nameof(ApplyToDefaults));
               OnPropertyChanged(nameof(ApplyToSelection));
            }
         }
      }

      public bool ApplyToDefaults
      {
         get => !_applyToSelection && !_applyToEntireNote;
         set
         {
            if (value)
            {
               ApplyToSelection = false;
               ApplyToEntireNote = false;
            }
         }
      }

      public FontWeight PreviewFontWeight => IsBold ? FontWeights.Bold : FontWeights.Normal;
      public System.Windows.FontStyle PreviewFontStyle => IsItalic ? FontStyles.Italic : FontStyles.Normal;
      public TextDecorationCollection? PreviewTextDecorations
      {
         get
         {
            if (!IsUnderline && !IsStrikeThrough) return null;
            var col = new TextDecorationCollection();
            if (IsUnderline) foreach (var d in TextDecorations.Underline) col.Add(d);
            if (IsStrikeThrough) foreach (var d in TextDecorations.Strikethrough) col.Add(d);
            return col;
         }
      }

      private System.Windows.Media.FontFamily _fontFamily = new("Segoe UI");
      private double _fontSize = 14.0;
      private bool _isBold;
      private bool _isItalic;
      private bool _isUnderline;
      private bool _isStrikeThrough;
      private ColorItem _selectedColor = ColorItem.FromColor("Black", System.Windows.Media.Colors.Black);
      private double _lineHeightMultiplier = 1.0;
      private bool _applyToSelection = true;
      private bool _applyToEntireNote;
      private bool _setAsDefaultForNewNotes;
      private bool _applyToAllOpenNotes;
      private bool _handwrittenNoteLook;

      public event PropertyChangedEventHandler? PropertyChanged;

      private FontSettingsViewModel()
      {
         _allFontFamilies = Fonts.SystemFontFamilies.OrderBy(f => f.Source).ToList();
         _handwrittenFontFamilies = LoadHandwrittenFonts();
         FontFamilies = new ObservableCollection<System.Windows.Media.FontFamily>(_allFontFamilies);
         FontSizes = new ObservableCollection<double>(new[] { 8d, 9d, 10d, 11d, 12d, 13d, 14d, 15d, 16d, 18d, 19d, 20d, 22d, 24d, 28d, 32d, 36d, 48d, 72d });
         Colors = new ObservableCollection<ColorItem>(ColorItem.Defaults);
      }

      public static FontSettingsViewModel FromSettings(FontSettingsData settings)
      {
         var vm = new FontSettingsViewModel();

         vm.HandwrittenNoteLook = vm.IsHandwrittenFont(settings.FontFamily);
         vm.FontFamily = vm.ResolveFontFamily(settings.FontFamily);
         vm.FontSize = settings.FontSize;
         vm.IsBold = settings.IsBold;
         vm.IsItalic = settings.IsItalic;
         vm.IsUnderline = settings.IsUnderline;
         vm.IsStrikeThrough = settings.IsStrikeThrough;
         vm.LineHeightMultiplier = settings.LineHeightMultiplier > 0 ? settings.LineHeightMultiplier : 1.0;
         vm.ApplyToSelection = settings.ApplyToSelection;
         vm.ApplyToEntireNote = settings.ApplyToEntireNote;
         vm.SetAsDefaultForNewNotes = settings.SetAsDefaultForNewNotes;
         vm.ApplyToAllOpenNotes = settings.ApplyToAllOpenNotes;

         vm.SelectedColor = vm.Colors.FirstOrDefault(c => c.Color == settings.Color) ?? vm.Colors.First();
         return vm;
      }

      public FontSettingsData ToSettings()
      {
         return new FontSettingsData
         {
            FontFamily = GetPersistedFontFamily(FontFamily),
            FontSize = FontSize,
            IsBold = IsBold,
            IsItalic = IsItalic,
            IsUnderline = IsUnderline,
            IsStrikeThrough = IsStrikeThrough,
            Color = SelectedColor.Color,
            LineHeightMultiplier = LineHeightMultiplier,
            ApplyToSelection = ApplyToSelection,
            ApplyToEntireNote = ApplyToEntireNote,
            SetAsDefaultForNewNotes = SetAsDefaultForNewNotes,
            ApplyToAllOpenNotes = ApplyToAllOpenNotes
         };
      }

      private void OnPropertyChanged([CallerMemberName] string? name = null) =>
         PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

      private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
      {
         if (Equals(field, value))
            return false;

         field = value;
         OnPropertyChanged(name);

         if (name == nameof(IsBold))
            OnPropertyChanged(nameof(PreviewFontWeight));
         if (name == nameof(IsItalic))
            OnPropertyChanged(nameof(PreviewFontStyle));
         if (name == nameof(IsUnderline) || name == nameof(IsStrikeThrough))
            OnPropertyChanged(nameof(PreviewTextDecorations));

         return true;
      }

      private void ApplyFontFamilyFilter()
      {
         var candidate = FontFamily;

         var source = HandwrittenNoteLook
            ? _handwrittenFontFamilies.Select(f => f.FontFamily).ToList()
            : _allFontFamilies;

         FontFamilies.Clear();
         foreach (var font in source)
            FontFamilies.Add(font);

         if (FontFamilies.Count == 0)
            return;

         var resolved = FontFamilies.FirstOrDefault(f => AreSameFont(f, candidate));
         FontFamily = resolved ?? FontFamilies.First();
      }

      private System.Windows.Media.FontFamily ResolveFontFamily(string persistedFont)
      {
         if (!string.IsNullOrWhiteSpace(persistedFont))
         {
            var existing = _allFontFamilies.FirstOrDefault(f => AreSameFont(f, persistedFont));
            if (existing != null)
               return existing;

            try
            {
               return new System.Windows.Media.FontFamily(persistedFont);
            }
            catch
            {
               // fallback below
            }
         }

         return _allFontFamilies.FirstOrDefault() ?? new System.Windows.Media.FontFamily("Segoe UI");
      }

      private bool IsHandwrittenFont(string persistedFont)
      {
         return _handwrittenFontFamilies.Any(f =>
            string.Equals(NormalizeFontKey(f.PersistValue), NormalizeFontKey(persistedFont), StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeFontKey(f.FamilyName), NormalizeFontKey(persistedFont), StringComparison.OrdinalIgnoreCase));
      }

      private string GetPersistedFontFamily(System.Windows.Media.FontFamily fontFamily)
      {
         var handwritten = _handwrittenFontFamilies.FirstOrDefault(f => AreSameFont(f.FontFamily, fontFamily));
         return handwritten?.PersistValue ?? fontFamily.Source;
      }

      private static bool AreSameFont(System.Windows.Media.FontFamily left, System.Windows.Media.FontFamily right)
         => string.Equals(NormalizeFontKey(left.Source), NormalizeFontKey(right.Source), StringComparison.OrdinalIgnoreCase);

      private static bool AreSameFont(System.Windows.Media.FontFamily left, string right)
         => string.Equals(NormalizeFontKey(left.Source), NormalizeFontKey(right), StringComparison.OrdinalIgnoreCase);

      private static string NormalizeFontKey(string? value)
      {
         if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

         var v = value.Trim();
         var hashIndex = v.LastIndexOf('#');
         if (hashIndex >= 0 && hashIndex < v.Length - 1)
            v = v[(hashIndex + 1)..];

         if (v.StartsWith("./", StringComparison.Ordinal))
            v = v[2..];

         return v.Trim().ToLowerInvariant();
      }

      private static List<HandwrittenFontEntry> LoadHandwrittenFonts()
      {
         var result = new List<HandwrittenFontEntry>();
         var fontsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Properties", "Fonts");

         if (!Directory.Exists(fontsDir))
            return result;

         foreach (var filePath in Directory.EnumerateFiles(fontsDir)
            .Where(p => p.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p))
         {
            try
            {
               var glyph = new GlyphTypeface(new Uri(filePath, UriKind.Absolute));
               var familyName = glyph.FamilyNames.Values.FirstOrDefault() ?? Path.GetFileNameWithoutExtension(filePath);
               var persistValue = $"pack://siteoforigin:,,,/Properties/Fonts/#{familyName}";
               var fontFamily = new System.Windows.Media.FontFamily(persistValue);

               if (result.Any(f => string.Equals(NormalizeFontKey(f.FamilyName), NormalizeFontKey(familyName), StringComparison.OrdinalIgnoreCase)))
                  continue;

               result.Add(new HandwrittenFontEntry(fontFamily, persistValue, familyName));
            }
            catch
            {
               // Ignore invalid/unloadable font file
            }
         }

         return result;
      }

      private sealed record HandwrittenFontEntry(System.Windows.Media.FontFamily FontFamily, string PersistValue, string FamilyName);
   }

   public sealed class ColorItem
   {
      public string Name { get; init; } = string.Empty;
      public System.Windows.Media.Color Color { get; init; }
      public SolidColorBrush Brush { get; init; } = new(System.Windows.Media.Colors.Black);

      public static ColorItem FromColor(string name, System.Windows.Media.Color color)
      {
         return new ColorItem
         {
            Name = name,
            Color = color,
            Brush = new SolidColorBrush(color)
         };
      }

      public static readonly ColorItem[] Defaults = BuildDefaults();

      private static ColorItem[] BuildDefaults()
      {
         var result = new List<ColorItem>
         {
            FromColor("Black", System.Windows.Media.Colors.Black),
            FromColor("Dark Gray", System.Windows.Media.Colors.DarkGray),
            FromColor("Gray", System.Windows.Media.Colors.Gray),
            FromColor("Light Gray", System.Windows.Media.Colors.LightGray),
            FromColor("White", System.Windows.Media.Colors.White),
            FromColor("Red", System.Windows.Media.Colors.Firebrick),
            FromColor("Orange", System.Windows.Media.Colors.DarkOrange),
            FromColor("Yellow", System.Windows.Media.Colors.Goldenrod),
            FromColor("Green", System.Windows.Media.Colors.SeaGreen),
            FromColor("Blue", System.Windows.Media.Colors.SteelBlue),
            FromColor("Purple", System.Windows.Media.Colors.MediumPurple)
         };

         foreach (var noteColor in Enum.GetValues<NoteColors.NoteColor>())
         {
            if (!NoteColors.Hex.TryGetValue(noteColor, out var baseHex))
               continue;

            var textColor = ColorSchemeConverter.GetColor(noteColor.ToString(), baseHex, ColorComponent.Text);
            result.Add(FromColor($"Default 3M {SplitName(noteColor.ToString())} text", textColor));
         }

         return result.ToArray();
      }

      private static string SplitName(string value)
      {
         if (string.IsNullOrWhiteSpace(value))
            return "Yellow";

         var chars = new List<char>(value.Length + 6);
         for (int i = 0; i < value.Length; i++)
         {
            var ch = value[i];
            if (i > 0 && char.IsUpper(ch) && char.IsLetter(value[i - 1]))
               chars.Add(' ');
            chars.Add(ch);
         }

         return new string(chars.ToArray());
      }
   }
}
