// Mise à jour d'un émulateur depuis ses publications (« releases ») GitHub.
//
// Principe, pour un jeu qui a un dépôt GitHub dans games.json :
//   1. on demande à GitHub la dernière publication qui contient une archive Windows (.zip) ;
//   2. on télécharge l'archive et on vérifie son empreinte SHA-256 annoncée par GitHub ;
//   3. on l'extrait dans un dossier temporaire, à l'intérieur du dossier de l'émulateur ;
//   4. on met l'ancienne version de côté dans « _version-precedente », puis on installe la nouvelle.
// Le dossier « user » (clés, firmware, sauvegardes, réglages) n'est jamais déplacé ni écrasé.
// Si quoi que ce soit échoue pendant la bascule, l'ancienne version est remise en place.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Lanceur
{
    /// <summary>Réglage « mises à jour » d'un jeu (dans games.json).</summary>
    public class UpdateInfo
    {
        public string repo;          // dépôt GitHub « propriétaire/nom »
        public string channel;       // "versions" (étiquettes v1.2…) ou "builds" (tout, builds quotidiens compris)
        public string asset;         // facultatif : morceau du nom de fichier à préférer, ex. "x86_64.zip"
        public string installed;     // version installée par le lanceur ; vide tant qu'il n'a rien installé
        public string installedAt;
        public string previous;      // version d'avant la dernière mise à jour ("" = celle installée à la main)
        public string ignored;       // version à ne plus proposer
    }

    /// <summary>Dernière publication trouvée pour un jeu (mémorisée dans data\updates.json).</summary>
    public class ReleaseInfo
    {
        public string repo;
        public string channel;
        public string tag;
        public string name;
        public string publishedAt;
        public string notes;
        public string url;
        public string assetName;
        public string assetUrl;
        public long size;
        public string digest;
        public string checkedAt;
    }

    partial class MainForm
    {
        const string BackupDir = "_version-precedente";
        const string TempDir = "_maj-en-cours";
        const string SettingsCopy = "_reglages-user-config";

        Dictionary<string, ReleaseInfo> releases = new Dictionary<string, ReleaseInfo>();
        string jobGame;        // jeu dont la mise à jour est en cours, sinon null
        bool jobCancel;
        bool autoChecked;

        string UpdatesFile { get { return Path.Combine(dataDir, "updates.json"); } }

        void LoadReleases()
        {
            releases = ReadJson<Dictionary<string, ReleaseInfo>>(UpdatesFile);
        }

        void SaveReleases()
        {
            try { Directory.CreateDirectory(dataDir); WriteJson(UpdatesFile, releases); } catch { }
        }

        static bool HasRepo(GameEntry g)
        {
            return g.update != null && !string.IsNullOrEmpty(g.update.repo);
        }

        static string Channel(GameEntry g)
        {
            return g.update != null && g.update.channel == "builds" ? "builds" : "versions";
        }

        static string InstallDir(GameEntry g)
        {
            return Path.GetDirectoryName(Path.GetFullPath(g.exe));
        }

        static bool CanRestore(GameEntry g)
        {
            try { return ExeExists(g) && Directory.Exists(Path.Combine(InstallDir(g), BackupDir)); } catch { return false; }
        }

        /// <summary>Vrai si cet exécutable est en train d'être mis à jour (on ne le lance pas pendant ce temps).</summary>
        bool Updating(string exe)
        {
            GameEntry j = jobGame == null ? null : Find(jobGame);
            return j != null && string.Equals(j.exe, exe, StringComparison.OrdinalIgnoreCase);
        }

        // ---------- Ce que l'interface reçoit et ce qui part dans games.json ----------

        object PublicUpdate(GameEntry g)
        {
            if (!HasRepo(g)) return null;
            ReleaseInfo r;
            bool known = releases.TryGetValue(g.id, out r) && r != null && r.repo == g.update.repo
                && r.channel == Channel(g) && !string.IsNullOrEmpty(r.tag);

            Dictionary<string, object> d = new Dictionary<string, object>();
            d["repo"] = g.update.repo;
            d["channel"] = Channel(g);
            d["installed"] = g.update.installed;
            d["installedAt"] = g.update.installedAt;
            d["canRestore"] = CanRestore(g);
            d["busy"] = jobGame == g.id;
            d["available"] = known && r.tag != g.update.installed && r.tag != g.update.ignored;
            if (known)
            {
                Dictionary<string, object> l = new Dictionary<string, object>();
                l["tag"] = r.tag;
                l["name"] = r.name;
                l["publishedAt"] = r.publishedAt;
                l["notes"] = r.notes;
                l["url"] = r.url;
                l["file"] = r.assetName;
                l["size"] = r.size;
                l["checkedAt"] = r.checkedAt;
                d["latest"] = l;
            }
            else d["latest"] = null;
            return d;
        }

        static object SavedUpdate(GameEntry g)
        {
            if (!HasRepo(g)) return null;
            Dictionary<string, object> u = new Dictionary<string, object>();
            u["repo"] = g.update.repo;
            u["channel"] = Channel(g);
            if (!string.IsNullOrEmpty(g.update.asset)) u["asset"] = g.update.asset;
            if (!string.IsNullOrEmpty(g.update.installed)) u["installed"] = g.update.installed;
            if (!string.IsNullOrEmpty(g.update.installedAt)) u["installedAt"] = g.update.installedAt;
            if (g.update.previous != null) u["previous"] = g.update.previous;
            if (!string.IsNullOrEmpty(g.update.ignored)) u["ignored"] = g.update.ignored;
            return u;
        }

        /// <summary>Réglages venus de la fenêtre « Modifier » : dépôt (ou lien GitHub collé tel quel) et canal.</summary>
        void ApplyUpdateSettings(GameEntry g, string repoText, string channel)
        {
            string repo = NormalizeRepo(repoText);
            if (repo == "")
            {
                g.update = null;
                releases.Remove(g.id);
                return;
            }
            if (g.update == null) g.update = new UpdateInfo();
            channel = channel == "builds" ? "builds" : "versions";
            if (g.update.repo != repo)
            {
                // autre dépôt : ce qu'on savait de la version installée ne vaut plus
                g.update.installed = null; g.update.installedAt = null; g.update.previous = null; g.update.ignored = null; g.update.asset = null;
            }
            if (g.update.repo != repo || g.update.channel != channel) releases.Remove(g.id);
            g.update.repo = repo;
            g.update.channel = channel;
        }

        static string NormalizeRepo(string text)
        {
            text = (text ?? "").Trim();
            if (text == "") return "";
            Match m = Regex.Match(text, @"github\.com[/:]([\w.-]+)/([\w.-]+)", RegexOptions.IgnoreCase);
            if (!m.Success) m = Regex.Match(text, @"^([\w.-]+)/([\w.-]+)$");
            if (!m.Success) throw new InvalidOperationException("Dépôt GitHub non reconnu. Colle le lien de la page GitHub, ou écris « propriétaire/nom ».");
            string name = m.Groups[2].Value;
            if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            return m.Groups[1].Value + "/" + name;
        }

        // ---------- Recherche de la dernière version ----------

        /// <summary>Au démarrage : vérifie en arrière-plan, au plus une fois toutes les 6 heures par jeu.</summary>
        async void AutoCheckUpdates()
        {
            if (autoChecked || shot != null) return;
            autoChecked = true;
            bool changed = false;
            foreach (GameEntry g in new List<GameEntry>(library.games))
            {
                if (!HasRepo(g)) continue;
                ReleaseInfo r;
                DateTime last;
                if (releases.TryGetValue(g.id, out r) && r != null && r.repo == g.update.repo && r.channel == Channel(g)
                    && DateTime.TryParse(r.checkedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out last)
                    && (DateTime.UtcNow - last.ToUniversalTime()).TotalHours < 6) continue;
                try
                {
                    releases[g.id] = await FetchLatest(g.update.repo, Channel(g), g.update.asset);
                    changed = true;
                }
                catch { }   // pas de réseau, GitHub indisponible : on réessaiera au prochain démarrage
            }
            if (changed) { SaveReleases(); PushState(); }
        }

        async Task<object> CheckUpdate(string id)
        {
            GameEntry g = Need(id);
            if (!HasRepo(g)) throw new InvalidOperationException("Aucun dépôt GitHub n'est indiqué pour ce jeu.");
            releases[id] = await FetchLatest(g.update.repo, Channel(g), g.update.asset);
            SaveReleases();
            return WithGames("id", id);
        }

        async Task<ReleaseInfo> FetchLatest(string repo, string channel, string assetHint)
        {
            string text = await HttpGetString("https://api.github.com/repos/" + repo + "/releases?per_page=20");
            JavaScriptSerializer big = new JavaScriptSerializer();
            big.MaxJsonLength = 64 * 1024 * 1024;
            object[] list = big.DeserializeObject(text) as object[];
            if (list == null) throw new InvalidOperationException("Réponse inattendue de GitHub.");

            // Canal « versions » : uniquement les publications numérotées. Si le dépôt distingue versions
            // stables et préversions, on ne propose que les stables ; s'il marque tout en préversion
            // (c'est le cas de l'Eglantine officiel), on prend la plus récente des numérotées.
            bool versions = channel == "versions", anyStable = false;
            if (versions)
                foreach (object o in list)
                {
                    Dictionary<string, object> rel = o as Dictionary<string, object>;
                    if (rel != null && Str(rel, "draft") != "True" && Str(rel, "prerelease") != "True"
                        && IsVersionTag(Str(rel, "tag_name")) && PickAsset(rel, assetHint) != null) anyStable = true;
                }

            Dictionary<string, object> best = null, bestAsset = null;
            DateTime bestDate = DateTime.MinValue;
            foreach (object o in list)
            {
                Dictionary<string, object> rel = o as Dictionary<string, object>;
                if (rel == null || Str(rel, "draft") == "True") continue;
                if (versions && !IsVersionTag(Str(rel, "tag_name"))) continue;
                if (anyStable && Str(rel, "prerelease") == "True") continue;
                Dictionary<string, object> asset = PickAsset(rel, assetHint);
                if (asset == null) continue;
                DateTime when;
                DateTime.TryParse(Str(rel, "published_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out when);
                if (best == null || when > bestDate) { best = rel; bestAsset = asset; bestDate = when; }
            }
            if (best == null)
                throw new InvalidOperationException(channel == "versions"
                    ? "Ce dépôt n'a pas de version numérotée avec une archive Windows. Essaie « Tous les builds »."
                    : "Ce dépôt ne publie pas d'archive Windows (.zip).");

            ReleaseInfo r = new ReleaseInfo();
            r.repo = repo;
            r.channel = channel;
            r.tag = Str(best, "tag_name");
            r.name = Str(best, "name");
            r.publishedAt = Str(best, "published_at") == "" ? null : bestDate.ToString("o");
            r.notes = Summary(Str(best, "body"));
            r.url = Str(best, "html_url");
            r.assetName = Path.GetFileName(Str(bestAsset, "name"));
            r.assetUrl = Str(bestAsset, "browser_download_url");
            long size;
            long.TryParse(Str(bestAsset, "size"), out size);
            r.size = size;
            r.digest = Str(bestAsset, "digest");
            r.checkedAt = DateTime.UtcNow.ToString("o");
            return r;
        }

        /// <summary>
        /// Une version numérotée : l'étiquette contient un numéro à point, comme « v0.1.0-rc1 », « 1.2 »
        /// ou « zendnevs-v2.0.5 ». « daily-20261105 » et « build-212 » n'en sont pas.
        /// </summary>
        static bool IsVersionTag(string tag)
        {
            return !string.IsNullOrEmpty(tag) && Regex.IsMatch(tag, @"\d+\.\d+");
        }

        /// <summary>Choisit l'archive Windows d'une publication. Sans préférence, le build « pgo » quand il existe.</summary>
        static Dictionary<string, object> PickAsset(Dictionary<string, object> rel, string hint)
        {
            object[] assets = rel.ContainsKey("assets") ? rel["assets"] as object[] : null;
            if (assets == null) return null;
            Dictionary<string, object> best = null;
            int bestScore = -1;
            foreach (object o in assets)
            {
                Dictionary<string, object> a = o as Dictionary<string, object>;
                if (a == null) continue;
                string n = Str(a, "name").ToLowerInvariant();
                if (!n.EndsWith(".zip") || !n.Contains("win") || n.Contains("darwin")) continue;
                if (n.Contains("arm64") || n.Contains("aarch64") || n.Contains("source") || n.Contains("symbols") || n.Contains("debug")) continue;
                int score = 1;
                if (n.Contains("x86_64") || n.Contains("x64") || n.Contains("amd64")) score += 2;
                if (!string.IsNullOrEmpty(hint)) { if (n.Contains(hint.ToLowerInvariant())) score += 10; }
                else if (n.Contains("pgo")) score += 1;
                if (score > bestScore) { best = a; bestScore = score; }
            }
            return best;
        }

        /// <summary>Le début des notes de version, sans les tableaux de téléchargement par système.</summary>
        static string Summary(string body)
        {
            string t = (body ?? "").Replace("\r", "");
            foreach (string stop in new string[] { "\n## Linux", "\n## Windows", "\n## macOS", "\n## Android", "\n| " })
            {
                int i = t.IndexOf(stop, StringComparison.OrdinalIgnoreCase);
                if (i >= 0) t = t.Substring(0, i);
            }
            t = Regex.Replace(t, @"^\s*#+\s*", "", RegexOptions.Multiline);          // titres
            t = Regex.Replace(t, @"^\s*>\s?", "", RegexOptions.Multiline);            // citations
            t = Regex.Replace(t, @"\*\*|`", "");                                       // gras, code
            t = Regex.Replace(t, @"\[([^\]]+)\]\([^)]+\)", "$1");                      // liens
            t = Regex.Replace(t, @"\n{3,}", "\n\n").Trim();
            if (t.Length > 1400) t = t.Substring(0, 1400).TrimEnd() + "…";
            return t;
        }

        // ---------- Réseau ----------

        static HttpWebRequest NewRequest(string url, string accept)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = Brand.Name + "-lanceur";
            req.Accept = accept;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            return req;
        }

        /// <summary>Attend la réponse, 25 s au plus (les délais intégrés ne s'appliquent pas aux appels asynchrones).</summary>
        static async Task<WebResponse> GetResponse(HttpWebRequest req)
        {
            Task<WebResponse> answer = Task.Factory.FromAsync<WebResponse>(req.BeginGetResponse, req.EndGetResponse, null);
            if (await Task.WhenAny(answer, Task.Delay(25000)) != answer)
            {
                req.Abort();
                throw new InvalidOperationException("GitHub ne répond pas. Vérifie ta connexion, puis réessaie.");
            }
            try { return await answer; }
            catch (WebException ex) { throw new InvalidOperationException(Explain(ex)); }
        }

        static string Explain(WebException ex)
        {
            HttpWebResponse res = ex.Response as HttpWebResponse;
            if (res == null) return "Pas de connexion à GitHub (" + ex.Status + ").";
            int code = (int)res.StatusCode;
            if (code == 404) return "Dépôt introuvable sur GitHub. Vérifie « propriétaire/nom ».";
            if (code == 403 || code == 429) return "GitHub limite le nombre de vérifications depuis ta connexion. Réessaie dans une heure.";
            return "GitHub a répondu par une erreur (" + code + ").";
        }

        static async Task<string> HttpGetString(string url)
        {
            using (WebResponse res = await GetResponse(NewRequest(url, "application/vnd.github+json")))
            using (StreamReader reader = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
                return await reader.ReadToEndAsync();
        }

        // ---------- Mise à jour ----------

        void Progress(string id, string phase, long received, long total, string tag, string message)
        {
            Dictionary<string, object> m = new Dictionary<string, object>();
            m["event"] = "update";
            m["id"] = id;
            m["phase"] = phase;          // download, verify, install, done, error, cancelled
            m["received"] = received;
            m["total"] = total;
            m["tag"] = tag;
            m["message"] = message;
            Post(m);
        }

        object StartUpdate(string id)
        {
            GameEntry g = Need(id);
            if (jobGame != null) throw new InvalidOperationException("Une mise à jour est déjà en cours.");
            ReleaseInfo r;
            if (!HasRepo(g) || !releases.TryGetValue(id, out r) || r == null || string.IsNullOrEmpty(r.assetUrl))
                throw new InvalidOperationException("Vérifie d'abord s'il existe une mise à jour.");
            if (!ExeExists(g)) throw new InvalidOperationException("L'exécutable est introuvable : impossible de savoir où installer.");
            using (Process open = FindProcess(g.exe))
                if (open != null) throw new InvalidOperationException(Path.GetFileName(g.exe) + " est ouvert. Ferme-le, puis relance la mise à jour.");

            jobGame = id;
            jobCancel = false;
            RunUpdate(g, r);
            return WithGames("id", id);
        }

        async void RunUpdate(GameEntry g, ReleaseInfo r)
        {
            string download = Path.Combine(dataDir, "telechargements", r.assetName);
            string failure = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(download));
                await Download(g.id, r, download);

                Progress(g.id, "install", 0, 0, r.tag, null);
                string install = InstallDir(g), exeName = Path.GetFileName(g.exe);
                await Task.Run(new Action(delegate { Install(download, install, exeName); }));

                g.update.previous = g.update.installed ?? "";
                g.update.installed = r.tag;
                g.update.installedAt = DateTime.UtcNow.ToString("o");
                g.update.ignored = null;
                SaveLibrary();
            }
            catch (Exception ex) { failure = jobCancel || ex is OperationCanceledException ? "" : ex.Message; }

            try { if (File.Exists(download)) File.Delete(download); } catch { }
            jobGame = null;
            if (failure == null) Progress(g.id, "done", 0, 0, r.tag, null);
            else if (failure == "") Progress(g.id, "cancelled", 0, 0, r.tag, null);
            else Progress(g.id, "error", 0, 0, r.tag, failure);
            PushState();
        }

        async Task Download(string id, ReleaseInfo r, string file)
        {
            HttpWebRequest req = NewRequest(r.assetUrl, "application/octet-stream");
            using (WebResponse res = await GetResponse(req))
            using (Stream input = res.GetResponseStream())
            using (FileStream output = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
            using (SHA256 sha = SHA256.Create())
            {
                long total = res.ContentLength > 0 ? res.ContentLength : r.size, done = 0;
                byte[] buffer = new byte[1 << 17];
                DateTime shown = DateTime.MinValue;
                Progress(id, "download", 0, total, r.tag, null);
                while (true)
                {
                    if (jobCancel) { req.Abort(); throw new OperationCanceledException(); }
                    Task<int> read = input.ReadAsync(buffer, 0, buffer.Length);
                    if (await Task.WhenAny(read, Task.Delay(30000)) != read)
                    {
                        req.Abort();
                        throw new InvalidOperationException("Le téléchargement s'est interrompu. Rien n'a été installé.");
                    }
                    int n = await read;
                    if (n <= 0) break;
                    await output.WriteAsync(buffer, 0, n);
                    sha.TransformBlock(buffer, 0, n, null, 0);
                    done += n;
                    if ((DateTime.UtcNow - shown).TotalMilliseconds > 120)
                    {
                        shown = DateTime.UtcNow;
                        Progress(id, "download", done, total, r.tag, null);
                    }
                }
                sha.TransformFinalBlock(buffer, 0, 0);
                Progress(id, "verify", done, total, r.tag, null);

                if (r.size > 0 && done != r.size)
                    throw new InvalidOperationException("Téléchargement incomplet. Rien n'a été installé.");
                string hex = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                if (!string.IsNullOrEmpty(r.digest) && r.digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                    && r.digest.Substring(7).ToLowerInvariant() != hex)
                    throw new InvalidOperationException("Le fichier reçu ne correspond pas à l'empreinte publiée par GitHub. Rien n'a été installé.");
            }
        }

        static bool IsKept(string name)
        {
            return string.Equals(name, "user", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(BackupDir, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, TempDir, StringComparison.OrdinalIgnoreCase);
        }

        static void MoveEntry(string from, string to)
        {
            if (Directory.Exists(from)) Directory.Move(from, to); else File.Move(from, to);
        }

        static void DeleteEntry(string path)
        {
            if (Directory.Exists(path)) Directory.Delete(path, true); else if (File.Exists(path)) File.Delete(path);
        }

        static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            foreach (string d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
        }

        /// <summary>Le dossier de l'archive qui contient le programme : la racine, ou un sous-dossier.</summary>
        static string FolderWith(string dir, string fileName, int depth)
        {
            if (File.Exists(Path.Combine(dir, fileName))) return dir;
            if (depth <= 0) return null;
            foreach (string sub in Directory.GetDirectories(dir))
            {
                string found = FolderWith(sub, fileName, depth - 1);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>Extrait l'archive puis remplace l'ancienne version. Ne touche jamais au dossier « user ».</summary>
        static void Install(string zip, string install, string exeName)
        {
            string temp = Path.Combine(install, TempDir);
            string backup = Path.Combine(install, BackupDir), olderBackup = backup + "-a-supprimer";

            // 1. Extraction dans le dossier de l'émulateur lui-même : même disque, donc bascule instantanée.
            DeleteEntry(temp);
            Directory.CreateDirectory(temp);
            string root = Path.GetFullPath(temp).TrimEnd('\\') + "\\";
            using (ZipArchive archive = ZipFile.OpenRead(zip))
                foreach (ZipArchiveEntry e in archive.Entries)
                {
                    string dest = Path.GetFullPath(Path.Combine(temp, e.FullName));
                    if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Archive refusée : elle contient un chemin qui sort de son dossier.");
                    if (e.FullName.EndsWith("/") || e.FullName.EndsWith("\\")) { Directory.CreateDirectory(dest); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    e.ExtractToFile(dest, true);
                }

            string fresh = FolderWith(temp, exeName, 3);
            if (fresh == null)
            {
                DeleteEntry(temp);
                throw new InvalidOperationException("L'archive ne contient pas " + exeName + ". Rien n'a été modifié.");
            }

            // 2. Bascule : l'ancienne version part dans la sauvegarde, la nouvelle prend sa place.
            DeleteEntry(olderBackup);
            if (Directory.Exists(backup)) Directory.Move(backup, olderBackup);
            Directory.CreateDirectory(backup);
            List<string> movedOut = new List<string>(), movedIn = new List<string>();
            try
            {
                foreach (string path in Directory.GetFileSystemEntries(install))
                {
                    string name = Path.GetFileName(path);
                    if (IsKept(name)) continue;
                    MoveEntry(path, Path.Combine(backup, name));
                    movedOut.Add(name);
                }
                string settings = Path.Combine(install, "user", "config");
                if (Directory.Exists(settings)) CopyDir(settings, Path.Combine(backup, SettingsCopy));

                foreach (string path in Directory.GetFileSystemEntries(fresh))
                {
                    string name = Path.GetFileName(path);
                    if (IsKept(name)) continue;
                    MoveEntry(path, Path.Combine(install, name));
                    movedIn.Add(name);
                }
            }
            catch
            {
                // Un fichier a résisté : on défait tout pour retrouver l'état de départ.
                foreach (string name in movedIn) { try { DeleteEntry(Path.Combine(install, name)); } catch { } }
                foreach (string name in movedOut) { try { MoveEntry(Path.Combine(backup, name), Path.Combine(install, name)); } catch { } }
                try { DeleteEntry(backup); } catch { }
                try { if (Directory.Exists(olderBackup)) Directory.Move(olderBackup, backup); } catch { }
                try { DeleteEntry(temp); } catch { }
                throw new InvalidOperationException("Un fichier de l'émulateur est utilisé par un autre programme. Rien n'a été modifié : ferme l'émulateur et ses fenêtres, puis réessaie.");
            }
            try { DeleteEntry(temp); } catch { }
            try { DeleteEntry(olderBackup); } catch { }
        }

        /// <summary>Remet la version d'avant la dernière mise à jour, avec ses réglages.</summary>
        static void Restore(string install)
        {
            string backup = Path.Combine(install, BackupDir), discard = Path.Combine(install, TempDir);
            DeleteEntry(discard);
            Directory.CreateDirectory(discard);
            List<string> movedOut = new List<string>(), movedIn = new List<string>();
            try
            {
                foreach (string path in Directory.GetFileSystemEntries(install))
                {
                    string name = Path.GetFileName(path);
                    if (IsKept(name)) continue;
                    MoveEntry(path, Path.Combine(discard, name));
                    movedOut.Add(name);
                }
                foreach (string path in Directory.GetFileSystemEntries(backup))
                {
                    string name = Path.GetFileName(path);
                    if (name == SettingsCopy) continue;
                    MoveEntry(path, Path.Combine(install, name));
                    movedIn.Add(name);
                }
            }
            catch
            {
                foreach (string name in movedIn) { try { MoveEntry(Path.Combine(install, name), Path.Combine(backup, name)); } catch { } }
                foreach (string name in movedOut) { try { MoveEntry(Path.Combine(discard, name), Path.Combine(install, name)); } catch { } }
                try { DeleteEntry(discard); } catch { }
                throw new InvalidOperationException("Un fichier de l'émulateur est utilisé par un autre programme. La version actuelle est restée en place : ferme l'émulateur, puis réessaie.");
            }
            string settings = Path.Combine(backup, SettingsCopy);
            if (Directory.Exists(settings)) CopyDir(settings, Path.Combine(install, "user", "config"));
            try { DeleteEntry(backup); } catch { }
            try { DeleteEntry(discard); } catch { }
        }

        async Task<object> RestoreUpdate(string id)
        {
            GameEntry g = Need(id);
            if (jobGame != null) throw new InvalidOperationException("Une mise à jour est en cours.");
            if (!CanRestore(g)) throw new InvalidOperationException("Il n'y a pas de version précédente à remettre.");
            using (Process open = FindProcess(g.exe))
                if (open != null) throw new InvalidOperationException(Path.GetFileName(g.exe) + " est ouvert. Ferme-le, puis réessaie.");

            string install = InstallDir(g);
            jobGame = id;
            try { await Task.Run(new Action(delegate { Restore(install); })); }
            finally { jobGame = null; }

            if (g.update != null)
            {
                g.update.ignored = g.update.installed;   // on ne repropose pas d'office la version qu'on vient de quitter
                g.update.installed = string.IsNullOrEmpty(g.update.previous) ? null : g.update.previous;
                g.update.installedAt = null;
                g.update.previous = null;
                SaveLibrary();
            }
            return WithGames("id", id);
        }

        object IgnoreUpdate(string id)
        {
            GameEntry g = Need(id);
            ReleaseInfo r;
            if (HasRepo(g) && releases.TryGetValue(id, out r) && r != null)
            {
                g.update.ignored = r.tag;
                SaveLibrary();
            }
            return WithGames("id", id);
        }

        static object OpenUrl(string url)
        {
            // Uniquement des pages GitHub, ouvertes dans le navigateur habituel.
            if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Lien refusé.");
            Process.Start(url);
            return null;
        }
    }
}
