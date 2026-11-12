// Быстрый запуск баз 1С: тот же список, что в окне «Запуск 1С:Предприятия»,
// но 1С стартует напрямую через 1cv8c.exe / 1cv8.exe с параметрами командной строки.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

[assembly: System.Reflection.AssemblyTitle("1C Launcher")]
[assembly: System.Reflection.AssemblyProduct("1C Launcher")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace OneCLauncher
{
    public class InfoBase : INotifyPropertyChanged
    {
        // Цвета меток «запущено» берутся из темы (задаются в Themed).
        public static Brush EnterpriseBrush = Frozen(Color.FromRgb(63, 185, 80));
        public static Brush DesignerBrush = Frozen(Color.FromRgb(242, 160, 60));

        public string Name { get; set; }
        public string Connect { get; set; }
        public string FilePath { get; set; }
        public string Server { get; set; }
        public string Ref { get; set; }
        public string Ws { get; set; }

        // Строки секции из ibases.v8i как есть (ID, порядок, версия…), чтобы при записи ничего не потерять.
        public List<string> Lines = new List<string>();

        // Значение строки «Ключ=значение» из секции или fallback, если строки нет.
        public string Get(string key, string fallback)
        {
            string line = Lines.FirstOrDefault(l => l.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
            return line == null ? fallback : line.Substring(key.Length + 1);
        }

        // Меняет строку секции; value == null удаляет её.
        public void Set(string key, string value)
        {
            int i = Lines.FindIndex(l => l.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
            if (value == null) { if (i >= 0) Lines.RemoveAt(i); return; }
            if (i >= 0) Lines[i] = key + "=" + value; else Lines.Add(key + "=" + value);
        }

        // Какой клиент запускать: Auto, ThinClient или ThickClient.
        public string App { get { return Get("App", "Auto"); } }

        public void SetConnect(string connect)
        {
            Connect = connect;
            FilePath = Server = Ref = Ws = null;
            foreach (Match m in Regex.Matches(connect, "(\\w+)=\"((?:[^\"]|\"\")*)\""))
            {
                string key = m.Groups[1].Value.ToLowerInvariant();
                string val = m.Groups[2].Value.Replace("\"\"", "\"");
                if (key == "file") FilePath = val;
                else if (key == "srvr") Server = val;
                else if (key == "ref") Ref = val;
                else if (key == "ws") Ws = val;
            }
            int i = Lines.FindIndex(l => l.StartsWith("Connect=", StringComparison.OrdinalIgnoreCase));
            if (i >= 0) Lines[i] = "Connect=" + connect; else Lines.Insert(0, "Connect=" + connect);
        }

        public string Sub
        {
            get
            {
                if (!string.IsNullOrEmpty(FilePath)) return FilePath;
                if (!string.IsNullOrEmpty(Server)) return Server + " \\ " + Ref;
                if (!string.IsNullOrEmpty(Ws)) return Ws;
                return Connect;
            }
        }

        // Сколько окон 1С сейчас открыто с этой базой: Предприятие и Конфигуратор.
        int enterprise, designer;

        // Номера процессов этих окон — по ним крестик в метке закрывает 1С.
        public List<int> EnterprisePids = new List<int>();
        public List<int> DesignerPids = new List<int>();

        public void SetRunning(List<int> enterprisePids, List<int> designerPids)
        {
            EnterprisePids = enterprisePids;
            DesignerPids = designerPids;
            if (enterprisePids.Count == enterprise && designerPids.Count == designer) return;
            enterprise = enterprisePids.Count;
            designer = designerPids.Count;
            foreach (string p in new[] { "IsRunning", "RunningText", "EnterpriseText", "DesignerText",
                                         "EnterpriseVisibility", "DesignerVisibility", "AccentBrush", "AccentThickness" })
            {
                Raise(p);
            }
        }

        public bool IsRunning { get { return enterprise + designer > 0; } }
        public string EnterpriseText { get { return "Предприятие" + (enterprise > 1 ? " ×" + enterprise : ""); } }
        public string DesignerText { get { return "Конфигуратор" + (designer > 1 ? " ×" + designer : ""); } }
        public Visibility EnterpriseVisibility { get { return enterprise > 0 ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility DesignerVisibility { get { return designer > 0 ? Visibility.Visible : Visibility.Collapsed; } }
        public Brush AccentBrush { get { return designer > 0 ? DesignerBrush : EnterpriseBrush; } }
        public Thickness AccentThickness { get { return IsRunning ? new Thickness(4, 0, 0, 0) : new Thickness(0); } }

        public string RunningText
        {
            get
            {
                var parts = new List<string>();
                if (designer > 0) parts.Add("Конфигуратор");
                if (enterprise > 0) parts.Add("Предприятие");
                return string.Join(" и ", parts);
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        void Raise(string name)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }

        static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }

    // Одно запущенное окно 1С: какой режим и какая база в его командной строке.
    public class RunningOneC
    {
        public bool Designer;
        public int Pid;
        public string File;
        public string Server;
        public string IBName;
    }

    // Что пользователь ввёл в окне добавления/изменения базы.
    public class BaseForm
    {
        public string Name;
        public string Connect;
        public string FilePath;
        public bool Create;
        public string Client;    // Auto / ThinClient / ThickClient
        public string Speed;     // Normal / Low
        public string Version;   // 8.3 или конкретная версия платформы
        public string Arch;      // Auto / x86 / x86_64
        public string Extra;     // дополнительные параметры командной строки
    }

    // Установленная платформа 1С: версия, папка bin и разрядность.
    public class PlatformInfo
    {
        public Version Ver;
        public string Bin;
        public string Arch;
    }

    public static class Program
    {
        static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OneCLauncher");
        static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.txt");
        static readonly string BasesFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "1C", "1CEStart", "ibases.v8i");

        static Window window;
        static ListBox list;
        static TextBox search, user, cmdEnterprise, cmdDesigner;
        static PasswordBox pass;
        static TextBlock searchHint, status, title;
        static Button btnEnterprise, btnDesigner;
        static CheckBox closeAfter;
        static bool polling;
        static List<InfoBase> bases = new List<InfoBase>();
        static Dictionary<string, string> settings = new Dictionary<string, string>();
        static string platformBin;
        static string platformVersion;
        static List<InfoBase> sections = new List<InfoBase>();
        static List<string> preamble = new List<string>();
        static bool backedUp;
        static Button btnCloseAll;
        static FrameworkElement details, emptyHint, busy;
        static TextBlock busyText, closeAllText;
        static List<int> allPids = new List<int>();

        static Application app;
        static bool titleDark;          // тёмный ли системный заголовок окна
        static int titleCaption, titleText;   // цвета заголовка, COLORREF 0x00BBGGRR
        static List<PlatformInfo> platforms = new List<PlatformInfo>();

        [STAThread]
        public static int Main()
        {
            app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            LoadSettings();
            BuildWindow();
            window.Show();

            // Раз в 2 секунды смотрим, какие окна 1С открыты, и подсвечиваем их базы.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += delegate { RefreshRunning(); };
            timer.Start();

            return app.Run();
        }

        // Собирает содержимое главного окна с учётом настроек (тема, размеры). При старте создаёт окно,
        // а после смены настроек подменяет содержимое того же окна, без закрытия и повторного открытия.
        static void BuildWindow()
        {
            FindPlatform();
            var src = (Window)XamlReader.Parse(Themed(Xaml));
            string keep = null;
            if (window == null)
            {
                window = src;
                window.Closed += delegate { app.Shutdown(); };
                window.SourceInitialized += delegate { SetTitleBar(window); };
                window.PreviewKeyDown += OnKey;
                window.Loaded += delegate { search.Focus(); };
                SetWindowIcon();
            }
            else
            {
                var selected = list.SelectedItem as InfoBase;
                keep = selected == null ? null : selected.Name;
                object content = src.Content;
                src.Content = null;
                window.Resources = src.Resources;
                window.Background = src.Background;
                window.Foreground = src.Foreground;
                window.FontSize = src.FontSize;
                window.Content = content;
                src.Close();
                SetTitleBar(window);
            }

            list = (ListBox)src.FindName("List");
            search = (TextBox)src.FindName("Search");
            user = (TextBox)src.FindName("User");
            pass = (PasswordBox)src.FindName("Pass");
            searchHint = (TextBlock)src.FindName("SearchHint");
            status = (TextBlock)src.FindName("Status");
            title = (TextBlock)src.FindName("Header");
            cmdEnterprise = (TextBox)src.FindName("CmdEnterprise");
            cmdDesigner = (TextBox)src.FindName("CmdDesigner");
            btnEnterprise = (Button)src.FindName("BtnEnterprise");
            btnDesigner = (Button)src.FindName("BtnDesigner");
            closeAfter = (CheckBox)src.FindName("CloseAfter");
            details = (FrameworkElement)src.FindName("Details");
            emptyHint = (FrameworkElement)src.FindName("EmptyHint");
            busy = (FrameworkElement)src.FindName("Busy");
            busyText = (TextBlock)src.FindName("BusyText");
            closeAllText = (TextBlock)src.FindName("CloseAllText");
            btnCloseAll = (Button)src.FindName("BtnCloseAll");

            ((FrameworkElement)src.FindName("CommandsPanel")).Visibility =
                Setting("showCommands", "1") == "1" ? Visibility.Visible : Visibility.Collapsed;

            closeAfter.IsChecked = Setting("closeAfter", "0") == "1";
            closeAfter.Click += delegate
            {
                settings["closeAfter"] = closeAfter.IsChecked == true ? "1" : "0";
                SaveSettings();
            };

            btnEnterprise.Click += delegate { Launch(false); };
            btnDesigner.Click += delegate { Launch(true); };
            ((Button)src.FindName("BtnCopyEnterprise")).Click += delegate { CopyCommand(cmdEnterprise); };
            ((Button)src.FindName("BtnCopyDesigner")).Click += delegate { CopyCommand(cmdDesigner); };
            user.TextChanged += delegate { UpdatePreview(); };
            pass.PasswordChanged += delegate { UpdatePreview(); };
            ((Button)src.FindName("BtnFolder")).Click += delegate { OpenFolder(); };
            ((Button)src.FindName("BtnReload")).Click += delegate { LoadBases(); };
            ((Button)src.FindName("BtnSettings")).Click += delegate { ShowSettings(); };
            ((Button)src.FindName("BtnAdd")).Click += delegate { AddBase(); };
            ((Button)src.FindName("BtnEdit")).Click += delegate { EditBase(); };
            ((Button)src.FindName("BtnDelete")).Click += delegate { DeleteBase(); };
            btnCloseAll.Click += delegate { CloseAll(); };

            search.TextChanged += delegate { ApplyFilter(); };
            list.SelectionChanged += delegate { OnSelected(); };
            list.MouseDoubleClick += delegate (object s, MouseButtonEventArgs e)
            {
                if (list.SelectedItem != null && !InsideButton(e.OriginalSource)) Launch(false);
            };
            list.AddHandler(Button.ClickEvent, new RoutedEventHandler(OnBadgeClose));
            details.AddHandler(Button.ClickEvent, new RoutedEventHandler(OnBadgeClose));

            LoadBases(keep);
            OnSelected();
        }

        // ---------- настройки вида ----------

        static string Setting(string key, string fallback)
        {
            string v;
            return settings.TryGetValue(key, out v) && v.Length > 0 ? v : fallback;
        }

        static bool IsLight()
        {
            string theme = Setting("theme", "system");
            if (theme == "light") return true;
            if (theme == "dark") return false;
            try
            {
                object v = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0);
                return v is int && (int)v == 1;
            }
            catch (Exception) { return false; }
        }

        // Светлая тема: тёплый кремовый фон, оливковый текст и зелёные кнопки. Ключ — цвет тёмной темы в разметке.
        static readonly Dictionary<string, string> LightColors = new Dictionary<string, string>
        {
            { "#16171A", "#F9F7EA" }, { "#1B1C20", "#F3F0DE" }, { "#26282D", "#E3DFC8" },
            { "#E6E7EA", "#54513F" }, { "#B4B9C2", "#6B6852" }, { "#9AA0AA", "#7D7A63" },
            { "#8A909B", "#8A866E" }, { "#7C828D", "#8F8B73" }, { "#6E7480", "#9A967E" }, { "#5F6570", "#A8A48C" },
            { "#24262B", "#EFEBD6" }, { "#30333A", "#D8D3B8" }, { "#1D1F23", "#FFFDF4" }, { "#141518", "#EEEAD5" },
            { "#4A4E57", "#B5B095" }, { "#1E2025", "#F1EEDB" }, { "#22304C", "#E2E5C9" },
            { "#3B82F6", "#626D46" }, { "#7C3AED", "#4E5936" },
            { "#3A3D44", "#CFCAAF" }, { "#2A2D33", "#E3DFC8" }, { "#B0101114", "#B0F9F7EA" },
            { "#33FFFFFF", "#22000000" }, { "#55FFFFFF", "#33000000" },
            { "#3A2E1C", "#F5E3BE" }, { "#F2A03C", "#A8650F" }, { "#1C3524", "#DDE9C6" }, { "#3FB950", "#3F7A2B" },
            { "#F27C7C", "#C0392B" }
        };

        // Цвета, которые пользователь может задать сам: ключ, подпись, значение тёмной темы.
        static readonly string[][] Palette = new[]
        {
            new[] { "bg", "Фон окна", "#16171A" },
            new[] { "panel", "Фон панелей", "#1B1C20" },
            new[] { "field", "Фон полей ввода", "#1D1F23" },
            new[] { "button", "Кнопки", "#24262B" },
            new[] { "border", "Рамки", "#30333A" },
            new[] { "text", "Основной текст", "#E6E7EA" },
            new[] { "text2", "Второстепенный текст", "#8A909B" },
            new[] { "accent", "Акцент (главная кнопка, выбор)", "#3B82F6" },
            new[] { "accent2", "Акцент 2 (градиент кнопки)", "#7C3AED" },
            new[] { "accentText", "Текст на акценте", "#FFFFFF" },
            new[] { "selected", "Выбранная база в списке", "#22304C" },
            new[] { "ok", "Уведомление: Предприятие запущено", "#3FB950" },
            new[] { "warn", "Уведомление: Конфигуратор запущен", "#F2A03C" },
            new[] { "err", "Уведомление: ошибка", "#F27C7C" }
        };

        // Приводит #RRGGBB, RRGGBB или #RGB к виду #RRGGBB (заглавными); null, если это не цвет.
        static string NormHex(string s)
        {
            if (s == null) return null;
            s = s.Trim();
            if (s.StartsWith("#")) s = s.Substring(1);
            if (Regex.IsMatch(s, "^[0-9A-Fa-f]{3}$")) s = "" + s[0] + s[0] + s[1] + s[1] + s[2] + s[2];
            if (!Regex.IsMatch(s, "^[0-9A-Fa-f]{6}$")) return null;
            return "#" + s.ToUpperInvariant();
        }

        static int[] Rgb(string hex)
        {
            return new[] { Convert.ToInt32(hex.Substring(1, 2), 16), Convert.ToInt32(hex.Substring(3, 2), 16), Convert.ToInt32(hex.Substring(5, 2), 16) };
        }

        // Смесь двух цветов: t = 0 даёт a, t = 1 даёт b.
        static string Mix(string a, string b, double t)
        {
            int[] x = Rgb(a), y = Rgb(b);
            return string.Format("#{0:X2}{1:X2}{2:X2}",
                (int)Math.Round(x[0] + (y[0] - x[0]) * t), (int)Math.Round(x[1] + (y[1] - x[1]) * t), (int)Math.Round(x[2] + (y[2] - x[2]) * t));
        }

        static double Luma(string hex)
        {
            int[] c = Rgb(hex);
            return (0.299 * c[0] + 0.587 * c[1] + 0.114 * c[2]) / 255.0;
        }

        // Текущие свои цвета; неверное или пустое значение заменяется цветом тёмной темы.
        static Dictionary<string, string> CustomColors()
        {
            var r = new Dictionary<string, string>();
            foreach (var p in Palette) r[p[0]] = NormHex(Setting("c:" + p[0], p[2])) ?? p[2];
            return r;
        }

        // Свои цвета раскладываются по цветам тёмной темы в разметке: основные берутся как есть, остальные смешиваются из них.
        static Dictionary<string, string> CustomMap()
        {
            var c = CustomColors();
            string bg = c["bg"], text = c["text"], text2 = c["text2"], border = c["border"];
            bool lightBg = Luma(bg) > 0.5;
            return new Dictionary<string, string>
            {
                { "#16171A", bg }, { "#141518", Mix(bg, "#000000", 0.1) }, { "#1B1C20", c["panel"] }, { "#1D1F23", c["field"] },
                { "#24262B", c["button"] }, { "#1E2025", Mix(c["panel"], c["button"], 0.5) },
                { "#26282D", Mix(border, bg, 0.5) }, { "#2A2D33", border }, { "#30333A", border }, { "#3A3D44", border },
                { "#4A4E57", Mix(border, text, 0.25) }, { "#22304C", c["selected"] },
                { "#B0101114", "#B0" + bg.Substring(1) },
                { "#E6E7EA", text }, { "#B4B9C2", Mix(text2, text, 0.45) }, { "#9AA0AA", Mix(text2, text, 0.2) }, { "#8A909B", text2 },
                { "#7C828D", Mix(text2, bg, 0.12) }, { "#6E7480", Mix(text2, bg, 0.25) }, { "#5F6570", Mix(text2, bg, 0.4) },
                { "#3B82F6", c["accent"] }, { "#7C3AED", c["accent2"] },
                { "#33FFFFFF", lightBg ? "#22000000" : "#33FFFFFF" }, { "#55FFFFFF", lightBg ? "#33000000" : "#55FFFFFF" },
                { "#3A2E1C", Mix(bg, c["warn"], 0.18) }, { "#F2A03C", c["warn"] },
                { "#1C3524", Mix(bg, c["ok"], 0.18) }, { "#3FB950", c["ok"] }, { "#F27C7C", c["err"] }
            };
        }

        // Подставляет в разметку цвета темы и размеры шрифта и кнопок из настроек.
        static string Themed(string xaml)
        {
            Dictionary<string, string> map = null;
            bool custom = Setting("theme", "system") == "custom";
            if (custom) map = CustomMap();
            else if (IsLight()) map = LightColors;

            string bgFinal = "#16171A", textFinal = "#E6E7EA", okFinal = "#3FB950", warnFinal = "#F2A03C";
            if (map != null)
            {
                string to;
                if (map.TryGetValue(bgFinal, out to)) bgFinal = to;
                if (map.TryGetValue(textFinal, out to)) textFinal = to;
                if (map.TryGetValue(okFinal, out to)) okFinal = to;
                if (map.TryGetValue(warnFinal, out to)) warnFinal = to;
                var m2 = map;
                xaml = Regex.Replace(xaml, "'(#[0-9A-Fa-f]{6,8})'", m =>
                {
                    string r;
                    return m2.TryGetValue(m.Groups[1].Value.ToUpperInvariant(), out r) ? "'" + r + "'" : m.Value;
                });
                if (custom) xaml = xaml.Replace("'White'", "'" + CustomColors()["accentText"] + "'");
            }

            // Заголовок окна и метки «запущено» тоже берут цвета темы.
            titleDark = Luma(bgFinal.Substring(0, 7)) <= 0.5;
            int[] bc = Rgb(bgFinal.Substring(0, 7)), tc = Rgb(textFinal.Substring(0, 7));
            titleCaption = bc[0] | (bc[1] << 8) | (bc[2] << 16);
            titleText = tc[0] | (tc[1] << 8) | (tc[2] << 16);
            int[] ok = Rgb(okFinal.Substring(0, 7)), warn = Rgb(warnFinal.Substring(0, 7));
            InfoBase.EnterpriseBrush = FrozenBrush(Color.FromRgb((byte)ok[0], (byte)ok[1], (byte)ok[2]));
            InfoBase.DesignerBrush = FrozenBrush(Color.FromRgb((byte)warn[0], (byte)warn[1], (byte)warn[2]));

            double scale = 1.0;
            switch (Setting("font", "m"))
            {
                case "xs": scale = 0.85; break;
                case "s": scale = 0.92; break;
                case "l": scale = 1.1; break;
                case "xl": scale = 1.2; break;
            }
            if (scale != 1.0)
            {
                MatchEvaluator font = m => m.Groups[1].Value + Num(Math.Round(double.Parse(m.Groups[2].Value,
                    System.Globalization.CultureInfo.InvariantCulture) * scale * 2) / 2) + "'";
                xaml = Regex.Replace(xaml, "(FontSize=')(\\d+(?:\\.\\d+)?)'", font);
                xaml = Regex.Replace(xaml, "(Property='FontSize' Value=')(\\d+(?:\\.\\d+)?)'", font);
                // Панель базы шире при крупном шрифте, чтобы подписи кнопок помещались.
                if (scale > 1.0) xaml = xaml.Replace("<ColumnDefinition Width='400'/>", "<ColumnDefinition Width='" + Num(Math.Round(400 * scale)) + "'/>");
            }

            int delta = Setting("buttons", "m") == "s" ? -6 : Setting("buttons", "m") == "l" ? 6 : 0;
            if (delta != 0)
            {
                xaml = Regex.Replace(xaml, "(Property='(?:Height|Width)' Value=')(40|48)'", m => m.Groups[1].Value + (int.Parse(m.Groups[2].Value) + delta) + "'");
                xaml = xaml.Replace(" Height='40'", " Height='" + (40 + delta) + "'");
            }
            return xaml;
        }

        static Brush FrozenBrush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        static string Num(double v)
        {
            return v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        // Красит системный заголовок окна в цвет темы (Windows 11; на старых системах просто ничего не меняется).
        static void SetTitleBar(Window w)
        {
            try
            {
                IntPtr h = new WindowInteropHelper(w).Handle;
                if (h == IntPtr.Zero) return;
                int dark = titleDark ? 1 : 0;
                int caption = titleCaption;
                int text = titleText;
                DwmSetWindowAttribute(h, 20, ref dark, 4);
                DwmSetWindowAttribute(h, 35, ref caption, 4);
                DwmSetWindowAttribute(h, 36, ref text, 4);
            }
            catch (Exception) { }
        }

        static void ApplyTitleBar(Window w)
        {
            w.SourceInitialized += delegate { SetTitleBar(w); };
        }

        // Окно настроек: каждое изменение применяется сразу, кнопок «Сохранить/Отмена» нет.
        static void ShowSettings()
        {
            Window dlg = null;
            bool loading = false;
            Action fill = null;
            var colorBoxes = new Dictionary<string, TextBox>();

            // Перерисовывает оба окна в новых цветах; поле, где стоял курсор, остаётся в фокусе.
            Action rebuild = delegate
            {
                dlg.Dispatcher.BeginInvoke(new Action(delegate
                {
                    var focused = Keyboard.FocusedElement as FrameworkElement;
                    string tag = focused == null ? null : focused.Tag as string;
                    BuildWindow();
                    fill();
                    TextBox tb;
                    if (tag != null && colorBoxes.TryGetValue(tag, out tb)) { tb.Focus(); tb.CaretIndex = tb.Text.Length; }
                }));
            };

            fill = delegate
            {
                var src = (Window)XamlReader.Parse(Themed(SettingsXaml));
                if (dlg == null)
                {
                    dlg = src;
                    ApplyTitleBar(dlg);
                    dlg.Owner = window;
                    dlg.Icon = window.Icon;
                    dlg.MaxHeight = SystemParameters.WorkArea.Height - 40;
                    dlg.PreviewKeyDown += delegate (object s, KeyEventArgs e) { if (e.Key == Key.Escape) dlg.Close(); };
                }
                else
                {
                    object content = src.Content;
                    src.Content = null;
                    dlg.Background = src.Background;
                    dlg.Foreground = src.Foreground;
                    dlg.FontSize = src.FontSize;
                    dlg.Content = content;
                    src.Close();
                    SetTitleBar(dlg);
                }
                var res = new ResourceDictionary();
                res.MergedDictionaries.Add(window.Resources);
                dlg.Resources = res;

                var showCommands = (CheckBox)src.FindName("SShowCommands");
                var platform = (TextBox)src.FindName("SPlatform");
                var hint = (TextBlock)src.FindName("SPlatformHint");
                var error = (TextBlock)src.FindName("SError");

                Action showPlatform = delegate
                {
                    hint.Text = platformBin == null
                        ? "Платформа не найдена. Укажите папку, где установлена 1С."
                        : "Сейчас используется " + platformVersion + "  ·  " + platformBin + "\nПустое поле — искать автоматически.";
                };

                loading = true;
                SelectOption(src, "Th", Setting("theme", "system"));
                SelectOption(src, "Fs", Setting("font", "m"));
                SelectOption(src, "Bs", Setting("buttons", "m"));
                showCommands.IsChecked = Setting("showCommands", "1") == "1";
                platform.Text = Setting("platform", "");
                showPlatform();
                loading = false;

                // Тема, размеры и показ команд: сохраняем и сразу перерисовываем оба окна.
                RoutedEventHandler apply = delegate
                {
                    if (loading) return;
                    string newTheme = SelectedOption(src, "Th", new[] { "system", "light", "dark", "custom" }, "system");
                    // При первом переходе на «Моя тема» берём за основу цвета текущей светлой темы.
                    if (newTheme == "custom" && Setting("theme", "system") != "custom" && !settings.Keys.Any(k => k.StartsWith("c:")) && IsLight())
                    {
                        foreach (var p in Palette)
                        {
                            string lv;
                            if (LightColors.TryGetValue(p[2].ToUpperInvariant(), out lv)) settings["c:" + p[0]] = lv;
                        }
                    }
                    settings["theme"] = newTheme;
                    settings["font"] = SelectedOption(src, "Fs", new[] { "xs", "s", "m", "l", "xl" }, "m");
                    settings["buttons"] = SelectedOption(src, "Bs", new[] { "s", "m", "l" }, "m");
                    settings["showCommands"] = showCommands.IsChecked == true ? "1" : "0";
                    SaveSettings();
                    rebuild();
                };
                foreach (string n in new[] { "Th_system", "Th_light", "Th_dark", "Th_custom", "Fs_xs", "Fs_s", "Fs_m", "Fs_l", "Fs_xl", "Bs_s", "Bs_m", "Bs_l" })
                {
                    ((RadioButton)src.FindName(n)).Checked += apply;
                }
                showCommands.Checked += apply;
                showCommands.Unchecked += apply;

                // Свои цвета: поле #RRGGBB, квадратик с выбором мышью и сброс. Применяется по Enter, при уходе из поля и после выбора цвета.
                var customPanel = (FrameworkElement)src.FindName("SCustom");
                customPanel.Visibility = Setting("theme", "system") == "custom" ? Visibility.Visible : Visibility.Collapsed;
                var rows = (Panel)src.FindName("SColors");
                colorBoxes.Clear();
                var current = CustomColors();
                foreach (var item in Palette)
                {
                    string key = item[0], label = item[1];
                    // Ячейка одной из двух колонок: подпись сверху, под ней квадратик цвета и поле #RRGGBB.
                    bool leftColumn = rows.Children.Count % 2 == 0;
                    var cell = new StackPanel { Margin = new Thickness(leftColumn ? 0 : 8, 0, leftColumn ? 8 : 0, 12) };
                    var caption = new TextBlock
                    {
                        Text = label, ToolTip = label, TextTrimming = TextTrimming.CharacterEllipsis,
                        Style = (Style)window.FindResource("Caption")
                    };
                    var row = new Grid();
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var swatch = new Button
                    {
                        Width = 34, Height = 34, Padding = new Thickness(0), Margin = new Thickness(0, 0, 8, 0),
                        ToolTip = "Подобрать цвет", Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(current[key]))
                    };
                    var box = new TextBox
                    {
                        Tag = key, Height = 34, MaxLength = 7, Text = current[key],
                        Padding = new Thickness(8, 0, 8, 0), FontFamily = new FontFamily("Cascadia Mono, Consolas")
                    };
                    Grid.SetColumn(box, 1);
                    row.Children.Add(swatch);
                    row.Children.Add(box);
                    cell.Children.Add(caption);
                    cell.Children.Add(row);
                    rows.Children.Add(cell);
                    colorBoxes[key] = box;

                    // Пока вводят, квадратик показывает цвет, а неверный ввод подсвечивается красной рамкой.
                    box.TextChanged += delegate
                    {
                        string h = NormHex(box.Text);
                        if (h == null) { box.BorderBrush = Brushes.IndianRed; return; }
                        box.ClearValue(Control.BorderBrushProperty);
                        swatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(h));
                    };
                    Action commit = delegate
                    {
                        string h = NormHex(box.Text);
                        if (h == null)
                        {
                            error.Text = "Цвет «" + label + "» указан неверно. Нужен формат #RRGGBB, например #1B1C20.";
                            return;
                        }
                        error.Text = "";
                        box.Text = h;
                        if (h == NormHex(Setting("c:" + key, Palette.First(q => q[0] == key)[2]))) return;
                        settings["c:" + key] = h;
                        SaveSettings();
                        rebuild();
                    };
                    box.LostFocus += delegate { commit(); };
                    box.KeyDown += delegate (object s, KeyEventArgs e) { if (e.Key == Key.Enter) { commit(); e.Handled = true; } };
                    // Палитра открывается под квадратиком; пока двигают мышь, меняется только поле,
                    // а тема перерисовывается один раз, когда палитру закрыли.
                    swatch.Click += delegate
                    {
                        ShowColorPicker(swatch, NormHex(box.Text) ?? current[key],
                            delegate (string hex) { box.Text = hex; },
                            delegate { commit(); });
                    };
                }
                ((Button)src.FindName("SGenerate")).Click += delegate
                {
                    foreach (var pair in GenerateTheme()) settings["c:" + pair.Key] = pair.Value;
                    error.Text = "";
                    SaveSettings();
                    rebuild();
                };
                ((Button)src.FindName("SResetColors")).Click += delegate
                {
                    foreach (string ck in settings.Keys.Where(q => q.StartsWith("c:")).ToList()) settings.Remove(ck);
                    SaveSettings();
                    rebuild();
                };

                // Папка платформы: применяется по Enter, при уходе из поля и после выбора папки.
                Action applyPlatform = delegate
                {
                    string oldPlatform = Setting("platform", "");
                    string newPlatform = platform.Text.Trim().Trim('"').TrimEnd('\\', '/');
                    if (newPlatform == oldPlatform) return;
                    error.Text = "";
                    if (newPlatform.Length > 0)
                    {
                        settings["platform"] = newPlatform;
                        FindPlatform();
                        if (platformBin == null || !platformBin.StartsWith(newPlatform, StringComparison.OrdinalIgnoreCase))
                        {
                            if (oldPlatform.Length > 0) settings["platform"] = oldPlatform; else settings.Remove("platform");
                            FindPlatform();
                            error.Text = "В этой папке не найдена 1С (нужен файл bin\\1cv8.exe).";
                            return;
                        }
                    }
                    else
                    {
                        settings.Remove("platform");
                        FindPlatform();
                    }
                    SaveSettings();
                    showPlatform();
                    var b = list.SelectedItem as InfoBase;
                    UpdateStatus(b == null ? "" : b.Connect);
                    UpdatePreview();
                };
                platform.LostFocus += delegate { applyPlatform(); };
                platform.KeyDown += delegate (object s, KeyEventArgs e) { if (e.Key == Key.Enter) { applyPlatform(); e.Handled = true; } };
                ((Button)src.FindName("SBrowse")).Click += delegate
                {
                    using (var fb = new System.Windows.Forms.FolderBrowserDialog())
                    {
                        fb.Description = "Папка платформы 1С (например …\\1cv8 или …\\8.3.27.2170)";
                        if (Directory.Exists(platform.Text.Trim())) fb.SelectedPath = platform.Text.Trim();
                        if (fb.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                        platform.Text = fb.SelectedPath;
                        applyPlatform();
                    }
                };
            };

            fill();
            dlg.ShowDialog();
        }

        // ---------- «Моя тема»: палитра и генератор ----------

        static readonly Random Rnd = new Random();

        // Цвет из оттенка (0–360), насыщенности и яркости (0–1) в виде #RRGGBB.
        static string Hsv(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            s = Math.Max(0, Math.Min(1, s));
            v = Math.Max(0, Math.Min(1, v));
            double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
            double r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; }
            else if (h < 120) { r = x; g = c; }
            else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; }
            else if (h < 300) { r = x; b = c; }
            else { r = c; b = x; }
            return string.Format("#{0:X2}{1:X2}{2:X2}",
                (int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
        }

        static void ToHsv(string hex, out double h, out double s, out double v)
        {
            int[] c = Rgb(hex);
            double r = c[0] / 255.0, g = c[1] / 255.0, b = c[2] / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            h = 0;
            if (d > 0)
            {
                if (max == r) h = 60 * (((g - b) / d + 6) % 6);
                else if (max == g) h = 60 * ((b - r) / d + 2);
                else h = 60 * ((r - g) / d + 4);
            }
            s = max == 0 ? 0 : d / max;
            v = max;
        }

        // Случайная тема для «Создай за меня»: фон, панели и текст одного оттенка (тёмные или светлые),
        // акцент — того же оттенка, соседнего или противоположного, чтобы цвета сочетались.
        static Dictionary<string, string> GenerateTheme()
        {
            double h = Rnd.Next(360);
            bool dark = Rnd.Next(100) < 55;
            double[] shifts = { 0, 30, 150, 180, 210, 330 };
            double ha = h + shifts[Rnd.Next(shifts.Length)];
            double ha2 = ha + 25 + Rnd.Next(30);
            var c = new Dictionary<string, string>();
            if (dark)
            {
                double sb = 0.10 + Rnd.NextDouble() * 0.25;
                c["bg"] = Hsv(h, sb, 0.10);
                c["panel"] = Hsv(h, sb, 0.13);
                c["field"] = Hsv(h, sb * 0.9, 0.15);
                c["button"] = Hsv(h, sb * 0.9, 0.19);
                c["border"] = Hsv(h, sb * 0.8, 0.27);
                c["text"] = Hsv(h, 0.04, 0.93);
                c["text2"] = Hsv(h, 0.10, 0.62);
                c["accent"] = Hsv(ha, 0.62, 0.96);
                c["accent2"] = Hsv(ha2, 0.66, 0.90);
                c["ok"] = Hsv(140, 0.62, 0.76);
                c["warn"] = Hsv(36, 0.76, 0.95);
                c["err"] = Hsv(2, 0.52, 0.95);
            }
            else
            {
                double sb = 0.02 + Rnd.NextDouble() * 0.06;
                c["bg"] = Hsv(h, sb, 0.98);
                c["panel"] = Hsv(h, sb * 1.6, 0.95);
                c["field"] = Hsv(h, sb * 0.3, 1.0);
                c["button"] = Hsv(h, sb * 2, 0.92);
                c["border"] = Hsv(h, sb * 2.4, 0.83);
                c["text"] = Hsv(h, 0.35, 0.20);
                c["text2"] = Hsv(h, 0.18, 0.50);
                c["accent"] = Hsv(ha, 0.72, 0.78);
                c["accent2"] = Hsv(ha2, 0.72, 0.66);
                c["ok"] = Hsv(140, 0.70, 0.52);
                c["warn"] = Hsv(34, 0.92, 0.68);
                c["err"] = Hsv(4, 0.72, 0.76);
            }
            c["accentText"] = Luma(c["accent"]) > 0.55 ? "#14151A" : "#FFFFFF";
            c["selected"] = Mix(c["bg"], c["accent"], dark ? 0.26 : 0.16);
            return c;
        }

        // Цвет тёмной темы из разметки в текущей теме (для элементов, которые создаются кодом).
        static Color ThemeColor(string darkHex)
        {
            Dictionary<string, string> map = null;
            if (Setting("theme", "system") == "custom") map = CustomMap();
            else if (IsLight()) map = LightColors;
            string hex;
            if (map == null || !map.TryGetValue(darkHex, out hex)) hex = darkHex;
            return (Color)ColorConverter.ConvertFromString(hex);
        }

        static readonly string[] PickerPresets =
        {
            "#EF4444", "#F97316", "#F59E0B", "#84CC16", "#22C55E", "#14B8A6", "#06B6D4", "#3B82F6",
            "#6366F1", "#8B5CF6", "#D946EF", "#EC4899", "#FFFFFF", "#9CA3AF", "#374151", "#111827"
        };

        // Палитра выбора цвета под кнопкой target: поле «насыщенность × яркость», полоса оттенков и готовые цвета.
        // onPick вызывается при каждом изменении, onClosed — один раз после закрытия, если цвет меняли.
        static void ShowColorPicker(FrameworkElement target, string startHex, Action<string> onPick, Action onClosed)
        {
            const double W = 232, H = 132;
            double hue, sat, val;
            ToHsv(startHex, out hue, out sat, out val);
            bool changed = false;

            var hueLayer = new Border { CornerRadius = new CornerRadius(8) };
            var whiteLayer = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0)
            };
            var blackLayer = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90)
            };
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 14, Height = 14, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 0.7 }
            };
            var field = new Grid { Width = W, Height = H, Cursor = Cursors.Cross, Background = Brushes.Transparent };
            field.Children.Add(hueLayer);
            field.Children.Add(whiteLayer);
            field.Children.Add(blackLayer);
            field.Children.Add(dot);

            var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            for (int i = 0; i <= 6; i++)
                rainbow.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(Hsv(i * 60, 1, 1)), i / 6.0));
            var hueMark = new Border
            {
                Width = 6, Height = 18, CornerRadius = new CornerRadius(3), Background = Brushes.White, IsHitTestVisible = false,
                BorderBrush = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center
            };
            var hueBar = new Grid { Width = W, Height = 18, Margin = new Thickness(0, 10, 0, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            hueBar.Children.Add(new Border { Height = 12, CornerRadius = new CornerRadius(6), Background = rainbow, VerticalAlignment = VerticalAlignment.Center });
            hueBar.Children.Add(hueMark);

            var preview = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
            var hexText = new TextBlock
            {
                FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0), Foreground = new SolidColorBrush(ThemeColor("#E6E7EA"))
            };
            var current = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 8) };
            current.Children.Add(preview);
            current.Children.Add(hexText);

            Action show = delegate
            {
                string hex = Hsv(hue, sat, val);
                hueLayer.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Hsv(hue, 1, 1)));
                dot.Margin = new Thickness(sat * W - 7, (1 - val) * H - 7, 0, 0);
                hueMark.Margin = new Thickness(hue / 360 * W - 3, 0, 0, 0);
                preview.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                hexText.Text = hex;
            };
            Action pick = delegate
            {
                show();
                changed = true;
                onPick(Hsv(hue, sat, val));
            };

            // Мышь не захватывается (иначе всплывающее окно закрылось бы), цвет меняется, пока кнопка зажата над полем.
            Action<MouseEventArgs> fromField = delegate (MouseEventArgs e)
            {
                Point p = e.GetPosition(field);
                sat = Math.Max(0, Math.Min(1, p.X / W));
                val = 1 - Math.Max(0, Math.Min(1, p.Y / H));
                pick();
            };
            field.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e) { fromField(e); };
            field.MouseMove += delegate (object s, MouseEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) fromField(e); };
            Action<MouseEventArgs> fromHue = delegate (MouseEventArgs e)
            {
                hue = Math.Max(0, Math.Min(359.9, e.GetPosition(hueBar).X / W * 360));
                pick();
            };
            hueBar.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e) { fromHue(e); };
            hueBar.MouseMove += delegate (object s, MouseEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) fromHue(e); };

            var presets = new UniformGrid { Columns = 8, Width = W };
            foreach (string preset in PickerPresets)
            {
                string hex = preset;
                var chip = new Border
                {
                    Height = 22, Margin = new Thickness(2), CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand, ToolTip = hex,
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                    BorderBrush = new SolidColorBrush(ThemeColor("#30333A")), BorderThickness = new Thickness(1)
                };
                chip.MouseLeftButtonDown += delegate
                {
                    ToHsv(hex, out hue, out sat, out val);
                    pick();
                };
                presets.Children.Add(chip);
            }
            preview.BorderBrush = new SolidColorBrush(ThemeColor("#30333A"));

            var body = new StackPanel();
            body.Children.Add(field);
            body.Children.Add(hueBar);
            body.Children.Add(current);
            body.Children.Add(presets);
            var frame = new Border
            {
                Child = body, Padding = new Thickness(12), Margin = new Thickness(10), CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(ThemeColor("#1B1C20")),
                BorderBrush = new SolidColorBrush(ThemeColor("#30333A")), BorderThickness = new Thickness(1),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.35 }
            };
            var popup = new Popup
            {
                Child = frame, PlacementTarget = target, Placement = PlacementMode.Bottom, HorizontalOffset = -10,
                StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade
            };
            popup.Closed += delegate { if (changed) onClosed(); };
            show();
            popup.IsOpen = true;
        }

        static void SelectOption(Window dlg, string group, string value)
        {
            var rb = dlg.FindName(group + "_" + value) as RadioButton;
            if (rb != null) rb.IsChecked = true;
        }

        static string SelectedOption(Window dlg, string group, string[] values, string fallback)
        {
            foreach (string v in values)
            {
                var rb = dlg.FindName(group + "_" + v) as RadioButton;
                if (rb != null && rb.IsChecked == true) return v;
            }
            return fallback;
        }

        const string SettingsXaml = @"
