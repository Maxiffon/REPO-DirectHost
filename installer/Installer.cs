// REPO DirectHost standalone installer: puts BepInEx 5 and the DirectHost plugin straight into the R.E.P.O. game
// folder, for players who do not use a mod manager. Everything it adds is listed in BepInEx\directhost-installer.txt,
// so "remove" takes back exactly that and restores whatever it replaced or disabled.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DirectHostInstaller
{
    internal static class Program
    {
        private const string AppId = "3241660"; // R.E.P.O. on Steam
        private const string PluginRel = @"BepInEx\plugins\REPO_DirectHost";
        private const string PluginsRel = @"BepInEx\plugins";
        private const string MarkerRel = @"BepInEx\directhost-installer.txt";
        private const string DisabledRel = @"BepInEx\disabled-by-directhost";
        private const string ConfigRel = @"BepInEx\config\kot.repo.directhost.cfg";
        private const string Backup = ".bak-directhost";

        // Plugins that replace Photon networking too (DirectHost refuses to load next to them), found by BepInEx GUID.
        private static readonly string[] ConflictIds = { "local.repoversteam.transport", "Self_Hosted_Photon", "kot.repo.p2pfix" };
        private static readonly string[] ConflictNames = { "RepoSteamTransport.dll", "REPOP2PFix.dll" };

        private static readonly string Ver = FormatVersion(typeof(Program).Assembly.GetName().Version);
        private static bool _auto;   // --yes: never ask, take the default answer (scripts and tests)
        private static bool _noPause; // the window is handed over to an elevated copy

        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            string game = null, action = null;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--game": game = i + 1 < args.Length ? args[++i] : ""; break;
                    case "--install": case "--uninstall": case "--remove-all": case "--detect": action = args[i].Substring(2); break;
                    case "--yes": _auto = true; break;
                    default:
                        Console.WriteLine("Неизвестный параметр: " + args[i]);
                        Console.WriteLine("Параметры: [--game <папка игры>] [--install | --uninstall | --remove-all | --detect] [--yes]");
                        return 2;
                }
            }

            int rc;
            try { rc = Run(game, action); }
            catch (Exception e)
            {
                Error(e is IOException || e is UnauthorizedAccessException
                    ? "не удалось записать файл: " + e.Message + "\nЕсли игра или r2modman открыты — закрой их и запусти установщик ещё раз."
                    : e.ToString());
                rc = 1;
            }
            if (!_auto && !_noPause)
            {
                Console.WriteLine();
                Console.Write("Нажми Enter, чтобы закрыть окно...");
                Console.ReadLine();
            }
            return rc;
        }

        private static int Run(string game, string action)
        {
            Console.WriteLine($"REPO DirectHost {Ver} — установка без r2modman");
            Console.WriteLine(new string('=', 46));

            if (game != null)
            {
                game = game.Trim().Trim('"');
                if (game.Length == 0) { Error("после --game нужна папка игры."); return 2; }
                game = Normalize(game);
                if (!IsGameDir(game)) { Error($"в папке «{game}» нет REPO.exe."); return 2; }
            }
            else game = FindGame();

            if (action == "detect") { Console.WriteLine(game ?? "R.E.P.O. не найдена"); return game == null ? 3 : 0; }
            if (game == null)
            {
                if (_auto) { Error("R.E.P.O. не найдена — укажи папку: --game <папка игры>."); return 3; }
                game = AskGameDir();
                if (game == null) return 3;
            }

            while (true)
            {
                var m = Marker.Load(game);
                Console.WriteLine();
                PrintStatus(game, m);
                if (action != null) return Do(action, game);

                Console.WriteLine();
                Console.WriteLine($"  1 — установить DirectHost {Ver} (или обновить)");
                Console.WriteLine("  2 — удалить DirectHost");
                if (m.OwnsBepInEx) Console.WriteLine("  3 — удалить DirectHost и BepInEx (вернуть игру как было)");
                Console.WriteLine("  4 — выбрать другую папку игры");
                Console.WriteLine("  0 — выход");
                Console.Write("Введи цифру и нажми Enter: ");
                switch ((Console.ReadLine() ?? "0").Trim())
                {
                    case "1": return Do("install", game);
                    case "2": return Do("uninstall", game);
                    case "3" when m.OwnsBepInEx: return Do("remove-all", game);
                    case "4":
                        var other = AskGameDir();
                        if (other != null) game = other;
                        break;
                    case "0": case "": return 0;
                    default: Console.WriteLine("Нет такого пункта."); break;
                }
            }
        }

        private static int Do(string action, string game)
        {
            Console.WriteLine();
            if (!EnsureGameClosed() || !EnsureWritable(game)) return 1;
            switch (action)
            {
                case "install": return Install(game);
                case "uninstall": return Uninstall(game, false);
                case "remove-all": return Uninstall(game, true);
                default: return 2;
            }
        }

        // ---------------------------------------------------------------- install

        private static int Install(string game)
        {
            var m = Marker.Load(game);
            string plugins = Path.Combine(game, PluginsRel);

            var conflicts = FindConflicts(game);
            if (conflicts.Count > 0)
            {
                Console.WriteLine("Эти моды тоже заменяют сеть игры, и вместе с DirectHost не работают:");
                foreach (var c in conflicts) Console.WriteLine("  " + c);
                if (!Ask($"Отключить их? Они переедут в {DisabledRel} и вернутся на место, если удалить DirectHost.", true))
                {
                    Console.WriteLine("Установка отменена, ничего не изменено.");
                    return 1;
                }
                foreach (var c in conflicts)
                {
                    string to = Path.Combine(game, DisabledRel, c);
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    if (File.Exists(to)) File.Delete(to);
                    File.Move(Path.Combine(plugins, c), to);
                    if (!m.Disabled.Contains(c)) m.Disabled.Add(c);
                    Console.WriteLine("  отключён: " + c);
                }
                m.Save(game);
            }

            using (var zip = OpenPayload())
            {
                var loader = zip.Entries.Where(e => e.Name.Length > 0 && Rel(e, "bepinex/") is string r && !r.StartsWith(@"BepInEx\")).ToList();
                string bepDll = Path.Combine(game, @"BepInEx\core\BepInEx.dll");
                if (File.Exists(Path.Combine(game, @"BepInEx\core\BepInEx.Preloader.dll")) && File.Exists(bepDll) && !m.OwnsBepInEx)
                {
                    var v = FileVersionInfo.GetVersionInfo(bepDll);
                    if (v.FileMajorPart != 5)
                    {
                        Error($"в игре уже стоит BepInEx {v.FileVersion}, а DirectHost работает только с BepInEx 5.");
                        return 1;
                    }
                    Console.WriteLine($"BepInEx {v.FileVersion} уже установлен — оставляю его.");
                    // BepInEx is started by the Doorstop loader (winhttp.dll + doorstop_config.ini); make sure it is there and on.
                    foreach (var e in loader)
                    {
                        string r = Rel(e, "bepinex/"), path = Path.Combine(game, r);
                        bool ok = File.Exists(path);
                        if (ok && r.Equals("doorstop_config.ini", StringComparison.OrdinalIgnoreCase)) ok = DoorstopOn(File.ReadAllText(path));
                        if (!ok) { Put(game, r, Bytes(e), m); Console.WriteLine("  восстановлен загрузчик: " + r); }
                    }
                }
                else
                {
                    Console.WriteLine(m.OwnsBepInEx ? "Обновляю BepInEx 5.4.23.5..." : "Ставлю BepInEx 5.4.23.5...");
                    foreach (var e in zip.Entries)
                    {
                        if (!(Rel(e, "bepinex/") is string r) || e.Name.Length == 0) continue;
                        // BepInEx.cfg may hold the player's own settings (and BepInEx rewrites it): never overwrite it.
                        if (r.StartsWith(@"BepInEx\config\", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(game, r))) continue;
                        Put(game, r, Bytes(e), m);
                    }
                    m.OwnsBepInEx = true;
                }
                Directory.CreateDirectory(plugins);
                m.Save(game);

                // Older copies under another folder name would load twice.
                string mine = Path.Combine(game, PluginRel);
                foreach (var f in Directory.GetFiles(plugins, "RepoDirectHost.dll", SearchOption.AllDirectories))
                    if (!Path.GetDirectoryName(f).Equals(mine, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(f);
                        Console.WriteLine("  удалена старая копия: " + RelTo(plugins, f));
                    }

                Console.WriteLine($"Ставлю DirectHost {Ver}...");
                Directory.CreateDirectory(mine);
                foreach (var e in zip.Entries)
                    if (Rel(e, "plugin/") is string r && e.Name.Length > 0)
                    {
                        string path = Path.Combine(mine, r);
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        File.WriteAllBytes(path, Bytes(e));
                    }
            }
            m.Plugin = Ver;
            m.Save(game);

            Console.WriteLine();
            Console.WriteLine($"Готово: DirectHost {Ver} установлен.");
            Console.WriteLine("Запускай R.E.P.O. как обычно из Steam. Вместе с игрой откроется чёрное окно BepInEx —");
            Console.WriteLine("так и должно быть, не закрывай его (игра закроется вместе с ним).");
            Console.WriteLine("Совет: Steam → Настройки → В игре → Steam Networking = «Всегда» или «Только друзья».");
            if (ManagerInstalled())
                Console.WriteLine("У тебя есть r2modman/Thunderstore: запуск через него берёт моды из его профиля, а не эту установку.");
            return 0;
        }

        private static void Put(string game, string rel, byte[] data, Marker m)
        {
            string path = Path.Combine(game, rel);
            if (File.Exists(path))
            {
                if (SameBytes(path, data)) return;
                if (!m.Added.Contains(rel) && !m.Replaced.Contains(rel))
                {
                    File.Copy(path, path + Backup, true);
                    m.Replaced.Add(rel);
                }
            }
            else if (!m.Added.Contains(rel) && !m.Replaced.Contains(rel)) m.Added.Add(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, data);
        }

        // ---------------------------------------------------------------- remove

        private static int Uninstall(string game, bool all)
        {
            var m = Marker.Load(game);
            string plugins = Path.Combine(game, PluginsRel);
            bool any = false;

            string mine = Path.Combine(game, PluginRel);
            if (Directory.Exists(mine)) { Directory.Delete(mine, true); any = true; }
            if (Directory.Exists(plugins))
                foreach (var f in Directory.GetFiles(plugins, "RepoDirectHost.dll", SearchOption.AllDirectories)) { File.Delete(f); any = true; }
            string cfg = Path.Combine(game, ConfigRel);
            if (File.Exists(cfg)) File.Delete(cfg);
            m.Plugin = null;
            Console.WriteLine(any ? "DirectHost удалён." : "DirectHost не был установлен.");

            if (m.Disabled.Count > 0 && Ask("Вернуть моды, которые установщик отключил (" + string.Join(", ", m.Disabled.Select(Path.GetFileName)) + ")?", true))
            {
                foreach (var c in m.Disabled.ToList())
                {
                    string from = Path.Combine(game, DisabledRel, c), to = Path.Combine(plugins, c);
                    if (File.Exists(from) && !File.Exists(to))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(to));
                        File.Move(from, to);
                        Console.WriteLine("  возвращён: " + c);
                    }
                    m.Disabled.Remove(c);
                }
                DeleteEmptyDirs(Path.Combine(game, DisabledRel));
            }

            // With someone else's BepInEx the only files changed were its loader, fixed for DirectHost: put them back.
            if (!m.OwnsBepInEx && (m.Added.Count > 0 || m.Replaced.Count > 0))
            {
                UndoFiles(game, m);
                Console.WriteLine("Загрузчик BepInEx возвращён как был до установки.");
            }

            if (all)
            {
                if (!m.OwnsBepInEx) Console.WriteLine("BepInEx ставил не этот установщик — его не трогаю.");
                else if (Directory.Exists(plugins) && Directory.EnumerateFiles(plugins, "*", SearchOption.AllDirectories).Any())
                    Console.WriteLine("В BepInEx\\plugins есть другие моды — BepInEx оставляю, чтобы они работали.");
                else
                {
                    UndoFiles(game, m);
                    string bep = Path.Combine(game, "BepInEx");
                    foreach (var gen in new[] { "cache", "LogOutput.log", "LogOutput.log.1", "LogOutput.log.2" })
                    {
                        string p = Path.Combine(bep, gen);
                        if (Directory.Exists(p)) Directory.Delete(p, true);
                        else if (File.Exists(p)) File.Delete(p);
                    }
                    m.OwnsBepInEx = false;
                    m.Save(game);
                    DeleteEmptyDirs(bep);
                    Console.WriteLine("BepInEx удалён, игра снова без модов.");
                }
            }
            m.Save(game);
            return 0;
        }

        /// <summary>Deletes the files the installer created and puts back the ones it overwrote.</summary>
        private static void UndoFiles(string game, Marker m)
        {
            foreach (var r in m.Added)
            {
                string p = Path.Combine(game, r);
                if (File.Exists(p)) File.Delete(p);
            }
            foreach (var r in m.Replaced)
            {
                string p = Path.Combine(game, r);
                if (File.Exists(p + Backup)) { File.Copy(p + Backup, p, true); File.Delete(p + Backup); }
            }
            m.Added.Clear();
            m.Replaced.Clear();
        }

        private static void DeleteEmptyDirs(string dir)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var d in Directory.GetDirectories(dir)) DeleteEmptyDirs(d);
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }

        // ---------------------------------------------------------------- checks

        private static void PrintStatus(string game, Marker m)
        {
            Console.WriteLine("Папка игры:  " + game);
            string bep = Path.Combine(game, @"BepInEx\core\BepInEx.dll");
            Console.WriteLine("BepInEx:     " + (File.Exists(bep)
                ? FileVersionInfo.GetVersionInfo(bep).FileVersion + (m.OwnsBepInEx ? " (поставлен этим установщиком)" : "")
                : "нет"));
            string plugins = Path.Combine(game, PluginsRel);
            var dh = Directory.Exists(plugins) ? Directory.GetFiles(plugins, "RepoDirectHost.dll", SearchOption.AllDirectories).FirstOrDefault() : null;
            Console.WriteLine("DirectHost:  " + (dh != null ? DllVersion(dh) : "не установлен"));
            var conflicts = FindConflicts(game);
            if (conflicts.Count > 0) Console.WriteLine("Мешают:      " + string.Join(", ", conflicts) + " (при установке будут отключены)");
        }

        private static List<string> FindConflicts(string game)
        {
            var res = new List<string>();
            string plugins = Path.Combine(game, PluginsRel);
            if (!Directory.Exists(plugins)) return res;
            var needles = ConflictIds.Select(Encoding.UTF8.GetBytes).ToArray();
            foreach (var f in Directory.GetFiles(plugins, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(f);
                if (name.Equals("RepoDirectHost.dll", StringComparison.OrdinalIgnoreCase)) continue;
                bool hit = ConflictNames.Any(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (!hit)
                {
                    var b = File.ReadAllBytes(f);
                    hit = needles.Any(n => IndexOf(b, n) >= 0);
                }
                if (hit) res.Add(RelTo(plugins, f));
            }
            return res;
        }

        private static bool EnsureGameClosed()
        {
            while (Process.GetProcessesByName("REPO").Length > 0)
            {
                if (_auto) { Error("R.E.P.O. запущена — закрой игру."); return false; }
                Console.Write("R.E.P.O. сейчас запущена. Закрой игру и нажми Enter (0 — отмена): ");
                if ((Console.ReadLine() ?? "0").Trim() == "0") return false;
            }
            return true;
        }

        private static bool EnsureWritable(string game)
        {
            try
            {
                string t = Path.Combine(game, ".directhost-write-test");
                File.WriteAllText(t, "");
                File.Delete(t);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                if (IsAdmin() || _auto) { Error("нет прав на запись в папку игры. Запусти установщик от имени администратора."); return false; }
                Console.WriteLine("Для записи в папку игры нужны права администратора — перезапускаю установщик с ними...");
                try
                {
                    Process.Start(new ProcessStartInfo(Assembly.GetEntryAssembly().Location, $"--game \"{game}\"") { Verb = "runas", UseShellExecute = true });
                    _noPause = true;
                }
                catch (Win32Exception) { Error("права администратора не выданы — ничего не изменено."); }
                return false;
            }
        }

        private static bool IsAdmin() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        private static bool DoorstopOn(string ini) =>
            Regex.IsMatch(ini, @"(?im)^\s*enabled\s*=\s*true\s*$") &&
            Regex.IsMatch(ini, @"(?im)^\s*target_assembly\s*=.*BepInEx\.Preloader\.dll\s*$");

        private static bool ManagerInstalled()
        {
            string ad = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Directory.Exists(Path.Combine(ad, "r2modmanPlus-local")) || Directory.Exists(Path.Combine(ad, "Thunderstore Mod Manager"));
        }

        // ---------------------------------------------------------------- finding the game

        private static string FindGame()
        {
            var libs = new List<string>();
            foreach (var steam in SteamRoots())
            {
                libs.Add(steam);
                foreach (var vdf in new[] { @"steamapps\libraryfolders.vdf", @"config\libraryfolders.vdf" })
                {
                    string p = Path.Combine(steam, vdf);
                    if (!File.Exists(p)) continue;
                    foreach (Match mm in Regex.Matches(File.ReadAllText(p), "\"path\"\\s+\"([^\"]+)\""))
                        libs.Add(mm.Groups[1].Value.Replace(@"\\", @"\"));
                }
            }
            foreach (var d in DriveInfo.GetDrives())
                if (d.DriveType == DriveType.Fixed)
                    foreach (var sub in new[] { "SteamLibrary", "Steam", @"Program Files (x86)\Steam", @"Games\Steam", @"Games\SteamLibrary" })
                        libs.Add(Path.Combine(d.Name, sub));

            foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    string dir = null, acf = Path.Combine(lib, "steamapps", "appmanifest_" + AppId + ".acf");
                    if (File.Exists(acf))
                    {
                        var mm = Regex.Match(File.ReadAllText(acf), "\"installdir\"\\s+\"([^\"]+)\"");
                        if (mm.Success) dir = Path.Combine(lib, "steamapps", "common", mm.Groups[1].Value);
                    }
                    dir = dir ?? Path.Combine(lib, @"steamapps\common\REPO");
                    if (IsGameDir(dir)) return Normalize(dir);
                }
                catch (Exception) { /* unreadable library: try the next one */ }
            }
            return null;
        }

        private static IEnumerable<string> SteamRoots()
        {
            foreach (var (key, name) in new[]
            {
                (@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath"),
                (@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                (@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath"),
            })
            {
                string v = null;
                try { v = Registry.GetValue(key, name, null) as string; } catch { }
                if (!string.IsNullOrEmpty(v)) yield return v.Replace('/', '\\');
            }
        }

        private static string AskGameDir()
        {
            while (true)
            {
                Console.WriteLine();
                Console.WriteLine("Укажи папку R.E.P.O. (ту, где лежит REPO.exe): перетащи её в это окно или вставь путь.");
                Console.WriteLine("Найти её можно в Steam: R.E.P.O. → ПКМ → Управление → Просмотреть локальные файлы.");
                Console.Write("Папка (пусто — выход): ");
                string s = (Console.ReadLine() ?? "").Trim().Trim('"');
                if (s.Length == 0) return null;
                if (s.EndsWith("REPO.exe", StringComparison.OrdinalIgnoreCase)) s = Path.GetDirectoryName(s);
                try { s = Normalize(s); } catch (Exception) { }
                if (IsGameDir(s)) return s;
                Console.WriteLine("В этой папке нет REPO.exe.");
            }
        }

        private static bool IsGameDir(string d)
        {
            try { return File.Exists(Path.Combine(d, "REPO.exe")) && Directory.Exists(Path.Combine(d, "REPO_Data")); }
            catch (Exception) { return false; }
        }

        // ---------------------------------------------------------------- helpers

        private static ZipArchive OpenPayload()
        {
            var s = typeof(Program).Assembly.GetManifestResourceStream("payload.zip")
                    ?? throw new InvalidOperationException("в установщике нет файлов (payload.zip) — он собран неправильно.");
            return new ZipArchive(s, ZipArchiveMode.Read);
        }

        /// <summary>Entry path below the given payload folder, with Windows separators; null if it is elsewhere.</summary>
        private static string Rel(ZipArchiveEntry e, string folder)
        {
            string n = e.FullName.Replace('\\', '/');
            return n.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? n.Substring(folder.Length).Replace('/', '\\') : null;
        }

        private static byte[] Bytes(ZipArchiveEntry e)
        {
            using (var s = e.Open())
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        private static bool SameBytes(string path, byte[] data)
        {
            var fi = new FileInfo(path);
            return fi.Length == data.Length && File.ReadAllBytes(path).SequenceEqual(data);
        }

        private static int IndexOf(byte[] hay, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= hay.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && hay[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        private static string RelTo(string root, string path) => path.Substring(root.Length).TrimStart('\\');
        private static string Normalize(string d) => Path.GetFullPath(d).TrimEnd('\\');
        private static string FormatVersion(Version v) => v.Build >= 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}";

        private static string DllVersion(string path)
        {
            string fv = FileVersionInfo.GetVersionInfo(path).FileVersion;
            return Version.TryParse(fv, out var v) ? FormatVersion(v) : fv ?? "?";
        }

        private static bool Ask(string question, bool def)
        {
            if (_auto) return def;
            while (true)
            {
                Console.Write(question + (def ? " [Д/н]: " : " [д/Н]: "));
                string s = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
                if (s.Length == 0) return def;
                if (s == "д" || s == "да" || s == "y" || s == "yes") return true;
                if (s == "н" || s == "нет" || s == "n" || s == "no") return false;
            }
        }

        private static void Error(string msg)
        {
            var c = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Ошибка: " + msg);
            Console.ForegroundColor = c;
        }

        /// <summary>What the installer did to this game folder (BepInEx\directhost-installer.txt).</summary>
        private sealed class Marker
        {
            public readonly List<string> Added = new List<string>();    // files it created, relative to the game folder
            public readonly List<string> Replaced = new List<string>(); // files it overwrote; the original is kept as <file>.bak-directhost
            public readonly List<string> Disabled = new List<string>(); // conflicting plugins moved to BepInEx\disabled-by-directhost
            public bool OwnsBepInEx;
            public string Plugin;

            public static Marker Load(string game)
            {
                var m = new Marker();
                string p = Path.Combine(game, MarkerRel);
                if (!File.Exists(p)) return m;
                foreach (var raw in File.ReadAllLines(p))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int sp = line.IndexOf(' ');
                    string k = sp < 0 ? line : line.Substring(0, sp), v = sp < 0 ? "" : line.Substring(sp + 1);
                    switch (k)
                    {
                        case "owns-bepinex": m.OwnsBepInEx = true; break;
                        case "plugin": m.Plugin = v; break;
                        case "added": m.Added.Add(v); break;
                        case "replaced": m.Replaced.Add(v); break;
                        case "disabled": m.Disabled.Add(v); break;
                    }
                }
                return m;
            }

            public void Save(string game)
            {
                string p = Path.Combine(game, MarkerRel);
                if (!OwnsBepInEx && Plugin == null && Added.Count == 0 && Replaced.Count == 0 && Disabled.Count == 0)
                {
                    if (File.Exists(p)) File.Delete(p);
                    return;
                }
                var sb = new StringBuilder();
                sb.AppendLine("# Written by the REPO DirectHost installer: what it changed in this game folder, so it can undo it.");
                if (OwnsBepInEx) sb.AppendLine("owns-bepinex");
                if (Plugin != null) sb.AppendLine("plugin " + Plugin);
                foreach (var r in Added) sb.AppendLine("added " + r);
                foreach (var r in Replaced) sb.AppendLine("replaced " + r);
                foreach (var r in Disabled) sb.AppendLine("disabled " + r);
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p, sb.ToString());
            }
        }
    }
}
