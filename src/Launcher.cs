// Warp : lanceur de jeux pour Windows.
//
// Hôte natif. Une fenêtre sans barre de titre système affiche l'interface (dossier ui\)
// dans WebView2, et exécute pour elle tout ce qui touche au système : lancer un jeu,
// suivre les processus, compter le temps de jeu, ouvrir un sélecteur de fichier,
// enregistrer la bibliothèque (games.json).
//
// Compilation : src\build.bat (compilateur C# livré avec Windows, rien à installer).
// Ce compilateur s'arrête à C# 5 : pas de $"...", pas de ?., pas de membres en =>.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

[assembly: AssemblyTitle(Brand.Name)]
[assembly: AssemblyProduct(Brand.Name)]
[assembly: AssemblyDescription("Lanceur de jeux")]
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]

/// <summary>Le nom de l'app, à un seul endroit. Pour en changer : ici, le nom du .exe dans build.bat, et le mot affiché dans ui\index.html.</summary>
static class Brand { public const string Name = "Warp"; }

namespace Lanceur
{
    // ---------- Données enregistrées ----------

    /// <summary>Un jeu de la bibliothèque (une entrée de games.json).</summary>
    public class GameEntry
    {
        public string id;
        public string name;
        public string subtitle;                    // petite ligne sous le titre, ex. « Via Eglantine »
        public string exe;                         // exécutable à lancer (le jeu, ou l'émulateur)
        public string args;                        // options de lancement, comme dans un raccourci Windows
        public string cwd;                         // dossier de travail ; vide = dossier de l'exe
        public Dictionary<string, string> env;     // variables d'environnement en plus
        public string image;                       // visuel, chemin absolu ou relatif au dossier de l'app
        public UpdateInfo update;                  // mises à jour de l'émulateur depuis GitHub (voir Updater.cs)
        public string dismiss;                     // titre d'une fenêtre que le jeu ouvre à chaque démarrage, à refermer pour toi
    }

    public class Library { public List<GameEntry> games = new List<GameEntry>(); }

    public class GameStats { public double seconds; public string last; public int launches; }

    public class WindowPlacement { public int x; public int y; public int w; public int h; public bool maximized; }

    /// <summary>Une partie en cours : le processus suivi et l'heure de début.</summary>
    class Session { public Process process; public DateTime started; public DateTime tick; }

    /// <summary>Options du mode test (capture de l'interface dans un PNG, puis fermeture).</summary>
    class ShotOptions { public string file; public string js; public string screen; public int width; public int height; public int delay; public int after; public bool maximized; }

