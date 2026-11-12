// Установщик 1C Launcher. Ставит программу в профиль пользователя (права администратора не нужны):
//   %LOCALAPPDATA%\Programs\1C Launcher\1C-Launcher.exe
// создаёт ярлыки и запись в «Установленные приложения». Сам лаунчер вшит в этот файл как ресурс.
//   Setup.exe            — обычная установка с окном
//   Setup.exe /silent    — установка без вопросов
//   Setup.exe /uninstall — удаление (так его вызывает Windows; добавьте /silent, чтобы без вопросов)
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("1C Launcher Setup")]
[assembly: AssemblyProduct("1C Launcher")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

static class Setup
{
    const string AppName = "1C Launcher";
    const string ExeName = "1C-Launcher.exe";
    const string Version = "1.0.0";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\1C Launcher";

    static readonly string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
    static readonly string StartMenuLink = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
    static readonly string DesktopLink = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    [STAThread]
    static int Main(string[] args)
    {
        bool silent = Array.Exists(args, a => a.Equals("/silent", StringComparison.OrdinalIgnoreCase));
        bool uninstall = Array.Exists(args, a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase));
        Application.EnableVisualStyles();
        try
        {
            return uninstall ? Uninstall(silent) : Install(silent);
        }
        catch (Exception ex)
        {
            if (!silent) MessageBox.Show("Не получилось: " + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    static int Install(bool silent)
    {
        bool desktop = true, run = !silent;
        if (!silent && !AskInstall(ref desktop, ref run)) return 2;

        CloseRunning();
        Directory.CreateDirectory(InstallDir);
        string exe = Path.Combine(InstallDir, ExeName);
        using (Stream src = Assembly.GetExecutingAssembly().GetManifestResourceStream(ExeName))
        using (FileStream dst = File.Create(exe))
        {
            src.CopyTo(dst);
        }
        string uninstaller = Path.Combine(InstallDir, "uninstall.exe");
        File.Copy(Application.ExecutablePath, uninstaller, true);

        Shortcut(StartMenuLink, exe);
        if (desktop) Shortcut(DesktopLink, exe);

        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", Version);
            key.SetValue("Publisher", "1C Launcher");
            key.SetValue("DisplayIcon", exe);
            key.SetValue("InstallLocation", InstallDir);
            key.SetValue("UninstallString", "\"" + uninstaller + "\" /uninstall");
            key.SetValue("QuietUninstallString", "\"" + uninstaller + "\" /uninstall /silent");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024 + 200), RegistryValueKind.DWord);
        }

        if (run) Process.Start(exe);
        return 0;
    }

    static int Uninstall(bool silent)
    {
        if (!silent && MessageBox.Show("Удалить " + AppName + "?\n\nСписок баз 1С и сами базы не затрагиваются.",
                AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 2;

        CloseRunning();
        foreach (string link in new[] { StartMenuLink, DesktopLink })
        {
            if (File.Exists(link)) File.Delete(link);
        }
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);

        // Настройки лаунчера (тема, размеры, имена пользователей; паролей там нет) остаются,
        // если их не попросили удалить: в окне — отдельным вопросом, в тихом режиме — ключом /purge.
        string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OneCLauncher");
        bool purge = silent
            ? Array.Exists(Environment.GetCommandLineArgs(), a => a.Equals("/purge", StringComparison.OrdinalIgnoreCase))
            : Directory.Exists(settings) && MessageBox.Show("Удалить также настройки лаунчера (тема, размеры)?",
                  AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        if (purge && Directory.Exists(settings)) Directory.Delete(settings, true);

        // Свою папку работающий файл удалить не может: это делает cmd после нашего выхода.
        if (Directory.Exists(InstallDir))
        {
            Process.Start(new ProcessStartInfo("cmd.exe",
                "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + InstallDir + "\"")
            {
                CreateNoWindow = true, UseShellExecute = false,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            });
        }
        if (!silent) MessageBox.Show(AppName + " удалён.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    static bool AskInstall(ref bool desktop, ref bool run)
    {
        using (var form = new Form())
        {
            form.Text = "Установка " + AppName;
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.MaximizeBox = form.MinimizeBox = false;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.Font = new System.Drawing.Font("Segoe UI", 9.5f);
            form.ClientSize = new System.Drawing.Size(460, 250);
            form.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            var title = new Label { Text = AppName + " " + Version, Left = 20, Top = 18, AutoSize = true,
                                    Font = new System.Drawing.Font("Segoe UI", 14f, System.Drawing.FontStyle.Bold) };
            var text = new Label { Left = 20, Top = 58, Width = 420, Height = 70,
                Text = "Быстрый запуск баз 1С:Предприятие.\n\nПрограмма будет установлена для текущего пользователя в папку:\n" + InstallDir };
            var cbDesktop = new CheckBox { Text = "Создать ярлык на рабочем столе", Left = 22, Top = 136, Width = 400, Checked = true };
            var cbRun = new CheckBox { Text = "Запустить после установки", Left = 22, Top = 162, Width = 400, Checked = true };
            var ok = new Button { Text = "Установить", Left = 230, Top = 204, Width = 110, Height = 30, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Отмена", Left = 348, Top = 204, Width = 92, Height = 30, DialogResult = DialogResult.Cancel };
            form.Controls.AddRange(new Control[] { title, text, cbDesktop, cbRun, ok, cancel });
            form.AcceptButton = ok;
            form.CancelButton = cancel;

            if (form.ShowDialog() != DialogResult.OK) return false;
            desktop = cbDesktop.Checked;
            run = cbRun.Checked;
            return true;
        }
    }

    // Закрывает запущенный лаунчер из папки установки, иначе его файл нельзя заменить или удалить.
    static void CloseRunning()
    {
        foreach (Process p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
        {
            try
            {
                if (!p.MainModule.FileName.StartsWith(InstallDir, StringComparison.OrdinalIgnoreCase)) continue;
                p.CloseMainWindow();
                if (!p.WaitForExit(3000)) p.Kill();
                p.WaitForExit(3000);
            }
            catch (Exception) { }
        }
    }

    static void Shortcut(string link, string target)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(shellType);
        object s = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
        Type t = s.GetType();
        t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, s, new object[] { target });
        t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, s, new object[] { Path.GetDirectoryName(target) });
        t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, s, new object[] { target + ",0" });
        t.InvokeMember("Description", BindingFlags.SetProperty, null, s, new object[] { "Быстрый запуск баз 1С" });
        t.InvokeMember("Save", BindingFlags.InvokeMethod, null, s, null);
    }
}