<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
        xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
        Title='Настройки' Width='580' SizeToContent='Height' ResizeMode='NoResize' ShowInTaskbar='False'
        WindowStartupLocation='CenterOwner' Background='#16171A'
        FontFamily='Segoe UI' FontSize='14' Foreground='#E6E7EA' UseLayoutRounding='True'
        TextOptions.TextFormattingMode='Display'>
 <ScrollViewer VerticalScrollBarVisibility='Auto'>
  <StackPanel Margin='24,4,24,24'>
    <TextBlock Text='ТЕМА' Style='{DynamicResource Section}'/>
    <Border Background='#1D1F23' BorderBrush='#30333A' BorderThickness='1' CornerRadius='11'>
      <UniformGrid Rows='1'>
        <RadioButton x:Name='Th_system' GroupName='Th' Style='{DynamicResource Seg}' Content='Как в системе'/>
        <RadioButton x:Name='Th_light' GroupName='Th' Style='{DynamicResource Seg}' Content='Светлая'/>
        <RadioButton x:Name='Th_dark' GroupName='Th' Style='{DynamicResource Seg}' Content='Тёмная'/>
        <RadioButton x:Name='Th_custom' GroupName='Th' Style='{DynamicResource Seg}' Content='Моя тема'/>
      </UniformGrid>
    </Border>

    <StackPanel x:Name='SCustom' Visibility='Collapsed'>
      <TextBlock Text='МОЯ ТЕМА' Style='{DynamicResource Section}'/>
      <TextBlock Text='Цвет в формате #RRGGBB. Нажмите на квадратик, чтобы подобрать цвет мышью, или на «Создай за меня» — тема соберётся сама.'
                 Foreground='#6E7480' FontSize='12' Margin='2,0,0,12' TextWrapping='Wrap'/>
      <UniformGrid x:Name='SColors' Columns='2'/>
      <StackPanel Orientation='Horizontal' Margin='0,2,0,0'>
        <Button x:Name='SGenerate' Content='Создай за меня' Style='{DynamicResource Primary}' Height='40' Padding='18,0' Margin='0,0,8,0'
                ToolTip='Заполнить все цвета случайной гармоничной темой. Не понравилось — нажмите ещё раз.'/>
        <Button x:Name='SResetColors' Content='Сбросить цвета' Padding='18,0'
                ToolTip='Вернуть цвета тёмной темы'/>
      </StackPanel>
    </StackPanel>

    <TextBlock Text='РАЗМЕР ШРИФТА' Style='{DynamicResource Section}'/>
    <Border Background='#1D1F23' BorderBrush='#30333A' BorderThickness='1' CornerRadius='11'>
      <UniformGrid Rows='1'>
        <RadioButton x:Name='Fs_xs' GroupName='Fs' Style='{DynamicResource Seg}' Content='XS'/>
        <RadioButton x:Name='Fs_s' GroupName='Fs' Style='{DynamicResource Seg}' Content='S'/>
        <RadioButton x:Name='Fs_m' GroupName='Fs' Style='{DynamicResource Seg}' Content='M'/>
        <RadioButton x:Name='Fs_l' GroupName='Fs' Style='{DynamicResource Seg}' Content='L'/>
        <RadioButton x:Name='Fs_xl' GroupName='Fs' Style='{DynamicResource Seg}' Content='XL'/>
      </UniformGrid>
    </Border>

    <TextBlock Text='РАЗМЕР КНОПОК' Style='{DynamicResource Section}'/>
    <Border Background='#1D1F23' BorderBrush='#30333A' BorderThickness='1' CornerRadius='11'>
      <UniformGrid Rows='1'>
        <RadioButton x:Name='Bs_s' GroupName='Bs' Style='{DynamicResource Seg}' Content='Маленькие'/>
        <RadioButton x:Name='Bs_m' GroupName='Bs' Style='{DynamicResource Seg}' Content='Средние'/>
        <RadioButton x:Name='Bs_l' GroupName='Bs' Style='{DynamicResource Seg}' Content='Большие'/>
      </UniformGrid>
    </Border>

    <TextBlock Text='ИНТЕРФЕЙС' Style='{DynamicResource Section}'/>
    <CheckBox x:Name='SShowCommands' Content='Показывать команды запуска в панели базы' Margin='2,0,0,0'/>

    <TextBlock Text='ПЛАТФОРМА 1С' Style='{DynamicResource Section}'/>
    <Grid>
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width='*'/>
        <ColumnDefinition Width='Auto'/>
      </Grid.ColumnDefinitions>
      <TextBox x:Name='SPlatform'/>
      <Button x:Name='SBrowse' Grid.Column='1' Style='{DynamicResource IconBtn}' Content='&#xE8B7;'
              ToolTip='Выбрать папку' Margin='8,0,0,0'/>
    </Grid>
    <TextBlock x:Name='SPlatformHint' Foreground='#6E7480' FontSize='12' Margin='2,6,0,0' TextWrapping='Wrap'/>

    <TextBlock x:Name='SError' Foreground='#F27C7C' FontSize='13' Margin='2,12,0,0' TextWrapping='Wrap'/>

    <TextBlock Text='Изменения применяются сразу.  Esc — закрыть' Foreground='#5F6570' FontSize='11' HorizontalAlignment='Center' Margin='0,10,0,0'/>
  </StackPanel>
 </ScrollViewer>