    static class Native
    {
        public const int WM_NCCALCSIZE = 0x0083, WM_NCLBUTTONDOWN = 0x00A1, WM_DPICHANGED = 0x02E0;
        public const int HTCAPTION = 2, SW_RESTORE = 9;
        public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
        public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int left, top, right, bottom; }

        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern int GetSystemMetricsForDpi(int index, uint dpi);
        [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int max);
        public delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern int GetWindowRgnBox(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] argv)
        {
            ShotOptions shot = null;
            bool debug = false;
            for (int i = 0; i < argv.Length; i++)
            {
                string a = argv[i];
                bool more = i + 1 < argv.Length;
                if (a == "--debug") debug = true;
                else if (a == "--shot" && more) { if (shot == null) shot = new ShotOptions(); shot.file = argv[++i]; }
                else if (a == "--js-file" && more && shot != null) shot.js = File.ReadAllText(argv[++i]);
                else if (a == "--screen" && more && shot != null) shot.screen = argv[++i];
                else if (a == "--max" && shot != null) shot.maximized = true;
                else if (a == "--delay" && more && shot != null) shot.delay = int.Parse(argv[++i]);
                else if (a == "--after" && more && shot != null) shot.after = int.Parse(argv[++i]);
                else if (a == "--size" && more && shot != null)
                {
                    string[] wh = argv[++i].Split('x');
                    shot.width = int.Parse(wh[0]); shot.height = int.Parse(wh[1]);
                }
            }

            // Une seule fenêtre à la fois : un second lancement ramène la première au premier plan.
            Mutex once = null;
            if (shot == null)
            {
                bool first;
                once = new Mutex(true, "Lanceur." + Brand.Name + ".Instance", out first);
                if (!first) { ActivateRunningInstance(); return; }
            }

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;   // exigé par GitHub
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { Application.Run(new MainForm(shot, debug)); }
            catch (Exception ex) { MainForm.Fatal(ex); }
            GC.KeepAlive(once);
        }

        static void ActivateRunningInstance()
        {
            Process me = Process.GetCurrentProcess();
            foreach (Process p in Process.GetProcessesByName(me.ProcessName))
            {
                if (p.Id == me.Id) continue;
                IntPtr h = p.MainWindowHandle;
                if (h == IntPtr.Zero) continue;
                if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
                Native.SetForegroundWindow(h);
                return;
            }
        }
    }

    partial class MainForm : Form
    {
        const string Version = "2.0";
        const string Origin = "https://lanceur.example";   // adresse interne : rien ne sort sur le réseau
        static readonly Color Ink = Color.FromArgb(12, 11, 10);

        readonly string appDir, uiDir, dataDir, coversDir, libraryFile, statsFile, windowFile;
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        readonly WebView2 web = new WebView2();
        readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer();
        readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
        readonly Dictionary<string, string> picks = new Dictionary<string, string>();   // jeton -> image choisie
        readonly ShotOptions shot;
        readonly bool debug;

        CoreWebView2 core;
        Library library = new Library();
        Dictionary<string, GameStats> stats = new Dictionary<string, GameStats>();
        string libraryWarning, lastExists = "";
        DateTime lastPush = DateTime.UtcNow;
        int pollCount;
        bool shotDone;

        public MainForm(ShotOptions shot, bool debug)
        {
            this.shot = shot;
            this.debug = debug;

            appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            uiDir = Path.Combine(appDir, "ui");
            dataDir = Path.Combine(appDir, "data");
            coversDir = Path.Combine(appDir, "covers");
            libraryFile = Path.Combine(appDir, "games.json");
            statsFile = Path.Combine(dataDir, "stats.json");
            windowFile = Path.Combine(dataDir, "window.json");

            Text = Brand.Name;
            BackColor = Ink;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = shot == null;
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                if (s != null) Icon = new Icon(s, SystemInformation.IconSize);

            float scale;
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            MinimumSize = new Size((int)(900 * scale), (int)(580 * scale));
            PlaceWindow(scale);

            web.Dock = DockStyle.Fill;
            web.DefaultBackgroundColor = Ink;
            Controls.Add(web);

            LoadLibrary();
            poll.Interval = 2000;
            poll.Tick += OnPoll;
        }

        // ================= Fenêtre =================

        protected override bool ShowWithoutActivation { get { return shot != null; } }

        void PlaceWindow(float scale)
        {
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            int w = Math.Min((int)(1400 * scale), (int)(work.Width * 0.92));
            int h = Math.Min((int)(860 * scale), (int)(work.Height * 0.92));
            if (shot != null && shot.width > 0) { w = (int)(shot.width * scale); h = (int)(shot.height * scale); }
            Rectangle b = new Rectangle(work.Left + (work.Width - w) / 2, work.Top + (work.Height - h) / 2, w, h);
            bool max = shot != null && shot.maximized;

            if (shot == null)
            {
                // Retrouve la taille et la position de la dernière fois, si elles tombent encore sur un écran.
                WindowPlacement p = ReadJson<WindowPlacement>(windowFile);
                Rectangle saved = new Rectangle(p.x, p.y, p.w, p.h);
                if (p.w >= MinimumSize.Width && p.h >= MinimumSize.Height)
                    foreach (Screen s in Screen.AllScreens)
                        if (s.WorkingArea.IntersectsWith(saved)) { b = saved; max = p.maximized; break; }
            }
            Bounds = b;
            if (max) WindowState = FormWindowState.Maximized;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int dark = 1, border = 0x0024272A;   // COLORREF 0x00BBGGRR
            Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4);     // bordure et ombre en thème sombre
            Native.DwmSetWindowAttribute(Handle, 34, ref border, 4);   // couleur du liseré (Windows 11)
            // Force Windows à recalculer le cadre : c'est là que la barre de titre système disparaît.
            Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
            {
                // On garde les bords standard (redimensionnement, coins arrondis, ombre) mais on retire
                // la barre de titre : l'interface occupe toute la hauteur et dessine la sienne.
                Native.RECT before = (Native.RECT)Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
                base.WndProc(ref m);
                Native.RECT after = (Native.RECT)Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
                after.top = before.top;
                if (Native.IsZoomed(Handle))
                {
                    // Agrandie, la fenêtre dépasse de l'écran de l'épaisseur du cadre : on compense en haut.
                    uint dpi = Native.GetDpiForWindow(Handle);
                    after.top += Native.GetSystemMetricsForDpi(33, dpi) + Native.GetSystemMetricsForDpi(92, dpi);
                }
                Marshal.StructureToPtr(after, m.LParam, false);
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == Native.WM_DPICHANGED)
            {
                // Passage sur un écran d'une autre échelle : Windows propose la nouvelle taille.
                Native.RECT r = (Native.RECT)Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
                Native.SetWindowPos(Handle, IntPtr.Zero, r.left, r.top, r.right - r.left, r.bottom - r.top,
                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            }
            base.WndProc(ref m);
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (shot != null)
                Native.SetWindowPos(Handle, Native.HWND_BOTTOM, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            try { await StartWeb(); }
            catch (Exception ex) { Fatal(ex); Close(); }
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (core != null) web.Focus();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (core != null) PushWindow();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            poll.Stop();
            if (shot == null)
            {
                try
                {
                    Rectangle b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                    WindowPlacement p = new WindowPlacement();
                    p.x = b.X; p.y = b.Y; p.w = b.Width; p.h = b.Height;
                    p.maximized = WindowState == FormWindowState.Maximized;
                    WriteJson(windowFile, p);
                    SaveStats();
                }
                catch { }
            }
            base.OnFormClosing(e);
        }

        object WindowAction(string action)
        {
            if (action == "minimize") WindowState = FormWindowState.Minimized;
            else if (action == "toggle") WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
            else if (action == "close") Close();
            return null;
        }

        public static void Fatal(Exception ex)
        {
            string text = ex is WebView2RuntimeNotFoundException
                ? "Il manque le composant « Microsoft Edge WebView2 Runtime ».\n\nInstalle-le depuis https://developer.microsoft.com/microsoft-edge/webview2 puis relance " + Brand.Name + "."
                : Brand.Name + " n'a pas pu démarrer.\n\n" + ex.Message;
            try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "erreur.log"), DateTime.Now + "\r\n" + ex + "\r\n\r\n"); } catch { }
            MessageBox.Show(text, Brand.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        // ================= Interface (WebView2) =================

        async Task StartWeb()
        {
            if (!File.Exists(Path.Combine(uiDir, "index.html")))
                throw new FileNotFoundException("Le dossier « ui » est introuvable à côté de " + Brand.Name + ".exe.");
            Directory.CreateDirectory(dataDir);

            CoreWebView2EnvironmentOptions options = null;
            string profile = Path.Combine(dataDir, "webview");
            if (shot != null)
            {
                // Mode test : profil séparé, et rendu même si la fenêtre est cachée derrière les autres.
                profile = Path.Combine(dataDir, "webview-test");
                options = new CoreWebView2EnvironmentOptions("--disable-features=CalculateNativeWinOcclusion");
            }
            CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, profile, options);
            await web.EnsureCoreWebView2Async(env);
            core = web.CoreWebView2;

            CoreWebView2Settings s = core.Settings;
            s.AreDevToolsEnabled = debug;
            s.AreDefaultContextMenusEnabled = debug;
            s.AreBrowserAcceleratorKeysEnabled = debug;   // pas de F5, Ctrl+P, etc. : c'est une app, pas une page
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = false;
            s.IsPinchZoomEnabled = false;
            s.IsSwipeNavigationEnabled = false;
            s.IsPasswordAutosaveEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            s.IsNonClientRegionSupportEnabled = true;     // la barre du haut de l'interface déplace la fenêtre

            // Tout ce que l'interface charge passe par OnResource : fichiers de ui\ et visuels des jeux.
            core.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnResource;
            core.WebMessageReceived += OnMessage;
            core.NewWindowRequested += delegate(object o, CoreWebView2NewWindowRequestedEventArgs a) { a.Handled = true; };
            core.NavigationStarting += delegate(object o, CoreWebView2NavigationStartingEventArgs a)
            {
                if (!a.Uri.StartsWith(Origin + "/", StringComparison.OrdinalIgnoreCase)) a.Cancel = true;
            };
            core.NavigationCompleted += delegate { if (shot == null) web.Focus(); };
            core.Navigate(Origin + "/index.html");
            poll.Start();
        }

        void OnResource(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            string file = null;
            try
            {
                string path = Uri.UnescapeDataString(new Uri(e.Request.Uri).AbsolutePath);
                if (path.StartsWith("/media/"))
                {
                    GameEntry g = Find(path.Substring(7));
                    if (g != null) file = ResolveImage(g);
                }
                else if (path.StartsWith("/preview/")) picks.TryGetValue(path.Substring(9), out file);
                else
                {
                    string rel = (path == "/" ? "index.html" : path.TrimStart('/')).Replace('/', '\\');
                    if (rel.IndexOf("..", StringComparison.Ordinal) < 0 && rel.IndexOf(':') < 0) file = Path.Combine(uiDir, rel);
                }
                if (file != null && File.Exists(file))
                {
                    MemoryStream body = new MemoryStream(File.ReadAllBytes(file));
                    e.Response = core.Environment.CreateWebResourceResponse(body, 200, "OK",
                        "Content-Type: " + Mime(file) + "\r\nCache-Control: no-store");
                    return;
                }
            }
            catch { }
            e.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");
        }

        static string Mime(string file)
        {
            switch (Path.GetExtension(file).ToLowerInvariant())
            {
                case ".html": return "text/html; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".js": return "text/javascript; charset=utf-8";
                case ".json": return "application/json; charset=utf-8";
                case ".svg": return "image/svg+xml";
                case ".woff2": return "font/woff2";
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".webp": return "image/webp";
                case ".gif": return "image/gif";
                case ".bmp": return "image/bmp";
                case ".ico": return "image/x-icon";
                default: return "application/octet-stream";
            }
        }

        // ----- Messages : l'interface appelle { id, cmd, args }, l'hôte répond { id, ok, result | error } -----

        void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string raw = e.WebMessageAsJson;
            // Différé : certaines commandes ouvrent une boîte de dialogue, à ne pas faire dans l'évènement WebView2.
            BeginInvoke(new Action(delegate { Dispatch(raw); }));
        }

        async void Dispatch(string raw)
        {
            object id = null;
            Dictionary<string, object> reply = new Dictionary<string, object>();
            try
            {
                Dictionary<string, object> msg = json.Deserialize<Dictionary<string, object>>(raw);
                msg.TryGetValue("id", out id);
                Dictionary<string, object> a = msg.ContainsKey("args") ? msg["args"] as Dictionary<string, object> : null;
                object result = Run(Str(msg, "cmd"), a ?? new Dictionary<string, object>());
                Task<object> pending = result as Task<object>;
                if (pending != null) result = await pending;   // commandes qui passent par le réseau ou le disque
                reply["result"] = result;
                reply["ok"] = true;
            }
            catch (Exception ex)
            {
                reply["ok"] = false;
                reply["error"] = ex.Message;
            }
            if (id != null) { reply["id"] = id; Post(reply); }
        }

        object Run(string cmd, Dictionary<string, object> a)
        {
            switch (cmd)
            {
                case "init":
                    Dictionary<string, object> r = new Dictionary<string, object>();
                    r["games"] = PublicGames();
                    r["version"] = Version;
                    r["maximized"] = WindowState == FormWindowState.Maximized;
                    r["warning"] = libraryWarning;
                    r["nativeDrag"] = NativeDrag();
                    return r;
                case "ready": OnUiReady(); AutoCheckUpdates(); return null;
                case "launch": return Launch(Str(a, "id"));
                case "focus": return FocusGame(Str(a, "id"));
                case "openFolder": return OpenFolder(Str(a, "id"));
                case "pickExe": return PickExe(Str(a, "current"));
                case "pickImage": return PickImage();
                case "saveGame": return SaveGame(a);
                case "removeGame": return RemoveGame(Str(a, "id"));
                case "window": return WindowAction(Str(a, "action"));
                case "checkUpdate": return CheckUpdate(Str(a, "id"));
                case "startUpdate": return StartUpdate(Str(a, "id"));
                case "cancelUpdate": jobCancel = true; return null;
                case "restoreUpdate": return RestoreUpdate(Str(a, "id"));
                case "ignoreUpdate": return IgnoreUpdate(Str(a, "id"));
                case "openUrl": return OpenUrl(Str(a, "url"));
                case "drag":
                    // Secours si le déplacement natif (app-region) n'est pas pris en charge.
                    Native.ReleaseCapture();
                    Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HTCAPTION, IntPtr.Zero);
                    return null;
                default: throw new InvalidOperationException("Commande inconnue : " + cmd);
            }
        }

        /// <summary>Le déplacement de fenêtre par la barre du haut (app-region) existe depuis le moteur 123.</summary>
        bool NativeDrag()
        {
            try { return int.Parse(core.Environment.BrowserVersionString.Split('.')[0]) >= 123; } catch { return true; }
        }

        void Post(object message)
        {
            if (core != null) core.PostWebMessageAsJson(json.Serialize(message));
        }

        void PushState()
        {
            lastPush = DateTime.UtcNow;
            Dictionary<string, object> m = new Dictionary<string, object>();
            m["event"] = "state";
            m["games"] = PublicGames();
            Post(m);
        }

        void PushWindow()
        {
            Dictionary<string, object> m = new Dictionary<string, object>();
            m["event"] = "window";
            m["maximized"] = WindowState == FormWindowState.Maximized;
            Post(m);
        }

        static string Str(Dictionary<string, object> a, string key)
        {
            object v;
            return a != null && a.TryGetValue(key, out v) && v != null ? Convert.ToString(v) : "";
        }

        // ================= Bibliothèque =================

        void LoadLibrary()
        {
            if (File.Exists(libraryFile))
            {
                try
                {
                    Library l = json.Deserialize<Library>(File.ReadAllText(libraryFile, Encoding.UTF8));
                    if (l != null && l.games != null) library = l;
                }
                catch (Exception ex)
                {
                    // Fichier modifié à la main et devenu illisible : on le met de côté au lieu de l'écraser.
                    string aside = Path.Combine(appDir, "games.illisible.json");
                    try { File.Copy(libraryFile, aside, true); } catch { }
                    libraryWarning = "games.json est illisible (" + ex.Message + "). Une copie est gardée dans games.illisible.json.";
                }
            }
            library.games.RemoveAll(delegate(GameEntry g) { return g == null || string.IsNullOrEmpty(g.id); });
            stats = ReadJson<Dictionary<string, GameStats>>(statsFile);
            LoadReleases();
            lastExists = ExistsSignature();
        }

        void SaveLibrary()
        {
            // Écrit champ par champ pour garder un fichier lisible, sans valeurs vides.
            List<object> games = new List<object>();
            foreach (GameEntry g in library.games)
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["id"] = g.id;
                d["name"] = g.name;
                if (!string.IsNullOrEmpty(g.subtitle)) d["subtitle"] = g.subtitle;
                d["exe"] = g.exe;
                if (!string.IsNullOrEmpty(g.args)) d["args"] = g.args;
                if (!string.IsNullOrEmpty(g.cwd)) d["cwd"] = g.cwd;
                if (g.env != null && g.env.Count > 0) d["env"] = g.env;
                if (!string.IsNullOrEmpty(g.image)) d["image"] = g.image;
                if (!string.IsNullOrEmpty(g.dismiss)) d["dismiss"] = g.dismiss;
                object update = SavedUpdate(g);
                if (update != null) d["update"] = update;
                games.Add(d);
            }
            Dictionary<string, object> root = new Dictionary<string, object>();
            root["games"] = games;
            WriteJson(libraryFile, root);
        }

        void SaveStats()
        {
            if (shot != null) return;   // le mode test ne laisse aucune trace
            try { Directory.CreateDirectory(dataDir); WriteJson(statsFile, stats); } catch { }
        }

        GameEntry Find(string id)
        {
            foreach (GameEntry g in library.games) if (g.id == id) return g;
            return null;
        }

        GameEntry Need(string id)
        {
            GameEntry g = Find(id);
            if (g == null) throw new InvalidOperationException("Ce jeu n'est plus dans la bibliothèque.");
            return g;
        }

        GameStats StatsFor(string id)
        {
            GameStats st;
            if (!stats.TryGetValue(id, out st) || st == null) { st = new GameStats(); stats[id] = st; }
            return st;
        }

        string ResolveImage(GameEntry g)
        {
            if (string.IsNullOrEmpty(g.image)) return null;
            try
            {
                string p = Path.IsPathRooted(g.image) ? g.image : Path.Combine(appDir, g.image);
                return File.Exists(p) ? p : null;
            }
            catch { return null; }
        }

        static bool ExeExists(GameEntry g)
        {
            try { return !string.IsNullOrEmpty(g.exe) && File.Exists(g.exe); } catch { return false; }
        }

        string ExistsSignature()
        {
            StringBuilder sb = new StringBuilder();
            foreach (GameEntry g in library.games) sb.Append(ExeExists(g) ? '1' : '0');
            return sb.ToString();
        }

        /// <summary>Ce que l'interface reçoit pour chaque jeu.</summary>
        List<object> PublicGames()
        {
            List<object> list = new List<object>();
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            foreach (GameEntry g in library.games)
            {
                GameStats st = StatsFor(g.id);
                Session s;
                sessions.TryGetValue(g.id, out s);
                string img = ResolveImage(g);
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["id"] = g.id;
                d["name"] = g.name ?? "";
                d["subtitle"] = g.subtitle ?? "";
                d["exe"] = g.exe ?? "";
                d["args"] = g.args ?? "";
                d["image"] = img == null ? null : "/media/" + Uri.EscapeDataString(g.id) + "?v=" + File.GetLastWriteTimeUtc(img).Ticks;
                d["exists"] = ExeExists(g);
                d["running"] = s != null;
                d["sessionStart"] = s == null ? 0 : (long)(s.started - epoch).TotalMilliseconds;
                d["seconds"] = Math.Round(st.seconds);
                d["last"] = st.last;
                d["launches"] = st.launches;
                d["update"] = PublicUpdate(g);
                list.Add(d);
            }
            return list;
        }

        Dictionary<string, object> WithGames(string key, object value)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r[key] = value;
            r["games"] = PublicGames();
            return r;
        }

        // ----- Ajouter, modifier, retirer -----

        object PickExe(string current)
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Choisir le jeu ou l'émulateur";
                d.Filter = "Applications (*.exe)|*.exe|Tous les fichiers (*.*)|*.*";
                try { if (current != "" && File.Exists(current)) { d.InitialDirectory = Path.GetDirectoryName(current); d.FileName = Path.GetFileName(current); } } catch { }
                if (d.ShowDialog(this) != DialogResult.OK) return null;

                // Propose un nom à partir des informations du fichier, sinon de son nom.
                string name = "";
                try
                {
                    FileVersionInfo v = FileVersionInfo.GetVersionInfo(d.FileName);
                    name = !string.IsNullOrWhiteSpace(v.ProductName) ? v.ProductName : (v.FileDescription ?? "");
                }
                catch { }
                if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(d.FileName);
                Dictionary<string, object> r = new Dictionary<string, object>();
                r["path"] = d.FileName;
                r["name"] = name.Trim();
                return r;
            }
        }

        object PickImage()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Choisir le visuel du jeu";
                d.Filter = "Images (*.jpg;*.jpeg;*.png;*.webp;*.gif;*.bmp)|*.jpg;*.jpeg;*.png;*.webp;*.gif;*.bmp";
                if (d.ShowDialog(this) != DialogResult.OK) return null;
                string token = Guid.NewGuid().ToString("N") + Path.GetExtension(d.FileName).ToLowerInvariant();
                picks[token] = d.FileName;
                Dictionary<string, object> r = new Dictionary<string, object>();
                r["token"] = token;
                r["url"] = "/preview/" + token;
                return r;
            }
        }

        object SaveGame(Dictionary<string, object> a)
        {
            string name = Str(a, "name").Trim(), exe = Str(a, "exe").Trim();
            if (name == "") throw new InvalidOperationException("Donne un nom au jeu.");
            if (exe == "") throw new InvalidOperationException("Choisis l'exécutable du jeu ou de l'émulateur.");

            GameEntry g = Find(Str(a, "id"));
            if (g == null)
            {
                g = new GameEntry();
                g.id = NewId(name);
                library.games.Add(g);
            }
            else if (!string.Equals(g.exe, exe, StringComparison.OrdinalIgnoreCase))
            {
                g.cwd = null;   // l'exécutable a changé : le dossier de travail redevient celui du nouvel exe
                g.env = null;
                if (g.update != null) { g.update.installed = null; g.update.installedAt = null; g.update.previous = null; }
            }
            g.name = name;
            g.exe = exe;
            g.args = Str(a, "args").Trim();
            g.subtitle = Str(a, "subtitle").Trim();
            ApplyUpdateSettings(g, Str(a, "repo"), Str(a, "channel"));

            string token = Str(a, "imageToken"), picked;
            if (Str(a, "removeImage") == "True" || Str(a, "removeImage") == "true") { DeleteOwnCover(g); g.image = null; }
            else if (token != "" && picks.TryGetValue(token, out picked) && File.Exists(picked))
            {
                // Copie le visuel dans covers\ : l'app ne dépend plus de l'emplacement d'origine.
                Directory.CreateDirectory(coversDir);
                DeleteOwnCover(g);
                string file = g.id + "-" + DateTime.Now.ToString("HHmmss") + Path.GetExtension(picked).ToLowerInvariant();
                File.Copy(picked, Path.Combine(coversDir, file), true);
                g.image = "covers\\" + file;
            }

            SaveLibrary();
            lastExists = ExistsSignature();
            return WithGames("id", g.id);
        }

        object RemoveGame(string id)
        {
            GameEntry g = Need(id);
            Session s;
            if (sessions.TryGetValue(id, out s)) { sessions.Remove(id); try { s.process.Dispose(); } catch { } }
            DeleteOwnCover(g);
            library.games.Remove(g);
            stats.Remove(id);
            releases.Remove(id);
            SaveLibrary();
            SaveStats();
            lastExists = ExistsSignature();
            return WithGames("id", id);
        }

        /// <summary>Supprime le visuel d'un jeu, uniquement s'il s'agit de notre copie dans covers\.</summary>
        void DeleteOwnCover(GameEntry g)
        {
            try
            {
                string p = ResolveImage(g);
                if (p != null && Path.GetFullPath(p).StartsWith(coversDir + "\\", StringComparison.OrdinalIgnoreCase)) File.Delete(p);
            }
            catch { }
        }

        string NewId(string name)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in name.ToLowerInvariant().Normalize(NormalizationForm.FormD))
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
                else if ((c == ' ' || c == '-' || c == '_') && sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');
            }
            string slug = sb.ToString().Trim('-');
            if (slug == "") slug = "jeu";
            if (slug.Length > 32) slug = slug.Substring(0, 32).Trim('-');
            string id = slug;
            for (int n = 2; Find(id) != null; n++) id = slug + "-" + n;
            return id;
        }

        // ================= Lancement et suivi des jeux =================

        object Launch(string id)
        {
            GameEntry g = Need(id);
            if (Updating(g.exe)) throw new InvalidOperationException("Une mise à jour est en cours. Attends qu'elle soit finie pour lancer ce jeu.");
            string state;
            Session s;
            if (!ExeExists(g)) state = "missing";
            else if (sessions.TryGetValue(id, out s) && Alive(s.process))
            {
                FocusProcess(s.process);
                state = "already";
            }
            else
            {
                Process open = FindProcess(g.exe);
                if (open != null)
                {
                    // Déjà ouvert (lancé hors du lanceur) : on n'en démarre pas un second, on y retourne.
                    if (Users(g.exe) == 1) Attach(g, open);
                    FocusProcess(open);
                    state = "already";
                }
                else
                {
                    Process started = Start(g);
                    Attach(g, started);
                    WatchDismiss(g, started);
                    StatsFor(id).launches++;
                    state = "started";
                }
            }
            SaveStats();
            return WithGames("state", state);
        }

        static Process Start(GameEntry g)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = g.exe;
            psi.Arguments = g.args ?? "";
            psi.WorkingDirectory = !string.IsNullOrEmpty(g.cwd) && Directory.Exists(g.cwd) ? g.cwd : Path.GetDirectoryName(g.exe);
            psi.UseShellExecute = false;   // nécessaire pour passer des variables d'environnement
            if (g.env != null)
                foreach (KeyValuePair<string, string> kv in g.env) psi.EnvironmentVariables[kv.Key] = kv.Value;
            try { return Process.Start(psi); }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode != 740) throw;
                // Le programme demande les droits administrateur : on passe par Windows (invite UAC).
                psi.UseShellExecute = true;
                return Process.Start(psi);
            }
        }

        /// <summary>
        /// Certains programmes posent la même question à chaque démarrage. Si le jeu a un champ "dismiss"
        /// (le titre exact de cette fenêtre), on la referme dès qu'elle apparaît : cela revient à la fermer
        /// à la main, donc à garder la réponse par défaut. On surveille 90 secondes après le lancement.
        /// </summary>
        void WatchDismiss(GameEntry g, Process p)
        {
            if (string.IsNullOrEmpty(g.dismiss) || p == null) return;
            string title = g.dismiss;
            uint pid;
            try { pid = (uint)p.Id; } catch { return; }
            DateTime until = DateTime.UtcNow.AddSeconds(90);
            System.Windows.Forms.Timer watch = new System.Windows.Forms.Timer();
            watch.Interval = 300;
            int attempts = 0;
            watch.Tick += delegate
            {
                bool found = false;
                Native.EnumWindows(delegate(IntPtr h, IntPtr l)
                {
                    uint owner;
                    Native.GetWindowThreadProcessId(h, out owner);
                    if (owner != pid || !Native.IsWindowVisible(h)) return true;
                    StringBuilder text = new StringBuilder(300);
                    Native.GetWindowText(h, text, text.Capacity);
                    if (!string.Equals(text.ToString(), title, StringComparison.OrdinalIgnoreCase)) return true;
                    found = true;
                    attempts++;
                    Native.PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero);   // WM_CLOSE : comme un clic sur la croix
                    if (attempts > 2)
                    {
                        // La fenêtre résiste à la croix : on essaie la touche Échap.
                        Native.PostMessage(h, 0x0100, (IntPtr)0x1B, IntPtr.Zero);
                        Native.PostMessage(h, 0x0101, (IntPtr)0x1B, IntPtr.Zero);
                    }
                    return false;
                }, IntPtr.Zero);
                // Fini quand la fenêtre a disparu après notre passage, ou au bout du délai.
                if ((attempts > 0 && !found) || attempts > 12 || DateTime.UtcNow > until) { watch.Stop(); watch.Dispose(); }
            };
            watch.Start();
        }

        void Attach(GameEntry g, Process p)
        {
            Session s = new Session();
            s.process = p;
            s.started = DateTime.UtcNow;
            s.tick = s.started;
            sessions[g.id] = s;
            StatsFor(g.id).last = s.started.ToString("o");
        }

        object FocusGame(string id)
        {
            GameEntry g = Need(id);
            Session s;
            bool ok = false;
            if (sessions.TryGetValue(id, out s)) ok = FocusProcess(s.process);
            if (!ok && ExeExists(g))
            {
                Process p = FindProcess(g.exe);
                if (p != null) ok = FocusProcess(p);
            }
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["ok"] = ok;
            return r;
        }

        object OpenFolder(string id)
        {
            GameEntry g = Need(id);
            if (ExeExists(g)) Process.Start("explorer.exe", "/select,\"" + g.exe + "\"");
            else
            {
                string dir = null;
                try { dir = Path.GetDirectoryName(g.exe); } catch { }
                if (dir == null || !Directory.Exists(dir)) throw new InvalidOperationException("Le dossier du jeu est introuvable.");
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            return null;
        }

        /// <summary>Nombre de jeux qui partagent cet exécutable (un émulateur peut en servir plusieurs).</summary>
        int Users(string exe)
        {
            int n = 0;
            foreach (GameEntry g in library.games)
                if (string.Equals(g.exe, exe, StringComparison.OrdinalIgnoreCase)) n++;
            return n;
        }

        static Process FindProcess(string exe)
        {
            string name;
            try { name = Path.GetFileNameWithoutExtension(exe); } catch { return null; }
            foreach (Process p in Process.GetProcessesByName(name))
            {
                bool match;
                try { match = string.Equals(p.MainModule.FileName, exe, StringComparison.OrdinalIgnoreCase); }
                catch { match = true; }   // processus protégé : on se fie au nom
                if (match) return p;
                p.Dispose();
            }
            return null;
        }

        static bool Alive(Process p)
        {
            try { return !p.HasExited; }
            catch
            {
                try { using (Process q = Process.GetProcessById(p.Id)) return q.ProcessName == p.ProcessName; }
                catch { return false; }
            }
        }

        static bool FocusProcess(Process p)
        {
            try
            {
                p.Refresh();
                IntPtr h = p.MainWindowHandle;
                if (h == IntPtr.Zero) return false;
                if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
                return Native.SetForegroundWindow(h);
            }
            catch { return false; }
        }

        /// <summary>Toutes les 2 s : compte le temps de jeu, repère les jeux ouverts ou fermés.</summary>
        void OnPoll(object sender, EventArgs e)
        {
            pollCount++;
            bool changed = false;
            DateTime now = DateTime.UtcNow;

            foreach (string id in new List<string>(sessions.Keys))
            {
                Session s = sessions[id];
                GameStats st = StatsFor(id);
                double dt = (now - s.tick).TotalSeconds;
                s.tick = now;
                if (dt > 0 && dt < 30) st.seconds += dt;   // au-delà : le PC était en veille, on ne compte pas
                st.last = now.ToString("o");
                if (!Alive(s.process))
                {
                    sessions.Remove(id);
                    try { s.process.Dispose(); } catch { }
                    changed = true;
                }
            }

            if (pollCount % 2 == 0)
            {
                // Jeu ouvert en dehors du lanceur : on le suit aussi, si son exécutable n'appartient qu'à lui.
                foreach (GameEntry g in library.games)
                {
                    if (sessions.ContainsKey(g.id) || !ExeExists(g) || Users(g.exe) != 1) continue;
                    Process p = FindProcess(g.exe);
                    if (p != null) { Attach(g, p); changed = true; }
                }
                string sig = ExistsSignature();
                if (sig != lastExists) { lastExists = sig; changed = true; }
            }

            bool periodic = sessions.Count > 0 && (now - lastPush).TotalSeconds >= 30;
            if (changed || periodic)
            {
                SaveStats();
                PushState();
            }
        }

        // ================= Fichiers JSON =================

        T ReadJson<T>(string file) where T : class, new()
        {
            try
            {
                if (File.Exists(file))
                {
                    T v = json.Deserialize<T>(File.ReadAllText(file, Encoding.UTF8));
                    if (v != null) return v;
                }
            }
            catch { }
            return new T();
        }

        void WriteJson(string file, object value)
        {
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, Pretty(json.Serialize(value)) + "\r\n", new UTF8Encoding(false));
            File.Copy(tmp, file, true);
            File.Delete(tmp);
        }

        /// <summary>Remet en forme un JSON compact (retours à la ligne et retraits).</summary>
        static string Pretty(string s)
        {
            StringBuilder sb = new StringBuilder();
            int depth = 0;
            bool inString = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inString)
                {
                    sb.Append(c);
                    if (c == '\\') sb.Append(s[++i]);
                    else if (c == '"') inString = false;
                    continue;
                }
                switch (c)
                {
                    case '"': inString = true; sb.Append(c); break;
                    case '{':
                    case '[':
                        sb.Append(c);
                        if (i + 1 < s.Length && (s[i + 1] == '}' || s[i + 1] == ']')) { sb.Append(s[++i]); break; }
                        depth++;
                        sb.Append("\r\n").Append(' ', depth * 2);
                        break;
                    case '}':
                    case ']':
                        depth--;
                        sb.Append("\r\n").Append(' ', depth * 2).Append(c);
                        break;
                    case ',': sb.Append(c).Append("\r\n").Append(' ', depth * 2); break;
                    case ':': sb.Append(": "); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        // ================= Mode test =================

        void HitTest(StringBuilder info, string label, int x, int y)
        {
            Point p = PointToScreen(new Point(x, y));
            IntPtr lp = (IntPtr)(((p.Y & 0xFFFF) << 16) | (p.X & 0xFFFF));
            Native.EnumChildWindows(Handle, delegate(IntPtr h, IntPtr l)
            {
                StringBuilder cls = new StringBuilder(128);
                Native.GetClassName(h, cls, 128);
                Native.RECT wr, rr;
                Native.GetWindowRect(h, out wr);
                int rgn = Native.GetWindowRgnBox(h, out rr);
                info.AppendLine("hit " + label + " " + cls + " = " + Native.SendMessage(h, 0x0084, IntPtr.Zero, lp)
                    + " visible=" + Native.IsWindowVisible(h) + " rect=" + wr.left + "," + wr.top + "," + wr.right + "," + wr.bottom
                    + " rgn(" + rgn + ")=" + rr.left + "," + rr.top + "," + rr.right + "," + rr.bottom);
                return true;
            }, IntPtr.Zero);
        }

        /// <summary>L'interface a fini son premier affichage. En mode test : capture puis fermeture.</summary>
        async void OnUiReady()
        {
            if (shot == null || shotDone) return;
            shotDone = true;
            StringBuilder info = new StringBuilder();
            try
            {
                await Task.Delay(1500 + shot.delay);
                if (shot.js != null)
                {
                    info.AppendLine("js=" + await core.ExecuteScriptAsync(shot.js));
                    await Task.Delay(900 + shot.after);
                }
                using (FileStream fs = File.Create(shot.file))
                    await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);

                info.AppendLine("runtime=" + core.Environment.BrowserVersionString);
                info.AppendLine("dpi=" + Native.GetDpiForWindow(Handle) + " state=" + WindowState);
                info.AppendLine("bounds=" + Bounds + " client=" + ClientSize + " clientOnScreen=" + RectangleToScreen(ClientRectangle));
                info.AppendLine("work=" + Screen.FromControl(this).WorkingArea);
                info.AppendLine("page=" + await core.ExecuteScriptAsync(
                    "JSON.stringify({w: innerWidth, h: innerHeight, dpr: devicePixelRatio, titre: (document.querySelector('#title')||{}).textContent," +
                    " cartes: document.querySelectorAll('.card[data-id]').length," +
                    " polices: Array.from(document.fonts).filter(function(f){return f.status==='loaded'}).map(function(f){return f.family}).join('|')})"));

                // Ce que Windows « voit » sous la souris : 2 = barre de titre (déplaçable), 1 = contenu.
                float k = Native.GetDpiForWindow(Handle) / 96f;
                HitTest(info, "barre", ClientSize.Width / 2, (int)(22 * k));
                HitTest(info, "bouton-fermer", ClientSize.Width - (int)(20 * k), (int)(22 * k));
                HitTest(info, "contenu", ClientSize.Width / 2, ClientSize.Height / 2);

                if (shot.screen != null)
                {
                    // Capture de l'écran autour de la fenêtre, pour contrôler le cadre dessiné par Windows.
                    TopMost = true;
                    await Task.Delay(900);
                    Rectangle r = Bounds;
                    r.Inflate(20, 20);
                    using (Bitmap bmp = new Bitmap(r.Width, r.Height))
                    {
                        using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                        bmp.Save(shot.screen, ImageFormat.Png);
                    }
                }
            }
            catch (Exception ex) { info.AppendLine("ERREUR " + ex); }
            try { File.WriteAllText(shot.file + ".txt", info.ToString()); } catch { }
            Close();
        }
    }
}