</Window>";

        static void OnKey(object sender, KeyEventArgs e)
        {
            // Пока создаётся база, клавиши не должны запускать или закрывать что-либо.
            if (busy.Visibility == Visibility.Visible) { e.Handled = true; return; }
            if (e.Key == Key.Escape)
            {
                if (search.Text.Length > 0) search.Text = ""; else window.Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                Launch((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control);
                e.Handled = true;
            }
            else if ((e.Key == Key.Down || e.Key == Key.Up) && search.IsKeyboardFocused && list.Items.Count > 0)
            {
                int i = list.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
                list.SelectedIndex = Math.Max(0, Math.Min(list.Items.Count - 1, i));
                list.ScrollIntoView(list.SelectedItem);
                e.Handled = true;
            }
        }

        // ---------- данные ----------

        // Читает ibases.v8i целиком: все секции и строки до первой секции.
        static void ReadFile()
        {
            sections.Clear();
            preamble.Clear();
            if (!File.Exists(BasesFile)) return;
            InfoBase cur = null;
            foreach (string raw in File.ReadAllLines(BasesFile, Encoding.UTF8))
            {
                string line = raw.Trim().TrimStart((char)0xFEFF);
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    cur = new InfoBase { Name = line.Substring(1, line.Length - 2) };
                    sections.Add(cur);
                    continue;
                }
                if (cur == null) { preamble.Add(raw); continue; }
                cur.Lines.Add(line);
                if (line.StartsWith("Connect=", StringComparison.OrdinalIgnoreCase)) cur.SetConnect(line.Substring(8));
            }
        }

        // Записывает список обратно в том же виде, что и 1С: UTF-8 с BOM, секция на базу.
        static void WriteFile()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BasesFile));
            if (!backedUp && File.Exists(BasesFile))
            {
                File.Copy(BasesFile, BasesFile + ".launcher-bak", true);
                backedUp = true;
            }
            var lines = new List<string>(preamble);
            foreach (var s in sections)
            {
                lines.Add("[" + s.Name + "]");
                lines.AddRange(s.Lines);
            }
            File.WriteAllLines(BasesFile, lines, new UTF8Encoding(true));
        }

        static void LoadBases(string select = null)
        {
            ReadFile();
            bases = sections.Where(b => !string.IsNullOrEmpty(b.Connect)).ToList();
            title.Text = bases.Count.ToString();
            list.SelectedItem = null;
            ApplyFilter();
            RefreshRunning();

            string last = select;
            if (last != null || settings.TryGetValue("last", out last))
            {
                var found = list.Items.Cast<InfoBase>().FirstOrDefault(b => b.Name == last);
                if (found != null) { list.SelectedItem = found; list.ScrollIntoView(found); }
            }
            if (list.SelectedItem == null && list.Items.Count > 0) list.SelectedIndex = 0;
        }

        // ---------- добавить / изменить / удалить ----------

        static void AddBase()
        {
            BaseForm f = ShowBaseDialog(null);
            if (f == null) return;
            if (!f.Create) { AppendBase(f); return; }

            // Новая пустая база: 1cv8.exe CREATEINFOBASE File="папка"
            string log = Path.Combine(Path.GetTempPath(), "1c-launcher-create.log");
            string args = CreateArgs(f.FilePath) + " /Out \"" + log + "\"";
            var createWith = PlatformFor(f.Version, f.Arch);
            string createBin = createWith == null ? platformBin : createWith.Bin;
            busyText.Text = "Создаю базу «" + f.Name + "»…";
            busy.Visibility = Visibility.Visible;
            UpdateStatus("Создаю базу «" + f.Name + "»…");
            Task.Factory.StartNew(() =>
            {
                using (var p = Process.Start(new ProcessStartInfo(Path.Combine(createBin, "1cv8.exe"), args) { UseShellExecute = false }))
                {
                    p.WaitForExit();
                }
            }).ContinueWith(t => window.Dispatcher.BeginInvoke(new Action(() =>
            {
                busy.Visibility = Visibility.Collapsed;
                if (File.Exists(Path.Combine(f.FilePath, "1Cv8.1CD")))
                {
                    AppendBase(f);
                    UpdateStatus("База «" + f.Name + "» создана");
                    return;
                }
                string details = t.IsFaulted ? t.Exception.InnerException.Message
                    : File.Exists(log) ? File.ReadAllText(log).Trim() : "";
                UpdateStatus("Базу создать не удалось");
                MessageBox.Show(window, "Не удалось создать базу.\n" + details, "Запуск 1С");
            })));
        }

        static string CreateArgs(string folder)
        {
            return "CREATEINFOBASE File=\"" + folder + "\" /DisableStartupDialogs";
        }

        static void AppendBase(BaseForm f)
        {
            ReadFile();
            var b = new InfoBase { Name = f.Name };
            int order = sections.Select(s => LineInt(s, "OrderInList")).DefaultIfEmpty(16128).Max() + 256;
            int tree = sections.Select(s => LineInt(s, "OrderInTree")).DefaultIfEmpty(0).Max() + 16384;
            b.Lines.AddRange(new[]
            {
                "ID=" + Guid.NewGuid().ToString(),
                "OrderInList=" + order,
                "Folder=/",
                "OrderInTree=" + tree,
                "External=0",
                "ClientConnectionSpeed=Normal",
                "App=Auto",
                "WA=1",
                "Version=8.3"
            });
            b.SetConnect(f.Connect);
            ApplyForm(b, f);
            sections.Add(b);
            if (!TryWrite()) return;
            SaveExtra(f.Name, f.Extra);
            LoadBases(f.Name);
        }

        // Переносит настройки запуска из окна в секцию базы (только значения, которые 1С тоже хранит в своём списке).
        static void ApplyForm(InfoBase b, BaseForm f)
        {
            b.Set("App", f.Client);
            b.Set("ClientConnectionSpeed", f.Speed);
            b.Set("Version", f.Version);
            b.Set("AppArch", f.Arch == "Auto" ? null : f.Arch);
        }

        static void SaveExtra(string name, string extra)
        {
            if (string.IsNullOrEmpty(extra)) settings.Remove("args:" + name); else settings["args:" + name] = extra;
            SaveSettings();
        }

        static void EditBase()
        {
            var sel = list.SelectedItem as InfoBase;
            if (sel == null) return;
            BaseForm f = ShowBaseDialog(sel);
            if (f == null) return;

            ReadFile();
            var b = sections.FirstOrDefault(s => s.Name == sel.Name);
            if (b == null) { LoadBases(); return; }
            string oldName = b.Name;
            b.Name = f.Name;
            b.SetConnect(f.Connect);
            ApplyForm(b, f);
            if (!TryWrite()) return;

            string u;
            if (oldName != f.Name && settings.TryGetValue("user:" + oldName, out u))
            {
                settings.Remove("user:" + oldName);
                settings["user:" + f.Name] = u;
            }
            settings.Remove("args:" + oldName);
            if (!string.IsNullOrEmpty(f.Extra)) settings["args:" + f.Name] = f.Extra;
            settings["last"] = f.Name;
            SaveSettings();
            LoadBases(f.Name);
        }

        static void DeleteBase()
        {
            var sel = list.SelectedItem as InfoBase;
            if (sel == null) return;
            var answer = MessageBox.Show(window,
                "Удалить базу «" + sel.Name + "» из списка?\n\nФайлы базы останутся на диске:\n" + sel.Sub,
                "Удаление базы", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            ReadFile();
            sections.RemoveAll(s => s.Name == sel.Name);
            if (!TryWrite()) return;
            settings.Remove("user:" + sel.Name);
            settings.Remove("args:" + sel.Name);
            SaveSettings();
            LoadBases();
        }

        static bool TryWrite()
        {
            try
            {
                WriteFile();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(window, "Не удалось сохранить список баз:\n" + ex.Message, "Запуск 1С");
                return false;
            }
        }

        static int LineInt(InfoBase b, string key)
        {
            string line = b.Lines.FirstOrDefault(l => l.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
            int v;
            return line != null && int.TryParse(line.Substring(key.Length + 1), out v) ? v : 0;
        }

        // Окно добавления (existing == null) или изменения базы. Возвращает null, если нажали «Отмена».
        static BaseForm ShowBaseDialog(InfoBase existing)
        {
            var dlg = (Window)XamlReader.Parse(Themed(DialogXaml));
            ApplyTitleBar(dlg);
            dlg.Owner = window;
            dlg.Icon = window.Icon;
            dlg.Resources.MergedDictionaries.Add(window.Resources);
            dlg.Title = existing == null ? "Добавление базы" : "Изменение базы";

            var modePanel = (FrameworkElement)dlg.FindName("ModePanel");
            var modeCreate = (RadioButton)dlg.FindName("ModeCreate");
            var kindFile = (RadioButton)dlg.FindName("KindFile");
            var kindServer = (RadioButton)dlg.FindName("KindServer");
            var kindWeb = (RadioButton)dlg.FindName("KindWeb");
            var web = (TextBox)dlg.FindName("DWeb");
            var version = (TextBox)dlg.FindName("DVersion");
            var versionHint = (TextBlock)dlg.FindName("DVersionHint");
            var extra = (TextBox)dlg.FindName("DExtra");
            var filePanel = (FrameworkElement)dlg.FindName("FilePanel");
            var serverPanel = (FrameworkElement)dlg.FindName("ServerPanel");
            var webPanel = (FrameworkElement)dlg.FindName("WebPanel");
            dlg.MaxHeight = SystemParameters.WorkArea.Height - 40;
            var name = (TextBox)dlg.FindName("DName");
            var path = (TextBox)dlg.FindName("DPath");
            var pathHint = (TextBlock)dlg.FindName("DPathHint");
            var srvr = (TextBox)dlg.FindName("DServer");
            var refName = (TextBox)dlg.FindName("DRef");
            var command = (TextBox)dlg.FindName("DCommand");
            var commandPanel = (FrameworkElement)dlg.FindName("CommandPanel");
            var error = (TextBlock)dlg.FindName("DError");
            var ok = (Button)dlg.FindName("DOk");

            if (existing != null)
            {
                modePanel.Visibility = Visibility.Collapsed;
                ((RadioButton)dlg.FindName("ModeExisting")).IsChecked = true;
                name.Text = existing.Name;
                if (existing.Server != null)
                {
                    kindServer.IsChecked = true;
                    srvr.Text = existing.Server;
                    refName.Text = existing.Ref;
                }
                else if (existing.Ws != null)
                {
                    kindWeb.IsChecked = true;
                    web.Text = existing.Ws;
                }
                else path.Text = existing.FilePath ?? "";
                extra.Text = ExtraArgs(existing);
            }

            // Подсказки под полем параметров: клик добавляет флаг или убирает его; подсветка следует за текстом поля.
            var flagModern = (ToggleButton)dlg.FindName("FlagModern");
            var flagDark = (ToggleButton)dlg.FindName("FlagDark");
            var flagTech = (ToggleButton)dlg.FindName("FlagTech");
            var flagDebug = (ToggleButton)dlg.FindName("FlagDebug");
            Action syncFlags = delegate
            {
                string design = DesignFlag(extra.Text);
                flagModern.IsChecked = design == "modern";
                flagDark.IsChecked = design == "dark";
                flagTech.IsChecked = HasFlag(extra.Text, "/TechnicalSpecialistMode");
                flagDebug.IsChecked = HasFlag(extra.Text, "/Debug");
            };
            extra.TextChanged += delegate { syncFlags(); };
            flagModern.Click += delegate { extra.Text = SetDesignFlag(extra.Text, DesignFlag(extra.Text) == "modern" ? "" : "modern"); syncFlags(); };
            flagDark.Click += delegate { extra.Text = SetDesignFlag(extra.Text, DesignFlag(extra.Text) == "dark" ? "" : "dark"); syncFlags(); };
            flagTech.Click += delegate { extra.Text = ToggleFlag(extra.Text, "/TechnicalSpecialistMode"); syncFlags(); };
            flagDebug.Click += delegate { extra.Text = ToggleFlag(extra.Text, "/Debug"); syncFlags(); };
            syncFlags();

            // Настройки запуска: у существующей базы берутся из её секции, у новой — значения по умолчанию.
            string curApp = existing == null ? "Auto" : existing.App;
            SelectOption(dlg, "App", curApp);
            SelectOption(dlg, "Speed", existing == null ? "Normal" : existing.Get("ClientConnectionSpeed", "Normal"));
            SelectOption(dlg, "Arch", existing == null ? "Auto" : existing.Get("AppArch", "Auto"));
            version.Text = existing == null ? "8.3" : existing.Get("Version", "8.3");
            versionHint.Text = platforms.Count == 0
                ? "Платформа не найдена. «8.3» — самая новая из установленных."
                : "Установлено: " + string.Join(", ", platforms.OrderByDescending(p => p.Ver)
                    .Select(p => p.Ver + (p.Arch == "x86" ? " (x86)" : " (x64)")).ToArray())
                  + ".\n«8.3» или «8.3.27» — самая новая подходящая версия; можно указать и точную.";

            Action refresh = delegate
            {
                bool create = modeCreate.IsChecked == true;
                if (create) kindFile.IsChecked = true;
                kindServer.IsEnabled = !create;
                kindWeb.IsEnabled = !create;
                bool file = kindFile.IsChecked == true;
                filePanel.Visibility = file ? Visibility.Visible : Visibility.Collapsed;
                serverPanel.Visibility = kindServer.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
                webPanel.Visibility = kindWeb.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
                pathHint.Text = create
                    ? "Папка для новой базы. Если её нет, 1С создаст её сама."
                    : "Папка, в которой лежит файл 1Cv8.1CD.";
                commandPanel.Visibility = create ? Visibility.Visible : Visibility.Collapsed;
                command.Text = platformBin == null ? "" :
                    "\"" + Path.Combine(platformBin, "1cv8.exe") + "\" " + CreateArgs(path.Text.Trim().Trim('"'));
                ok.Content = existing != null ? "Сохранить" : create ? "Создать" : "Добавить";
                error.Text = "";
            };
            foreach (string n in new[] { "ModeCreate", "ModeExisting", "KindFile", "KindServer", "KindWeb" })
            {
                ((RadioButton)dlg.FindName(n)).Checked += delegate { refresh(); };
            }
            path.TextChanged += delegate { refresh(); };
            refresh();

            ((Button)dlg.FindName("DBrowse")).Click += delegate
            {
                using (var fb = new System.Windows.Forms.FolderBrowserDialog())
                {
                    fb.Description = "Папка информационной базы";
                    if (Directory.Exists(path.Text.Trim())) fb.SelectedPath = path.Text.Trim();
                    if (fb.ShowDialog() == System.Windows.Forms.DialogResult.OK) path.Text = fb.SelectedPath;
                }
            };

            BaseForm result = null;
            ok.Click += delegate
            {
                var f = new BaseForm { Name = name.Text.Trim(), Create = existing == null && modeCreate.IsChecked == true };
                string folder = path.Text.Trim().Trim('"').TrimEnd('\\', '/');
                bool hasBase = folder.Length > 0 && File.Exists(Path.Combine(folder, "1Cv8.1CD"));
                bool nameTaken = sections.Any(s => s != existing && string.Equals(s.Name, f.Name, StringComparison.OrdinalIgnoreCase));

                if (f.Name.Length == 0) error.Text = "Введите название базы.";
                else if (f.Name.IndexOfAny(new[] { '[', ']' }) >= 0) error.Text = "В названии нельзя использовать [ и ].";
                else if (nameTaken) error.Text = "База с таким названием уже есть в списке.";
                else if (kindFile.IsChecked == true)
                {
                    if (folder.Length == 0) error.Text = "Укажите папку базы.";
                    else if (folder.Contains("\"")) error.Text = "В пути не должно быть кавычек.";
                    else if (f.Create && hasBase) error.Text = "В этой папке уже есть база. Выберите «Добавить существующую».";
                    else if (f.Create && platformBin == null) error.Text = "Не найдена платформа 1С, создать базу нечем.";
                    else if (!f.Create && existing == null && !hasBase) error.Text = "В этой папке нет файла 1Cv8.1CD.";
                    else { f.FilePath = folder; f.Connect = "File=\"" + folder + "\";"; }
                }
                else if (kindWeb.IsChecked == true)
                {
                    string w = web.Text.Trim();
                    if (w.Length == 0) error.Text = "Укажите адрес публикации базы.";
                    else if (w.Contains("\"")) error.Text = "В адресе не должно быть кавычек.";
                    else f.Connect = "ws=\"" + w + "\";";
                }
                else
                {
                    string s = srvr.Text.Trim(), r = refName.Text.Trim();
                    if (s.Length == 0 || r.Length == 0) error.Text = "Укажите сервер и имя базы на сервере.";
                    else if ((s + r).Contains("\"")) error.Text = "В имени сервера и базы не должно быть кавычек.";
                    else f.Connect = "Srvr=\"" + s + "\";Ref=\"" + r + "\";";
                }
                if (f.Connect == null) return;

                // Параметры запуска. Версия: «8.3», «8.3.27» или точная; пусто — «8.3».
                string ver = version.Text.Trim();
                if (ver.Length == 0) ver = "8.3";
                if (!Regex.IsMatch(ver, @"^\d+(\.\d+){1,3}$")) { error.Text = "Версию платформы укажите цифрами через точку, например 8.3.27.2170."; return; }
                if (extra.Text.Contains("\n") || extra.Text.Contains("\r")) { error.Text = "Дополнительные параметры пишутся в одну строку."; return; }
                f.Client = SelectedOption(dlg, "App", new[] { "Auto", "ThinClient", "ThickClient" }, curApp);
                f.Speed = SelectedOption(dlg, "Speed", new[] { "Normal", "Low" }, "Normal");
                f.Arch = SelectedOption(dlg, "Arch", new[] { "Auto", "x86", "x86_64" }, "Auto");
                f.Version = ver;
                f.Extra = extra.Text.Trim();
                result = f;
                dlg.DialogResult = true;
            };
            dlg.Loaded += delegate { name.Focus(); name.SelectAll(); };
            dlg.ShowDialog();
            return result;
        }

        static void ApplyFilter()
        {
            string q = search.Text.Trim();
            searchHint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var selected = list.SelectedItem as InfoBase;
            list.ItemsSource = bases
                .Where(b => q.Length == 0
                    || b.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                    || (b.Sub ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            if (selected != null && list.Items.Contains(selected)) list.SelectedItem = selected;
            else if (list.Items.Count > 0) list.SelectedIndex = 0;
        }

        static void OnSelected()
        {
            var b = list.SelectedItem as InfoBase;
            string saved;
            user.Text = b != null && settings.TryGetValue("user:" + b.Name, out saved) ? saved : "";
            pass.Password = "";
            details.DataContext = b;
            details.Visibility = b == null ? Visibility.Collapsed : Visibility.Visible;
            emptyHint.Visibility = b == null ? Visibility.Visible : Visibility.Collapsed;
            UpdateStatus(b == null ? "" : b.Connect);
            UpdatePreview();
        }

        // Показывает точные команды обеих кнопок для выбранной базы (пароль скрыт).
        static void UpdatePreview()
        {
            var b = list.SelectedItem as InfoBase;
            string ent = b == null || platformBin == null ? "" : "\"" + ExePath(b, false) + "\" " + BuildArgs(b, false, true);
            string des = b == null || platformBin == null ? "" : "\"" + ExePath(b, true) + "\" " + BuildArgs(b, true, true);
            cmdEnterprise.Text = ent;
            cmdDesigner.Text = des;
            btnEnterprise.ToolTip = ent.Length > 0 ? ent : null;
            btnDesigner.ToolTip = des.Length > 0 ? des : null;
        }

        static void CopyCommand(TextBox box)
        {
            if (box.Text.Length == 0) return;
            try { Clipboard.SetText(box.Text); } catch (Exception) { }
        }

        static void UpdateStatus(string text)
        {
            var sel = list == null ? null : list.SelectedItem as InfoBase;
            var used = sel == null ? null : PlatformOf(sel);
            string platform = platformBin == null ? "платформа 1С не найдена"
                : "платформа " + (used == null ? platformVersion : used.Ver + " (" + (used.Arch == "x86" ? "x86" : "x64") + ")");
            status.Text = (string.IsNullOrEmpty(text) ? "" : text + "    ·    ") + platform;
        }

        // ---------- платформа ----------

        // Разрядность 1cv8.exe по заголовку файла: x86 или x86_64.
        static string PeArch(string exe)
        {
            try
            {
                using (var fs = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var br = new BinaryReader(fs))
                {
                    fs.Seek(0x3C, SeekOrigin.Begin);
                    fs.Seek(br.ReadInt32() + 4, SeekOrigin.Begin);
                    return br.ReadUInt16() == 0x8664 ? "x86_64" : "x86";
                }
            }
            catch (Exception) { return "x86_64"; }
        }

        // Какая платформа подходит базе: версия «8.3», «8.3.27» или точная, и разрядность Auto / x86 / x86_64.
        // Самая новая из подходящих; если подходящих нет, берётся самая новая из всех.
        static PlatformInfo PlatformFor(string version, string arch)
        {
            IEnumerable<PlatformInfo> found = platforms;
            if (!string.IsNullOrEmpty(arch) && arch != "Auto")
            {
                var byArch = found.Where(p => string.Equals(p.Arch, arch, StringComparison.OrdinalIgnoreCase)).ToList();
                if (byArch.Count > 0) found = byArch;
            }
            if (!string.IsNullOrEmpty(version))
            {
                var byVersion = found.Where(p => p.Ver.ToString() == version || p.Ver.ToString().StartsWith(version + ".")).ToList();
                if (byVersion.Count > 0) found = byVersion;
            }
            return found.OrderByDescending(p => p.Ver).ThenByDescending(p => p.Arch).FirstOrDefault();
        }

        static PlatformInfo PlatformOf(InfoBase b)
        {
            return PlatformFor(b.Get("Version", "8.3"), b.Get("AppArch", "Auto"));
        }

        static void FindPlatform()
        {
            platformBin = null;
            platformVersion = null;
            platforms = new List<PlatformInfo>();
            var roots = new List<string>();
            // Папка из настроек: сама bin, папка версии или папка со всеми версиями.
            string custom = Setting("platform", "");
            if (custom.Length > 0)
            {
                foreach (string bin in new[] { custom, Path.Combine(custom, "bin") })
                {
                    string exe = Path.Combine(bin, "1cv8.exe");
                    if (!File.Exists(exe)) continue;
                    platformBin = bin;
                    platformVersion = FileVersionInfo.GetVersionInfo(exe).FileVersion ?? "?";
                    Version one;
                    if (!Version.TryParse(platformVersion, out one)) one = new Version(0, 0);
                    platforms.Add(new PlatformInfo { Ver = one, Bin = bin, Arch = PeArch(exe) });
                    return;
                }
                if (Directory.Exists(custom)) roots.Add(custom);
            }
            // Автопоиск: стандартные папки, пути из настроек стартера 1С и из регистрации 1С в Windows.
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "1cv8"));
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "1cv8"));
            string pf64 = Environment.GetEnvironmentVariable("ProgramW6432");
            if (!string.IsNullOrEmpty(pf64)) roots.Add(Path.Combine(pf64, "1cv8"));
            roots.AddRange(StarterLocations());
            roots.AddRange(RegisteredLocations());

            foreach (string root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string[] dirs;
                try { dirs = Directory.GetDirectories(root); } catch (Exception) { continue; }
                foreach (string dir in dirs)
                {
                    Version v;
                    string bin = Path.Combine(dir, "bin");
                    string exe = Path.Combine(bin, "1cv8.exe");
                    if (Version.TryParse(Path.GetFileName(dir), out v) && File.Exists(exe)
                        && !platforms.Any(p => string.Equals(p.Bin, bin, StringComparison.OrdinalIgnoreCase)))
                    {
                        platforms.Add(new PlatformInfo { Ver = v, Bin = bin, Arch = PeArch(exe) });
                    }
                }
            }
            // Платформа по умолчанию (для создания баз и общей строки состояния) — самая новая.
            var best = PlatformFor(null, null);
            platformBin = best == null ? null : best.Bin;
            platformVersion = best == null ? null : best.Ver.ToString();
        }

        // Строки InstalledLocation=… из 1cestart.cfg (пользовательского и общего).
        static List<string> StarterLocations()
        {
            var result = new List<string>();
            foreach (var folder in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.CommonApplicationData })
            {
                try
                {
                    string cfg = Path.Combine(Environment.GetFolderPath(folder), "1C", "1CEStart", "1cestart.cfg");
                    if (!File.Exists(cfg)) continue;
                    foreach (string line in File.ReadAllLines(cfg))   // кодировку определяет BOM (обычно UTF-16)
                    {
                        if (line.StartsWith("InstalledLocation=", StringComparison.OrdinalIgnoreCase))
                            result.Add(line.Substring(18).Trim());
                    }
                }
                catch (Exception) { }
            }
            return result;
        }

        // Папка, где лежат версии платформы, по регистрации COM-классов 1С (…\<версия>\bin\1cv8.exe → …).
        static List<string> RegisteredLocations()
        {
            var result = new List<string>();
            foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
            {
                foreach (string progId in new[] { "V83.Application", "V83C.Application", "V83.COMConnector" })
                {
                    try
                    {
                        using (var root = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.ClassesRoot, view))
                        using (var idKey = root.OpenSubKey(progId + "\\CLSID"))
                        {
                            string clsid = idKey == null ? null : idKey.GetValue(null) as string;
                            if (clsid == null) continue;
                            foreach (string kind in new[] { "LocalServer32", "InprocServer32" })
                            {
                                using (var server = root.OpenSubKey("CLSID\\" + clsid + "\\" + kind))
                                {
                                    string path = server == null ? null : server.GetValue(null) as string;
                                    if (string.IsNullOrEmpty(path)) continue;
                                    var m = Regex.Match(path, "^\"?([^\"]+\\.(?:exe|dll))", RegexOptions.IgnoreCase);
                                    if (!m.Success) continue;
                                    string versionDir = Path.GetDirectoryName(Path.GetDirectoryName(m.Groups[1].Value));
                                    string parent = versionDir == null ? null : Path.GetDirectoryName(versionDir);
                                    if (parent != null) result.Add(parent);
                                }
                            }
                        }
                    }
                    catch (Exception) { }
                }
            }
            return result;
        }

        // ---------- запуск ----------

        static void Launch(bool designer)
        {
            var b = list.SelectedItem as InfoBase;
            if (b == null) return;
            if (platformBin == null)
            {
                MessageBox.Show(window, "Не найдена платформа 1С (папка с bin\\1cv8.exe).", "Запуск 1С");
                return;
            }

            string exe = ExePath(b, designer);
            string u = UserName();

            try
            {
                Process.Start(new ProcessStartInfo(exe, BuildArgs(b, designer, false))
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(exe)
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(window, "Не удалось запустить 1С:\n" + ex.Message, "Запуск 1С");
                return;
            }

            settings["last"] = b.Name;
            if (u.Length > 0) settings["user:" + b.Name] = u; else settings.Remove("user:" + b.Name);
            SaveSettings();

            if (closeAfter.IsChecked == true)
            {
                window.Close();
                return;
            }
            UpdateStatus("Запущен " + (designer ? "Конфигуратор" : "1С:Предприятие") + " — " + b.Name);
            RefreshRunning();
        }

        // ---------- что сейчас запущено ----------

        static void RefreshRunning()
        {
            if (polling) return;
            polling = true;
            Task.Factory.StartNew(() => QueryRunning())
                .ContinueWith(t => window.Dispatcher.BeginInvoke(new Action(() => ApplyRunning(t))));
        }

        static void ApplyRunning(Task<List<RunningOneC>> t)
        {
            polling = false;
            if (t.IsFaulted) return;
            foreach (var b in bases)
            {
                var mine = t.Result.Where(r => Matches(b, r)).ToList();
                b.SetRunning(mine.Where(r => !r.Designer).Select(r => r.Pid).ToList(),
                             mine.Where(r => r.Designer).Select(r => r.Pid).ToList());
            }
            allPids = t.Result.Select(r => r.Pid).ToList();
            btnCloseAll.IsEnabled = allPids.Count > 0;
            closeAllText.Text = allPids.Count > 0 ? "Закрыть все 1С  ·  " + allPids.Count : "Закрыть все 1С";
        }

        // Закрывает окна 1С так же, как крестик окна: 1С сама спросит о несохранённом.
        // force — снять процесс сразу, без вопросов (несохранённое пропадёт).
        static void CloseProcesses(List<int> pids, bool force)
        {
            foreach (int pid in pids)
            {
                try
                {
                    using (var p = Process.GetProcessById(pid))
                    {
                        if (force || p.MainWindowHandle == IntPtr.Zero) p.Kill(); else p.CloseMainWindow();
                    }
                }
                catch (Exception) { }
            }
            UpdateStatus(force ? "Процессы 1С сняты: " + pids.Count : "Отправлена команда закрытия окон 1С: " + pids.Count);
        }

        // Крестик в метке «Предприятие» / «Конфигуратор» у базы в списке.
        static void OnBadgeClose(object sender, RoutedEventArgs e)
        {
            var btn = e.OriginalSource as Button;
            if (btn == null || !(btn.Tag is string)) return;
            var b = btn.DataContext as InfoBase;
            if (b == null) return;
            e.Handled = true;
            bool force = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            CloseProcesses(new List<int>((string)btn.Tag == "D" ? b.DesignerPids : b.EnterprisePids), force);
        }

        static void CloseAll()
        {
            if (allPids.Count == 0) return;
            bool force = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            string text = force
                ? "Принудительно снять все процессы 1С (" + allPids.Count + ")?\n\nНесохранённые изменения пропадут."
                : "Закрыть все окна 1С (" + allPids.Count + ")?\n\nЕсли где-то есть несохранённые изменения, 1С спросит о них сама.";
            if (MessageBox.Show(window, text, "Закрыть все 1С", MessageBoxButton.YesNo, MessageBoxImage.Question,
                                MessageBoxResult.No) != MessageBoxResult.Yes) return;
            CloseProcesses(new List<int>(allPids), force);
        }

        static bool InsideButton(object source)
        {
            var d = source as DependencyObject;
            while (d != null)
            {
                if (d is Button) return true;
                d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        // Командные строки всех открытых 1cv8.exe / 1cv8c.exe (и запущенных лаунчером, и из обычного окна 1С).
        static List<RunningOneC> QueryRunning()
        {
            var result = new List<RunningOneC>();
            using (var searcher = new ManagementObjectSearcher(
                "SELECT Name, ProcessId, CommandLine FROM Win32_Process WHERE Name='1cv8.exe' OR Name='1cv8c.exe'"))
            {
                foreach (ManagementObject p in searcher.Get())
                {
                    string cmd = (p["CommandLine"] as string) ?? "";
                    string name = ((p["Name"] as string) ?? "").ToLowerInvariant();
                    var r = new RunningOneC
                    {
                        Pid = Convert.ToInt32(p["ProcessId"]),
                        Designer = name == "1cv8.exe" && Regex.IsMatch(cmd, @"(^|\s)DESIGNER(\s|$)", RegexOptions.IgnoreCase),
                        File = Arg(cmd, "F"),
                        Server = Arg(cmd, "S"),
                        IBName = Arg(cmd, "IBName")
                    };
                    string cs = Arg(cmd, "IBConnectionString");
                    if (cs != null)
                    {
                        var f = Regex.Match(cs, "File=\"?([^\";]+)", RegexOptions.IgnoreCase);
                        if (f.Success) r.File = f.Groups[1].Value;
                        var s = Regex.Match(cs, "Srvr=\"?([^\";]+)\"?;\\s*Ref=\"?([^\";]+)", RegexOptions.IgnoreCase);
                        if (s.Success) r.Server = s.Groups[1].Value + "\\" + s.Groups[2].Value;
                    }
                    result.Add(r);
                }
            }
            return result;
        }

        // Значение параметра вида /F"путь" или /Fпуть из командной строки 1С.
        static string Arg(string cmd, string key)
        {
            var m = Regex.Match(cmd, "(?:^|\\s)/" + key + "(?:\"((?:[^\"]|\"\")*)\"|([^\\s\"]+))", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            return m.Groups[1].Success ? m.Groups[1].Value.Replace("\"\"", "\"") : m.Groups[2].Value;
        }

        static bool Matches(InfoBase b, RunningOneC r)
        {
            if (r.File != null && b.FilePath != null) return NormPath(r.File) == NormPath(b.FilePath);
            if (r.Server != null && b.Server != null)
                return string.Equals(r.Server, b.Server + "\\" + b.Ref, StringComparison.OrdinalIgnoreCase);
            if (r.IBName != null) return string.Equals(r.IBName, b.Name, StringComparison.OrdinalIgnoreCase);
            return false;
        }

        static string NormPath(string p)
        {
            try { p = Path.GetFullPath(p); } catch (Exception) { }
            return p.TrimEnd('\\', '/').ToLowerInvariant();
        }

        static string UserName()
        {
            return user.Text.Trim().Replace("\"", "");
        }

        // Конфигуратор и толстый клиент — 1cv8.exe, обычная работа — тонкий клиент 1cv8c.exe.
        static string ExePath(InfoBase b, bool designer)
        {
            bool thick = designer || string.Equals(b.App, "ThickClient", StringComparison.OrdinalIgnoreCase);
            var p = PlatformOf(b);
            return Path.Combine(p == null ? platformBin : p.Bin, thick ? "1cv8.exe" : "1cv8c.exe");
        }

        // Дополнительные параметры командной строки, заданные для базы в лаунчере.
        static string ExtraArgs(InfoBase b)
        {
            return Setting("args:" + b.Name, "");
        }

        // ---------- флаги-подсказки в параметрах запуска ----------

        // Параметр /C"…" в строке параметров: 1С принимает только один, поэтому дизайн дописывается внутрь него.
        static readonly Regex LaunchParam = new Regex("(?<![\\w/])/C\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);

        // Вариант дизайна из /C"…;design=…": modern, dark или пустая строка.
        static string DesignFlag(string extra)
        {
            Match m = LaunchParam.Match(extra);
            if (!m.Success) return "";
            foreach (string part in m.Groups[1].Value.Split(';'))
            {
                string p = part.Trim();
                if (p.StartsWith("design=", StringComparison.OrdinalIgnoreCase)) return p.Substring(7).Trim().ToLowerInvariant();
            }
            return "";
        }

        // Ставит вариант дизайна (пустая строка убирает его), сохраняя остальное содержимое /C"…".
        static string SetDesignFlag(string extra, string design)
        {
            Match m = LaunchParam.Match(extra);
            var parts = new List<string>();
            if (m.Success)
            {
                parts = m.Groups[1].Value.Split(';').Select(p => p.Trim())
                    .Where(p => p.Length > 0 && !p.StartsWith("design=", StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (design.Length > 0) parts.Add("design=" + design);
            string flag = parts.Count == 0 ? "" : "/C\"" + string.Join(";", parts.ToArray()) + "\"";
            string result = m.Success
                ? extra.Substring(0, m.Index) + flag + extra.Substring(m.Index + m.Length)
                : extra.Trim() + " " + flag;
            return Regex.Replace(result, "\\s{2,}", " ").Trim();
        }

        static Regex FlagPattern(string flag)
        {
            return new Regex("(?<!\\S)" + Regex.Escape(flag) + "(?!\\S)", RegexOptions.IgnoreCase);
        }

        static bool HasFlag(string extra, string flag)
        {
            return FlagPattern(flag).IsMatch(extra);
        }

        // Добавляет простой флаг в конец строки или убирает его, если он уже есть.
        static string ToggleFlag(string extra, string flag)
        {
            string result = HasFlag(extra, flag) ? FlagPattern(flag).Replace(extra, "") : extra.Trim() + " " + flag;
            return Regex.Replace(result, "\\s{2,}", " ").Trim();
        }

        // Одна и та же строка и показывается, и запускается; maskPassword прячет пароль для показа.
        static string BuildArgs(InfoBase b, bool designer, bool maskPassword)
        {
            var args = new StringBuilder(designer ? "DESIGNER" : "ENTERPRISE");
            if (!string.IsNullOrEmpty(b.FilePath)) args.Append(" /F\"" + b.FilePath + "\"");
            else if (!string.IsNullOrEmpty(b.Server)) args.Append(" /S\"" + b.Server + "\\" + b.Ref + "\"");
            else args.Append(" /IBConnectionString\"" + b.Connect.Replace("\"", "\"\"") + "\"");

            string u = UserName();
            if (u.Length > 0)
            {
                string p = pass.Password.Replace("\"", "");
                if (maskPassword && p.Length > 0) p = "***";
                args.Append(" /N\"" + u + "\" /P\"" + p + "\"");
            }
            args.Append(" /DisableStartupMessages");
            // Низкая скорость соединения действует только в режиме Предприятия.
            if (!designer && string.Equals(b.Get("ClientConnectionSpeed", "Normal"), "Low", StringComparison.OrdinalIgnoreCase))
                args.Append(" /LowSpeed");
            string extra = ExtraArgs(b);
            if (extra.Length > 0) args.Append(" " + extra);
            return args.ToString();
        }

        static void OpenFolder()
        {
            var b = list.SelectedItem as InfoBase;
            if (b == null || string.IsNullOrEmpty(b.FilePath) || !Directory.Exists(b.FilePath)) return;
            Process.Start("explorer.exe", "\"" + b.FilePath + "\"");
        }

        // ---------- настройки (без паролей) ----------

        static void LoadSettings()
        {
            if (!File.Exists(SettingsFile)) return;
            foreach (string line in File.ReadAllLines(SettingsFile, Encoding.UTF8))
            {
                int i = line.IndexOf('=');
                if (i > 0) settings[line.Substring(0, i)] = line.Substring(i + 1);
            }
        }

        static void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                File.WriteAllLines(SettingsFile, settings.Select(kv => kv.Key + "=" + kv.Value), Encoding.UTF8);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static void SetWindowIcon()
        {
            try
            {
                var ico = System.Drawing.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule.FileName);
                window.Icon = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            catch (Exception) { }
        }

        // ---------- внешний вид ----------

        // Стили кнопок и полей окно берёт из главного окна (ShowBaseDialog подключает его ресурсы).
        const string DialogXaml = @"
<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
        xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
        Width='580' SizeToContent='Height' ResizeMode='NoResize' ShowInTaskbar='False'
        WindowStartupLocation='CenterOwner' Background='#16171A'
        FontFamily='Segoe UI' FontSize='14' Foreground='#E6E7EA' UseLayoutRounding='True'
        TextOptions.TextFormattingMode='Display'>
 <ScrollViewer VerticalScrollBarVisibility='Auto'>
  <StackPanel Margin='24,22,24,24'>
    <StackPanel x:Name='ModePanel' Margin='0,0,0,20'>
      <RadioButton x:Name='ModeCreate' GroupName='Mode' Content='Создать новую пустую базу' Margin='0,0,0,10' IsChecked='True'/>
      <RadioButton x:Name='ModeExisting' GroupName='Mode' Content='Добавить в список существующую базу'/>
    </StackPanel>

    <TextBlock Text='Название в списке' Style='{DynamicResource Caption}'/>
    <TextBox x:Name='DName' Margin='0,0,0,16'/>

    <StackPanel Orientation='Horizontal' Margin='0,0,0,14'>
      <RadioButton x:Name='KindFile' GroupName='Kind' Content='На этом компьютере' IsChecked='True' Margin='0,0,24,0'/>
      <RadioButton x:Name='KindServer' GroupName='Kind' Content='На сервере 1С' Margin='0,0,24,0'/>
      <RadioButton x:Name='KindWeb' GroupName='Kind' Content='На веб-сервере'/>
    </StackPanel>

    <StackPanel x:Name='FilePanel'>
      <TextBlock Text='Папка базы' Style='{DynamicResource Caption}'/>
      <Grid>
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width='*'/>
          <ColumnDefinition Width='Auto'/>
        </Grid.ColumnDefinitions>
        <TextBox x:Name='DPath'/>
        <Button x:Name='DBrowse' Grid.Column='1' Style='{DynamicResource IconBtn}' Content='&#xE8B7;'
                ToolTip='Выбрать папку' Margin='8,0,0,0'/>
      </Grid>
      <TextBlock x:Name='DPathHint' Foreground='#6E7480' FontSize='12' Margin='2,6,0,0'/>
    </StackPanel>

    <StackPanel x:Name='ServerPanel' Visibility='Collapsed'>
      <TextBlock Text='Сервер (кластер 1С)' Style='{DynamicResource Caption}'/>
      <TextBox x:Name='DServer' Margin='0,0,0,16'/>
      <TextBlock Text='Имя базы на сервере' Style='{DynamicResource Caption}'/>
      <TextBox x:Name='DRef'/>
    </StackPanel>

    <StackPanel x:Name='WebPanel' Visibility='Collapsed'>
      <TextBlock Text='Адрес публикации' Style='{DynamicResource Caption}'/>
      <TextBox x:Name='DWeb'/>
      <TextBlock Text='Например: http://сервер/имя_базы' Foreground='#6E7480' FontSize='12' Margin='2,6,0,0'/>
    </StackPanel>

    <TextBlock Text='ПАРАМЕТРЫ ЗАПУСКА' Style='{DynamicResource Section}'/>

    <TextBlock Text='Основной режим запуска' Style='{DynamicResource Caption}'/>
    <Border Background='#1D1F23' BorderBrush='#30333A' BorderThickness='1' CornerRadius='11' Margin='0,0,0,14'>
      <UniformGrid Rows='1'>
        <RadioButton x:Name='App_Auto' GroupName='App' Style='{DynamicResource Seg}' Content='Автоматически'/>
        <RadioButton x:Name='App_ThinClient' GroupName='App' Style='{DynamicResource Seg}' Content='Тонкий клиент'/>
        <RadioButton x:Name='App_ThickClient' GroupName='App' Style='{DynamicResource Seg}' Content='Толстый клиент'/>
      </UniformGrid>
    </Border>

    <TextBlock Text='Скорость соединения' Style='{DynamicResource Caption}'/>
    <Border Background='#1D1F23' BorderBrush='#30333A' BorderThickness='1' CornerRadius='11' Margin='0,0,0,14'>
      <UniformGrid Rows='1'>
        <RadioButton x:Name='Speed_Normal' GroupName='Speed' Style='{DynamicResource Seg}' Content='Обычная'/>
        <RadioButton x:Name='Speed_Low' GroupName='Speed' Style='{DynamicResource Seg}' Content='Низкая'/>
      </UniformGrid>
    </Border>

    <TextBlock Text='Разрядность платформы' Style='{DynamicResource Caption}'/>
    <Border Background='#1D1F23' BorderBrush='#30333A' BorderThickness='1' CornerRadius='11' Margin='0,0,0,14'>
      <UniformGrid Rows='1'>
        <RadioButton x:Name='Arch_Auto' GroupName='Arch' Style='{DynamicResource Seg}' Content='Любая'/>
        <RadioButton x:Name='Arch_x86' GroupName='Arch' Style='{DynamicResource Seg}' Content='32 бита (x86)'/>
        <RadioButton x:Name='Arch_x86_64' GroupName='Arch' Style='{DynamicResource Seg}' Content='64 бита (x86-64)'/>
      </UniformGrid>
    </Border>

    <TextBlock Text='Версия платформы' Style='{DynamicResource Caption}'/>
    <TextBox x:Name='DVersion'/>
    <TextBlock x:Name='DVersionHint' Foreground='#6E7480' FontSize='12' Margin='2,6,0,14' TextWrapping='Wrap'/>

    <TextBlock Text='Дополнительные параметры запуска' Style='{DynamicResource Caption}'/>
    <TextBox x:Name='DExtra'/>
    <WrapPanel Margin='0,8,0,0'>
      <ToggleButton x:Name='FlagModern' Style='{DynamicResource Chip}' Content='Современный дизайн'
                    ToolTip='/C&quot;design=modern&quot; — светлое современное оформление 1С. Нажмите ещё раз, чтобы убрать.'/>
      <ToggleButton x:Name='FlagDark' Style='{DynamicResource Chip}' Content='Дизайн с тёмной панелью'
                    ToolTip='/C&quot;design=dark&quot; — современное оформление с тёмным боковым меню. Нажмите ещё раз, чтобы убрать.'/>
      <ToggleButton x:Name='FlagTech' Style='{DynamicResource Chip}' Content='Режим техн. специалиста'
                    ToolTip='/TechnicalSpecialistMode — в меню 1С появляются «Функции для технического специалиста».'/>
      <ToggleButton x:Name='FlagDebug' Style='{DynamicResource Chip}' Content='Отладка'
                    ToolTip='/Debug — запуск с разрешённой отладкой из Конфигуратора.'/>
    </WrapPanel>
    <TextBlock Foreground='#6E7480' FontSize='12' Margin='2,2,0,0' TextWrapping='Wrap'
               Text='Нажмите на подсказку, чтобы добавить флаг в поле, и ещё раз, чтобы убрать. Параметры добавляются в конец команды запуска. Дизайн включается в базах, где есть общий модуль «СовременныйДизайн»; остальные базы этот флаг просто не заметят.'/>

    <StackPanel x:Name='CommandPanel' Margin='0,20,0,0'>
      <TextBlock Text='КОМАНДА СОЗДАНИЯ' Foreground='#6E7480' FontSize='11' FontWeight='SemiBold' Margin='0,0,0,8'/>
      <Border Background='#141518' CornerRadius='8' Padding='10,8'>
        <TextBox x:Name='DCommand' IsReadOnly='True' TextWrapping='Wrap' Height='Auto' Padding='0'
                 FontFamily='Cascadia Mono, Consolas' FontSize='11' Background='Transparent' BorderThickness='0'
                 Foreground='#B4B9C2'/>
      </Border>
    </StackPanel>

    <TextBlock x:Name='DError' Foreground='#F27C7C' FontSize='13' Margin='2,14,0,0' TextWrapping='Wrap'/>

    <StackPanel Orientation='Horizontal' HorizontalAlignment='Right' Margin='0,16,0,0'>
      <Button x:Name='DCancel' Content='Отмена' Width='120' Margin='0,0,8,0' IsCancel='True'/>
      <Button x:Name='DOk' Content='Добавить' Width='140' IsDefault='True' Style='{DynamicResource Primary}' Height='40'/>
    </StackPanel>
  </StackPanel>
 </ScrollViewer>
</Window>";

        const string Xaml = @"
<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
        xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
        Title='Запуск 1С' Width='1060' Height='760' MinWidth='880' MinHeight='560'
        WindowStartupLocation='CenterScreen' Background='#16171A'
        FontFamily='Segoe UI' FontSize='14' Foreground='#E6E7EA' UseLayoutRounding='True'
        TextOptions.TextFormattingMode='Display'>
  <Window.Resources>
    <FontFamily x:Key='Icons'>Segoe Fluent Icons, Segoe MDL2 Assets</FontFamily>
    <LinearGradientBrush x:Key='AccentGradient' StartPoint='0,0' EndPoint='1,1'>
      <GradientStop Color='#3B82F6' Offset='0'/>
      <GradientStop Color='#7C3AED' Offset='1'/>
    </LinearGradientBrush>

    <!-- Базовая кнопка: высота 40, скругление 10 -->
    <Style TargetType='Button'>
      <Setter Property='Foreground' Value='#E6E7EA'/>
      <Setter Property='Background' Value='#24262B'/>
      <Setter Property='BorderBrush' Value='#30333A'/>
      <Setter Property='Height' Value='40'/>
      <Setter Property='Padding' Value='14,0'/>
      <Setter Property='FontSize' Value='13'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Focusable' Value='False'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='Button'>
            <Border x:Name='Bd' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}'
                    BorderThickness='1' CornerRadius='10' Padding='{TemplateBinding Padding}'>
              <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Opacity' Value='0.86'/></Trigger>
              <Trigger Property='IsPressed' Value='True'><Setter TargetName='Bd' Property='Opacity' Value='0.72'/></Trigger>
              <Trigger Property='IsEnabled' Value='False'><Setter TargetName='Bd' Property='Opacity' Value='0.38'/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <Style x:Key='Primary' TargetType='Button' BasedOn='{StaticResource {x:Type Button}}'>
      <Setter Property='Background' Value='{StaticResource AccentGradient}'/>
      <Setter Property='BorderBrush' Value='Transparent'/>
      <Setter Property='Foreground' Value='White'/>
      <Setter Property='FontSize' Value='14'/>
      <Setter Property='FontWeight' Value='SemiBold'/>
      <Setter Property='Height' Value='48'/>
    </Style>
    <Style x:Key='Big' TargetType='Button' BasedOn='{StaticResource {x:Type Button}}'>
      <Setter Property='FontSize' Value='14'/>
      <Setter Property='Height' Value='48'/>
    </Style>
    <Style x:Key='IconBtn' TargetType='Button' BasedOn='{StaticResource {x:Type Button}}'>
      <Setter Property='Width' Value='40'/>
      <Setter Property='Padding' Value='0'/>
      <Setter Property='FontFamily' Value='{StaticResource Icons}'/>
      <Setter Property='FontSize' Value='15'/>
    </Style>
    <Style x:Key='SmallIconBtn' TargetType='Button' BasedOn='{StaticResource IconBtn}'>
      <Setter Property='Width' Value='32'/>
      <Setter Property='Height' Value='32'/>
      <Setter Property='FontSize' Value='13'/>
      <Setter Property='Background' Value='Transparent'/>
      <Setter Property='BorderBrush' Value='Transparent'/>
      <Setter Property='Foreground' Value='#9AA0AA'/>
    </Style>

    <Style x:Key='BadgeClose' TargetType='Button'>
      <Setter Property='Width' Value='18'/>
      <Setter Property='Height' Value='18'/>
      <Setter Property='Margin' Value='5,0,0,0'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Focusable' Value='False'/>
      <Setter Property='VerticalAlignment' Value='Center'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='Button'>
            <Border x:Name='Bd' Background='Transparent' CornerRadius='9'>
              <Border.RenderTransform><TranslateTransform Y='1'/></Border.RenderTransform>
              <Path Data='M0,0 L7,7 M7,0 L0,7' Stroke='{TemplateBinding Foreground}' StrokeThickness='1.5'
                    HorizontalAlignment='Center' VerticalAlignment='Center'/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Background' Value='#33FFFFFF'/></Trigger>
              <Trigger Property='IsPressed' Value='True'><Setter TargetName='Bd' Property='Background' Value='#55FFFFFF'/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style x:Key='Field' TargetType='Control'>
      <Setter Property='Background' Value='#1D1F23'/>
      <Setter Property='Foreground' Value='#E6E7EA'/>
      <Setter Property='BorderBrush' Value='#30333A'/>
      <Setter Property='BorderThickness' Value='1'/>
      <Setter Property='Height' Value='40'/>
      <Setter Property='Padding' Value='10,0'/>
      <Setter Property='VerticalContentAlignment' Value='Center'/>
    </Style>
    <Style TargetType='TextBox' BasedOn='{StaticResource Field}'>
      <Setter Property='CaretBrush' Value='#E6E7EA'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='TextBox'>
            <Border x:Name='Bd' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}'
                    BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='10'>
              <ScrollViewer x:Name='PART_ContentHost' Margin='{TemplateBinding Padding}' VerticalAlignment='{TemplateBinding VerticalContentAlignment}'/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Bd' Property='BorderBrush' Value='#3B82F6'/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <Style TargetType='PasswordBox' BasedOn='{StaticResource Field}'>
      <Setter Property='CaretBrush' Value='#E6E7EA'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='PasswordBox'>
            <Border x:Name='Bd' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}'
                    BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='10'>
              <ScrollViewer x:Name='PART_ContentHost' Margin='{TemplateBinding Padding}' VerticalAlignment='Center'/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Bd' Property='BorderBrush' Value='#3B82F6'/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style TargetType='CheckBox'>
      <Setter Property='Foreground' Value='#B4B9C2'/>
      <Setter Property='FontSize' Value='13'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Focusable' Value='False'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='CheckBox'>
            <StackPanel Orientation='Horizontal' Background='Transparent'>
              <Border x:Name='Box' Width='18' Height='18' CornerRadius='5' BorderThickness='1.5'
                      BorderBrush='#4A4E57' Background='#1D1F23' VerticalAlignment='Center'>
                <Path x:Name='Mark' Data='M3.2,8 L6.5,11.2 L12.3,4.6' Stroke='White' StrokeThickness='2'
                      StrokeStartLineCap='Round' StrokeEndLineCap='Round' Visibility='Collapsed'/>
              </Border>
              <ContentPresenter Margin='10,0,0,0' VerticalAlignment='Center'/>
            </StackPanel>
            <ControlTemplate.Triggers>
              <Trigger Property='IsChecked' Value='True'>
                <Setter TargetName='Box' Property='Background' Value='#3B82F6'/>
                <Setter TargetName='Box' Property='BorderBrush' Value='#3B82F6'/>
                <Setter TargetName='Mark' Property='Visibility' Value='Visible'/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <Style TargetType='RadioButton'>
      <Setter Property='Foreground' Value='#E6E7EA'/>
      <Setter Property='FontSize' Value='13'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='RadioButton'>
            <StackPanel Orientation='Horizontal' Background='Transparent'>
              <Grid Width='18' Height='18' VerticalAlignment='Center'>
                <Ellipse x:Name='Ring' Stroke='#4A4E57' StrokeThickness='1.5' Fill='#1D1F23'/>
                <Ellipse x:Name='Dot' Width='8' Height='8' Fill='White' Visibility='Collapsed'/>
              </Grid>
              <ContentPresenter Margin='10,0,0,0' VerticalAlignment='Center'/>
            </StackPanel>
            <ControlTemplate.Triggers>
              <Trigger Property='IsChecked' Value='True'>
                <Setter TargetName='Ring' Property='Fill' Value='#3B82F6'/>
                <Setter TargetName='Ring' Property='Stroke' Value='#3B82F6'/>
                <Setter TargetName='Dot' Property='Visibility' Value='Visible'/>
              </Trigger>
              <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <!-- Переключатель-сегмент для окна настроек -->
    <Style x:Key='Seg' TargetType='RadioButton'>
      <Setter Property='Foreground' Value='#B4B9C2'/>
      <Setter Property='FontSize' Value='13'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Focusable' Value='False'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='RadioButton'>
            <Border x:Name='Bd' Background='Transparent' CornerRadius='8' Height='32' Margin='3'>
              <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Background' Value='#24262B'/></Trigger>
              <Trigger Property='IsChecked' Value='True'>
                <Setter TargetName='Bd' Property='Background' Value='#3B82F6'/>
                <Setter Property='Foreground' Value='White'/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <!-- Ссылка-подсказка: по клику добавляет флаг в поле параметров; подсвечена, пока флаг есть в поле -->
    <Style x:Key='Chip' TargetType='ToggleButton'>
      <Setter Property='Foreground' Value='#3B82F6'/>
      <Setter Property='FontSize' Value='12'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Focusable' Value='False'/>
      <Setter Property='Margin' Value='0,0,6,6'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='ToggleButton'>
            <Border x:Name='Bd' Background='Transparent' BorderBrush='#30333A' BorderThickness='1' CornerRadius='13'
                    Height='26' Padding='11,0'>
              <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Background' Value='#24262B'/></Trigger>
              <Trigger Property='IsChecked' Value='True'>
                <Setter TargetName='Bd' Property='Background' Value='#3B82F6'/>
                <Setter TargetName='Bd' Property='BorderBrush' Value='#3B82F6'/>
                <Setter Property='Foreground' Value='White'/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style TargetType='ScrollBar'>
      <Setter Property='Width' Value='6'/>
      <Setter Property='MinWidth' Value='6'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='ScrollBar'>
            <Track x:Name='PART_Track' IsDirectionReversed='True'>
              <Track.Thumb>
                <Thumb>
                  <Thumb.Template>
                    <ControlTemplate TargetType='Thumb'><Border Background='#3A3D44' CornerRadius='3'/></ControlTemplate>
                  </Thumb.Template>
                </Thumb>
              </Track.Thumb>
            </Track>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <!-- Список без стандартной (белой в неактивном состоянии) рамки -->
    <Style TargetType='ListBox'>
      <Setter Property='Background' Value='Transparent'/>
      <Setter Property='BorderThickness' Value='0'/>
      <Setter Property='ScrollViewer.HorizontalScrollBarVisibility' Value='Disabled'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='ListBox'>
            <ScrollViewer Focusable='False' Padding='0,0,6,0'><ItemsPresenter/></ScrollViewer>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <Style TargetType='ListBoxItem'>
      <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='ListBoxItem'>
            <Border x:Name='Bd' Background='Transparent' CornerRadius='10' Padding='14,10' Margin='0,0,0,6'
                    BorderBrush='{Binding AccentBrush}' BorderThickness='{Binding AccentThickness}'>
              <ContentPresenter/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Background' Value='#1E2025'/></Trigger>
              <Trigger Property='IsSelected' Value='True'><Setter TargetName='Bd' Property='Background' Value='#22304C'/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <!-- Метки запущенной 1С (используются в списке и в панели базы) -->
    <DataTemplate x:Key='Badges'>
      <StackPanel Orientation='Horizontal' VerticalAlignment='Center'>
        <Border Background='#3A2E1C' CornerRadius='11' Padding='9,2,4,2' Margin='0,0,8,0' Visibility='{Binding DesignerVisibility}'>
          <StackPanel Orientation='Horizontal'>
            <Ellipse Width='7' Height='7' Fill='#F2A03C' VerticalAlignment='Center' Margin='0,1,6,0'/>
            <TextBlock Text='{Binding DesignerText}' Foreground='#F2A03C' FontSize='11' FontWeight='SemiBold' VerticalAlignment='Center'/>
            <Button Style='{StaticResource BadgeClose}' Tag='D' Foreground='#F2A03C'
                    ToolTip='Закрыть Конфигуратор этой базы (с Shift — снять процесс без вопросов)'/>
          </StackPanel>
        </Border>
        <Border Background='#1C3524' CornerRadius='11' Padding='9,2,4,2' Margin='0,0,8,0' Visibility='{Binding EnterpriseVisibility}'>
          <StackPanel Orientation='Horizontal'>
            <Ellipse Width='7' Height='7' Fill='#3FB950' VerticalAlignment='Center' Margin='0,1,6,0'/>
            <TextBlock Text='{Binding EnterpriseText}' Foreground='#3FB950' FontSize='11' FontWeight='SemiBold' VerticalAlignment='Center'/>
            <Button Style='{StaticResource BadgeClose}' Tag='E' Foreground='#3FB950'
                    ToolTip='Закрыть Предприятие этой базы (с Shift — снять процесс без вопросов)'/>
          </StackPanel>
        </Border>
      </StackPanel>
    </DataTemplate>

    <Style x:Key='Caption' TargetType='TextBlock'>
      <Setter Property='Foreground' Value='#8A909B'/>
      <Setter Property='FontSize' Value='12'/>
      <Setter Property='Margin' Value='2,0,0,6'/>
    </Style>
    <Style x:Key='Section' TargetType='TextBlock'>
      <Setter Property='Foreground' Value='#6E7480'/>
      <Setter Property='FontSize' Value='11'/>
      <Setter Property='FontWeight' Value='SemiBold'/>
      <Setter Property='Margin' Value='0,20,0,10'/>
    </Style>
  </Window.Resources>

  <Grid>
    <Grid.RowDefinitions>
      <RowDefinition Height='Auto'/>
      <RowDefinition Height='*'/>
      <RowDefinition Height='Auto'/>
    </Grid.RowDefinitions>

    <!-- Верхняя панель -->
    <Grid Margin='24,20,24,16'>
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width='*'/>
        <ColumnDefinition Width='Auto'/>
      </Grid.ColumnDefinitions>
      <StackPanel Orientation='Horizontal' VerticalAlignment='Center'>
        <Border Width='32' Height='32' CornerRadius='9' Background='{StaticResource AccentGradient}' Margin='0,0,12,0'>
          <TextBlock Text='&#xE768;' FontFamily='{StaticResource Icons}' FontSize='14' Foreground='White'
                     HorizontalAlignment='Center' VerticalAlignment='Center' Margin='2,0,0,0'/>
        </Border>
        <TextBlock Text='Запуск 1С' FontSize='20' FontWeight='SemiBold' VerticalAlignment='Center'/>
        <Border Background='#24262B' CornerRadius='10' Padding='9,2' Margin='12,2,0,0' VerticalAlignment='Center'>
          <TextBlock x:Name='Header' Text='0' FontSize='12' Foreground='#9AA0AA'/>
        </Border>
      </StackPanel>
      <StackPanel Grid.Column='1' Orientation='Horizontal'>
        <Button x:Name='BtnSettings' Style='{StaticResource IconBtn}' Content='&#xE9E9;' ToolTip='Настройки лаунчера' Margin='0,0,8,0'/>
        <Button x:Name='BtnReload' Style='{StaticResource IconBtn}' Content='&#xE72C;' ToolTip='Обновить список баз' Margin='0,0,8,0'/>
        <Button x:Name='BtnAdd' Margin='0,0,8,0' ToolTip='Создать новую базу или добавить существующую в список'>
          <StackPanel Orientation='Horizontal'>
            <TextBlock Text='&#xE710;' FontFamily='{StaticResource Icons}' FontSize='13' VerticalAlignment='Center' Margin='0,0,8,0'/>
            <TextBlock Text='Новая база' VerticalAlignment='Center'/>
          </StackPanel>
        </Button>
        <Button x:Name='BtnCloseAll' IsEnabled='False'
                ToolTip='Закрыть все окна Предприятия и Конфигуратора (с Shift — снять процессы без вопросов)'>
          <StackPanel Orientation='Horizontal'>
            <TextBlock Text='&#xE711;' FontFamily='{StaticResource Icons}' FontSize='12' VerticalAlignment='Center' Margin='0,0,8,0'/>
            <TextBlock x:Name='CloseAllText' Text='Закрыть все 1С' VerticalAlignment='Center'/>
          </StackPanel>
        </Button>
      </StackPanel>
    </Grid>

    <!-- Основная часть: список слева, выбранная база справа -->
    <Grid Grid.Row='1' Margin='24,0,24,0'>
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width='*' MinWidth='320'/>
        <ColumnDefinition Width='24'/>
        <ColumnDefinition Width='400'/>
      </Grid.ColumnDefinitions>

      <DockPanel>
        <Grid DockPanel.Dock='Top' Margin='0,0,0,12'>
          <TextBox x:Name='Search' Padding='36,0,10,0'/>
          <TextBlock Text='&#xE721;' FontFamily='{StaticResource Icons}' FontSize='13' Foreground='#6E7480'
                     Margin='14,0,0,0' VerticalAlignment='Center' IsHitTestVisible='False'/>
          <TextBlock x:Name='SearchHint' Text='Поиск по названию или пути' Foreground='#5F6570'
                     Margin='37,0,0,0' VerticalAlignment='Center' IsHitTestVisible='False' FontSize='13'/>
        </Grid>
        <ListBox x:Name='List'>
          <ListBox.ItemTemplate>
            <DataTemplate>
              <Grid>
                <Grid.ColumnDefinitions>
                  <ColumnDefinition Width='*'/>
                  <ColumnDefinition Width='Auto'/>
                </Grid.ColumnDefinitions>
                <StackPanel>
                  <TextBlock Text='{Binding Name}' FontSize='15' Foreground='#E6E7EA' TextTrimming='CharacterEllipsis'/>
                  <TextBlock Text='{Binding Sub}' FontSize='12' Foreground='#7C828D' TextTrimming='CharacterEllipsis' Margin='0,2,0,0'/>
                </StackPanel>
                <ContentPresenter Grid.Column='1' Content='{Binding}' ContentTemplate='{StaticResource Badges}'
                                  Margin='8,0,-8,0' VerticalAlignment='Center'/>
              </Grid>
            </DataTemplate>
          </ListBox.ItemTemplate>
        </ListBox>
      </DockPanel>

      <!-- Панель выбранной базы -->
      <Border Grid.Column='2' Background='#1B1C20' BorderBrush='#26282D' BorderThickness='1' CornerRadius='14'>
        <Grid>
          <TextBlock x:Name='EmptyHint' Text='Выберите базу в списке' Foreground='#6E7480'
                     HorizontalAlignment='Center' VerticalAlignment='Center' Visibility='Collapsed'/>
          <ScrollViewer x:Name='Details' VerticalScrollBarVisibility='Auto' Padding='0,0,4,0'>
            <StackPanel Margin='24,22,20,24'>
              <TextBlock Text='{Binding Name}' FontSize='22' FontWeight='SemiBold' TextWrapping='Wrap'/>
              <TextBlock Text='{Binding Sub}' FontSize='12' Foreground='#7C828D' TextWrapping='Wrap' Margin='0,4,0,0'/>
              <ContentPresenter Content='{Binding}' ContentTemplate='{StaticResource Badges}' Margin='0,12,0,0'
                                HorizontalAlignment='Left'/>

              <Button x:Name='BtnEnterprise' Style='{StaticResource Primary}' Margin='0,20,0,8'>
                <StackPanel Orientation='Horizontal'>
                  <TextBlock Text='&#xE768;' FontFamily='{StaticResource Icons}' FontSize='14' VerticalAlignment='Center' Margin='0,0,10,0'/>
                  <TextBlock Text='1С:Предприятие' VerticalAlignment='Center'/>
                </StackPanel>
              </Button>
              <Button x:Name='BtnDesigner' Style='{StaticResource Big}'>
                <StackPanel Orientation='Horizontal'>
                  <TextBlock Text='&#xE713;' FontFamily='{StaticResource Icons}' FontSize='14' VerticalAlignment='Center' Margin='0,0,10,0'/>
                  <TextBlock Text='Конфигуратор' VerticalAlignment='Center'/>
                </StackPanel>
              </Button>
              <TextBlock Text='Enter — Предприятие  ·  Ctrl+Enter — Конфигуратор' FontSize='11' Foreground='#5F6570'
                         HorizontalAlignment='Center' Margin='0,8,0,0'/>

              <TextBlock Text='ВХОД' Style='{StaticResource Section}'/>
              <Grid>
                <Grid.ColumnDefinitions>
                  <ColumnDefinition Width='*'/>
                  <ColumnDefinition Width='12'/>
                  <ColumnDefinition Width='*'/>
                </Grid.ColumnDefinitions>
                <StackPanel>
                  <TextBlock Text='Пользователь' Style='{StaticResource Caption}'/>
                  <TextBox x:Name='User'/>
                </StackPanel>
                <StackPanel Grid.Column='2'>
                  <TextBlock Text='Пароль (не сохраняется)' Style='{StaticResource Caption}'/>
                  <PasswordBox x:Name='Pass'/>
                </StackPanel>
              </Grid>
              <CheckBox x:Name='CloseAfter' Content='Закрывать лаунчер после запуска' Margin='2,14,0,0'/>

              <TextBlock Text='БАЗА' Style='{StaticResource Section}'/>
              <UniformGrid Rows='1'>
                <Button x:Name='BtnFolder' Padding='6,0' Margin='0,0,4,0' ToolTip='Открыть папку базы в Проводнике'>
                  <StackPanel Orientation='Horizontal'>
                    <TextBlock Text='&#xE8B7;' FontFamily='{StaticResource Icons}' FontSize='13' VerticalAlignment='Center' Margin='0,0,8,0'/>
                    <TextBlock Text='Папка' VerticalAlignment='Center'/>
                  </StackPanel>
                </Button>
                <Button x:Name='BtnEdit' Padding='6,0' Margin='4,0,4,0' ToolTip='Изменить название или путь'>
                  <StackPanel Orientation='Horizontal'>
                    <TextBlock Text='&#xE70F;' FontFamily='{StaticResource Icons}' FontSize='13' VerticalAlignment='Center' Margin='0,0,8,0'/>
                    <TextBlock Text='Изменить' VerticalAlignment='Center'/>
                  </StackPanel>
                </Button>
                <Button x:Name='BtnDelete' Padding='6,0' Margin='4,0,0,0' ToolTip='Удалить из списка (файлы базы останутся на диске)'>
                  <StackPanel Orientation='Horizontal'>
                    <TextBlock Text='&#xE74D;' FontFamily='{StaticResource Icons}' FontSize='13' VerticalAlignment='Center' Margin='0,0,8,0'/>
                    <TextBlock Text='Удалить' VerticalAlignment='Center'/>
                  </StackPanel>
                </Button>
              </UniformGrid>

              <StackPanel x:Name='CommandsPanel'>
              <TextBlock Text='КОМАНДЫ ЗАПУСКА' Style='{StaticResource Section}'/>
              <TextBlock Text='Предприятие' Style='{StaticResource Caption}'/>
              <Grid Margin='0,0,0,12'>
                <Grid.ColumnDefinitions>
                  <ColumnDefinition Width='*'/>
                  <ColumnDefinition Width='Auto'/>
                </Grid.ColumnDefinitions>
                <Border Background='#141518' CornerRadius='8' Padding='10,8'>
                  <TextBox x:Name='CmdEnterprise' IsReadOnly='True' TextWrapping='Wrap' Height='Auto' Padding='0'
                           FontFamily='Cascadia Mono, Consolas' FontSize='11' Background='Transparent' BorderThickness='0'
                           Foreground='#B4B9C2'/>
                </Border>
                <Button x:Name='BtnCopyEnterprise' Grid.Column='1' Style='{StaticResource SmallIconBtn}' Content='&#xE8C8;'
                        ToolTip='Копировать команду' Margin='6,0,0,0' VerticalAlignment='Top'/>
              </Grid>
              <TextBlock Text='Конфигуратор' Style='{StaticResource Caption}'/>
              <Grid>
                <Grid.ColumnDefinitions>
                  <ColumnDefinition Width='*'/>
                  <ColumnDefinition Width='Auto'/>
                </Grid.ColumnDefinitions>
                <Border Background='#141518' CornerRadius='8' Padding='10,8'>
                  <TextBox x:Name='CmdDesigner' IsReadOnly='True' TextWrapping='Wrap' Height='Auto' Padding='0'
                           FontFamily='Cascadia Mono, Consolas' FontSize='11' Background='Transparent' BorderThickness='0'
                           Foreground='#B4B9C2'/>
                </Border>
                <Button x:Name='BtnCopyDesigner' Grid.Column='1' Style='{StaticResource SmallIconBtn}' Content='&#xE8C8;'
                        ToolTip='Копировать команду' Margin='6,0,0,0' VerticalAlignment='Top'/>
              </Grid>
              </StackPanel>
            </StackPanel>
          </ScrollViewer>
        </Grid>
      </Border>
    </Grid>

    <!-- Строка состояния -->
    <TextBlock x:Name='Status' Grid.Row='2' Margin='26,12,24,14' Foreground='#6E7480' FontSize='12'
               TextTrimming='CharacterEllipsis'/>

    <!-- Затемнение на время создания базы -->
    <Grid x:Name='Busy' Grid.RowSpan='3' Background='#B0101114' Visibility='Collapsed'>
      <Border Background='#1E2025' BorderBrush='#30333A' BorderThickness='1' CornerRadius='14' Padding='28,22'
              HorizontalAlignment='Center' VerticalAlignment='Center' MinWidth='320'>
        <StackPanel>
          <TextBlock x:Name='BusyText' Text='Создаю базу…' FontSize='15' FontWeight='SemiBold'/>
          <TextBlock Text='1С создаёт файлы базы, это занимает около 30 секунд.' Foreground='#8A909B' FontSize='12' Margin='0,6,0,14'/>
          <ProgressBar IsIndeterminate='True' Height='4' Foreground='#3B82F6' Background='#2A2D33' BorderThickness='0'/>
        </StackPanel>
      </Border>
    </Grid>
  </Grid>
</Window>";
    }
}
