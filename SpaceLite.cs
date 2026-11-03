// SpaceLite - analyseur d'espace disque (treemap + details), leger et portable.
// Cible : C# 5 / .NET Framework 4 (compilable avec le csc.exe fourni par Windows).
// Un seul fichier volontairement : facile a lire, a modifier et a compiler.
//
// SpaceLite est un logiciel independant (licence MIT, voir LICENSE).
// Certaines parties (disposition par bissection equilibree, seuils de densite, palettes de couleurs, rendu
// biseaute) derivent de SpaceMonger 1.x de Sean Werkema, MIT, (c) 1998-2020
// (https://github.com/seanofw/spacemonger1) : voir THIRD-PARTY-NOTICES.md et LICENSE-SpaceMonger.txt.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("SpaceLite")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyProduct("SpaceLite")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace SpaceLite
{
    // ------------------------------------------------------------------
    // Modele
    // ------------------------------------------------------------------
    class Node
    {
        public string Name;
        public string FullPath;
        public long Size;
        public int FileCount;      // nombre de fichiers (1 pour un fichier)
        public int DirCount;       // nombre de sous-dossiers (recursif)
        public bool IsDir;
        public bool IsFree;        // bloc virtuel "espace libre"
        public bool IsUncounted;   // bloc virtuel "non compte" (avec IsFree = true)
        public DateTime Modified;  // date de modification (fichiers)
        public Node Parent;
        public List<Node> Children = new List<Node>();
    }

    class Scanner
    {
        public volatile bool Cancel;
        public int FilesSeen;      // lu par le thread UI (valeur approximative suffisante)
        public string Current;

        // Diagnostic (affiche par le bouton "Diagnostic")
        public int UnreadCount;                                   // dossiers non lus (acces refuse, erreur d'enumeration...)
        public List<string> Unread = new List<string>();          // les 300 premiers
        public Dictionary<string, int> UnreadByParent = new Dictionary<string, int>();   // tous, regroupes par dossier parent
        public int SkippedCount;                                  // liens ignores (symlink / jonction)
        public List<string> SkippedLinks = new List<string>();    // les 40 premiers
        public Dictionary<uint, int> TagCount = new Dictionary<uint, int>();     // points de reparse rencontres, par balise
        public Dictionary<uint, long> TagBytes = new Dictionary<uint, long>();   // taille des fichiers comptes, par balise

        public const uint TagSymlink = 0xA000000C;
        public const uint TagMount = 0xA0000003;   // jonction ou point de montage

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WIN32_FIND_DATA
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;   // balise du point de reparse
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindFirstFileW(string lpFileName, out WIN32_FIND_DATA lpFindFileData);

        [DllImport("kernel32.dll")]
        static extern bool FindClose(IntPtr hFindFile);

        // Balise du point de reparse d'un element (0 si inconnue). Appelee seulement pour les rares elements a point de reparse.
        static uint ReparseTag(string path)
        {
            try
            {
                WIN32_FIND_DATA d;
                IntPtr h = FindFirstFileW(path, out d);
                if (h == new IntPtr(-1)) return 0;
                FindClose(h);
                return d.dwReserved0;
            }
            catch (Exception) { return 0; }
        }

        public static string TagName(uint tag)
        {
            if (tag == TagSymlink) return "lien symbolique";
            if (tag == TagMount) return "jonction / point de montage";
            if ((tag & 0xFFFF0FFF) == 0x9000001A) return "OneDrive (fichiers a la demande)";
            if (tag == 0x80000017) return "compression WOF";
            if (tag == 0x80000013) return "deduplication";
            if (tag == 0x8000001B) return "alias d'application (AppExecLink)";
            if (tag == 0) return "balise inconnue";
            return "autre";
        }

        void AddUnread(string path)
        {
            UnreadCount++;
            if (Unread.Count < 300) Unread.Add(path);
            string parent = null;
            try { parent = System.IO.Path.GetDirectoryName(path); } catch (Exception) { }
            if (string.IsNullOrEmpty(parent)) parent = path;
            int c;
            UnreadByParent.TryGetValue(parent, out c);
            UnreadByParent[parent] = c + 1;
        }

        public Node ScanRoot(string path)
        {
            Node root = ScanDir(path, null, path);
            SortTree(root);
            return root;
        }

        Node ScanDir(string path, Node parent, string name)
        {
            Node n = new Node();
            n.Name = name;
            n.FullPath = path;
            n.IsDir = true;
            n.Parent = parent;
            Current = path;

            IEnumerator<FileSystemInfo> en;
            try { en = new DirectoryInfo(path).EnumerateFileSystemInfos().GetEnumerator(); }
            catch (Exception) { AddUnread(path); return n; }

            using (en)
            {
                while (!Cancel)
                {
                    FileSystemInfo fi;
                    try
                    {
                        if (!en.MoveNext()) break;
                        fi = en.Current;
                    }
                    catch (Exception) { AddUnread(path); break; }   // on garde ce qui a ete lu avant l'erreur

                    try
                    {
                        bool isDir = (fi.Attributes & FileAttributes.Directory) != 0;

                        // Comme SpaceMonger : seuls les liens symboliques et les jonctions sont ignores.
                        // Les autres points de reparse (OneDrive, WOF, deduplication...) sont comptes normalement.
                        if ((fi.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            uint tag = ReparseTag(fi.FullName);
                            long len = 0;
                            if (!isDir) { try { len = ((FileInfo)fi).Length; } catch (Exception) { } }
                            int cnt;
                            TagCount.TryGetValue(tag, out cnt);
                            TagCount[tag] = cnt + 1;
                            if (tag == TagSymlink || tag == TagMount || (tag == 0 && isDir))
                            {
                                SkippedCount++;
                                if (SkippedLinks.Count < 40) SkippedLinks.Add(fi.FullName + "  [" + TagName(tag) + "]");
                                continue;
                            }
                            if (!isDir)
                            {
                                long tb;
                                TagBytes.TryGetValue(tag, out tb);
                                TagBytes[tag] = tb + len;
                            }
                        }

                        if (isDir)
                        {
                            Node c = ScanDir(fi.FullName, n, fi.Name);
                            n.Children.Add(c);
                            n.Size += c.Size;
                            n.FileCount += c.FileCount;
                            n.DirCount += c.DirCount + 1;
                        }
                        else
                        {
                            Node f = new Node();
                            f.Name = fi.Name;
                            f.FullPath = fi.FullName;
                            f.IsDir = false;
                            f.Parent = n;
                            f.FileCount = 1;
                            try { f.Modified = fi.LastWriteTime; } catch (Exception) { }
                            try { f.Size = ((FileInfo)fi).Length; } catch (Exception) { f.Size = 0; }
                            n.Children.Add(f);
                            n.Size += f.Size;
                            n.FileCount++;
                            FilesSeen++;
                        }
                    }
                    catch (Exception) { }   // un element illisible n'interrompt pas le dossier
                }
            }
            return n;
        }

        static void SortTree(Node n)
        {
            n.Children.Sort(delegate(Node a, Node b) { return b.Size.CompareTo(a.Size); });
            foreach (Node c in n.Children)
                if (c.IsDir) SortTree(c);
        }
    }

    // ------------------------------------------------------------------
    // Parametres (fichier SpaceLite.ini a cote de l'exe => portable)
    // ------------------------------------------------------------------
    class Settings
    {
        public int MaxDepth = 8;
        public int MinCell = 14;      // taille mini (px) d'une case pour etre dessinee
        public bool ShowLabels = true;
        public bool ShowSizes = true;
        public bool ShowDates = true;
        public bool FreeSpace = false;
        public bool ColorByType = false;       // ancien reglage : migre vers FileColor = 12
        public int LayoutMode = 0;             // 0 bissection equilibree (SpaceMonger), 1 squarified
        public int Density = 0;                // -3 .. 2 (seuils de cases de SpaceMonger)
        public int Bias = 0;                   // -20 .. 20 (negatif : cases plus hautes ; positif : plus larges)
        public int FileColor = 0;              // 0..12 (12 = par type de fichier)
        public int FolderColor = 0;            // 0..11
        public bool ShowUncounted = true;      // bloc "non compte" (utilise - compte) a la racine d'un lecteur
        public bool Rollover = false;          // surbrillance de la chaine de cases sous le curseur
        public bool ShowNameTips = true;
        public int NameTipDelay = 125;         // ms
        public bool ShowInfoTips = true;
        public int InfoTipDelay = 250;         // ms
        public int InfoFlags = 24;             // 1 chemin, 2 nom, 8 date, 16 taille, 32 attributs
        public bool SavePos = false;           // memoriser position et etat de la fenetre
        public bool ReopenLast = false;        // rouvrir le dernier dossier au demarrage
        public bool HasPos = false;
        public int WinX, WinY;
        public int WinState;                   // 0 normal, 2 agrandie
        public string LastPath = "";
        public int Width = 1200;
        public int Height = 720;
        public bool ShowPanel = false;         // panneau de details (arborescence, gros fichiers, types, age, recherche)
        public int PanelWidth = 380;

        static string IniPath
        {
            get { return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SpaceLite.ini"); }
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            bool fileColorSeen = false;
            try
            {
                if (!File.Exists(IniPath)) return s;
                foreach (string line in File.ReadAllLines(IniPath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "MaxDepth": int.TryParse(v, out s.MaxDepth); break;
                        case "ShowLabels": s.ShowLabels = (v == "1"); break;
                        case "ShowSizes": s.ShowSizes = (v == "1"); break;
                        case "MinCell": int.TryParse(v, out s.MinCell); break;
                        case "ShowUncounted": s.ShowUncounted = (v == "1"); break;
                        case "Rollover": s.Rollover = (v == "1"); break;
                        case "ShowNameTips": s.ShowNameTips = (v == "1"); break;
                        case "NameTipDelay": int.TryParse(v, out s.NameTipDelay); break;
                        case "ShowInfoTips": s.ShowInfoTips = (v == "1"); break;
                        case "InfoTipDelay": int.TryParse(v, out s.InfoTipDelay); break;
                        case "InfoFlags": int.TryParse(v, out s.InfoFlags); break;
                        case "SavePos": s.SavePos = (v == "1"); break;
                        case "ReopenLast": s.ReopenLast = (v == "1"); break;
                        case "WinX": if (int.TryParse(v, out s.WinX)) s.HasPos = true; break;
                        case "WinY": int.TryParse(v, out s.WinY); break;
                        case "WinState": int.TryParse(v, out s.WinState); break;
                        case "ShowDates": s.ShowDates = (v == "1"); break;
                        case "FreeSpace": s.FreeSpace = (v == "1"); break;
                        case "LayoutMode": int.TryParse(v, out s.LayoutMode); break;
                        case "Density": int.TryParse(v, out s.Density); break;
                        case "Bias": int.TryParse(v, out s.Bias); break;
                        case "FileColor": fileColorSeen = int.TryParse(v, out s.FileColor); break;
                        case "FolderColor": int.TryParse(v, out s.FolderColor); break;
                        case "ColorByType": s.ColorByType = (v == "1"); break;
                        case "LastPath": s.LastPath = v; break;
                        case "ShowPanel": s.ShowPanel = (v == "1"); break;
                        case "PanelWidth": int.TryParse(v, out s.PanelWidth); break;
                        case "Width": int.TryParse(v, out s.Width); break;
                        case "Height": int.TryParse(v, out s.Height); break;
                    }
                }
            }
            catch (Exception) { }
            if (!fileColorSeen && s.ColorByType) s.FileColor = 12;
            s.LayoutMode = Math.Max(0, Math.Min(1, s.LayoutMode));
            s.Density = Math.Max(-3, Math.Min(2, s.Density));
            s.Bias = Math.Max(-20, Math.Min(20, s.Bias));
            s.FileColor = Math.Max(0, Math.Min(12, s.FileColor));
            s.FolderColor = Math.Max(0, Math.Min(11, s.FolderColor));
            if (s.MaxDepth < 1) s.MaxDepth = 1;
            if (s.MinCell < 2) s.MinCell = 2;
            if (s.NameTipDelay < 1) s.NameTipDelay = 1;
            if (s.InfoTipDelay < 1) s.InfoTipDelay = 1;
            if (s.PanelWidth < 200) s.PanelWidth = 200;
            if (s.Width < 400) s.Width = 400;
            if (s.Height < 300) s.Height = 300;
            return s;
        }

        // Texte du .ini : uniquement les reglages utiles. Position/taille de la fenetre et dernier dossier
        // ne sont ecrits que si l'option correspondante est cochee.
        string Serialize()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("MaxDepth=" + MaxDepth);
            sb.AppendLine("ShowLabels=" + (ShowLabels ? "1" : "0"));
            sb.AppendLine("ShowSizes=" + (ShowSizes ? "1" : "0"));
            sb.AppendLine("MinCell=" + MinCell);
            sb.AppendLine("ShowUncounted=" + (ShowUncounted ? "1" : "0"));
            sb.AppendLine("Rollover=" + (Rollover ? "1" : "0"));
            sb.AppendLine("ShowNameTips=" + (ShowNameTips ? "1" : "0"));
            sb.AppendLine("NameTipDelay=" + NameTipDelay);
            sb.AppendLine("ShowInfoTips=" + (ShowInfoTips ? "1" : "0"));
            sb.AppendLine("InfoTipDelay=" + InfoTipDelay);
            sb.AppendLine("InfoFlags=" + InfoFlags);
            sb.AppendLine("SavePos=" + (SavePos ? "1" : "0"));
            sb.AppendLine("ReopenLast=" + (ReopenLast ? "1" : "0"));
            sb.AppendLine("ShowDates=" + (ShowDates ? "1" : "0"));
            sb.AppendLine("FreeSpace=" + (FreeSpace ? "1" : "0"));
            sb.AppendLine("LayoutMode=" + LayoutMode);
            sb.AppendLine("Density=" + Density);
            sb.AppendLine("Bias=" + Bias);
            sb.AppendLine("FileColor=" + FileColor);
            sb.AppendLine("FolderColor=" + FolderColor);
            sb.AppendLine("ShowPanel=" + (ShowPanel ? "1" : "0"));
            sb.AppendLine("PanelWidth=" + PanelWidth);
            if (SavePos)
            {
                if (HasPos)
                {
                    sb.AppendLine("WinX=" + WinX);
                    sb.AppendLine("WinY=" + WinY);
                }
                sb.AppendLine("WinState=" + WinState);
                sb.AppendLine("Width=" + Width);
                sb.AppendLine("Height=" + Height);
            }
            if (ReopenLast) sb.AppendLine("LastPath=" + LastPath);
            return sb.ToString();
        }

        // Aucun fichier n'est cree tant que les reglages sont ceux par defaut ; un .ini devenu inutile est supprime.
        public void Save()
        {
            try
            {
                string text = Serialize();
                if (text == new Settings().Serialize())
                {
                    if (File.Exists(IniPath)) File.Delete(IniPath);
                    return;
                }
                File.WriteAllText(IniPath, text, Encoding.UTF8);
            }
            catch (Exception) { }
        }
    }

    // ------------------------------------------------------------------
    // Utilitaires
    // ------------------------------------------------------------------
    static class Util
    {
        public static string FormatSize(long b)
        {
            string[] u = { "o", "Ko", "Mo", "Go", "To" };
            double v = b;
            int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return (i == 0 ? v.ToString("F0") : v.ToString("F1")) + " " + u[i];
        }

        // Categories de couleurs. Pour en ajouter : completer Categories + CategoryColors.
        public static readonly string[] CategoryNames =
            { "Autre", "Images", "Video", "Audio", "Archives / disques", "Documents", "Executables", "Code" };

        public static readonly Color[] CategoryColors =
        {
            Color.FromArgb(200, 200, 200),  // Autre
            Color.FromArgb(255, 170, 90),   // Images
            Color.FromArgb(255, 110, 110),  // Video
            Color.FromArgb(200, 130, 255),  // Audio
            Color.FromArgb(90, 220, 190),   // Archives
            Color.FromArgb(110, 170, 255),  // Documents
            Color.FromArgb(130, 230, 110),  // Executables
            Color.FromArgb(240, 220, 70)    // Code
        };

        static readonly Dictionary<string, int> ExtMap = BuildExtMap();

        static Dictionary<string, int> BuildExtMap()
        {
            Dictionary<string, int> d = new Dictionary<string, int>();
            Add(d, 1, "jpg jpeg png gif bmp tif tiff webp ico svg heic raw psd");
            Add(d, 2, "mp4 mkv avi mov wmv flv webm m4v mpg mpeg ts");
            Add(d, 3, "mp3 wav flac aac ogg m4a wma opus");
            Add(d, 4, "zip rar 7z tar gz bz2 xz iso cab wim vhd vhdx vmdk img bak");
            Add(d, 5, "doc docx xls xlsx ppt pptx pdf txt odt ods csv md rtf");
            Add(d, 6, "exe dll sys msi msu ocx");
            Add(d, 7, "cs py js html htm css json xml c cpp h java php sh bat ps1 sql log");
            return d;
        }

        static void Add(Dictionary<string, int> d, int cat, string exts)
        {
            foreach (string e in exts.Split(' ')) d[e] = cat;
        }

        public static int CategoryOf(string fileName)
        {
            string ext = System.IO.Path.GetExtension(fileName);
            if (string.IsNullOrEmpty(ext)) return 0;
            int cat;
            if (ExtMap.TryGetValue(ext.Substring(1).ToLowerInvariant(), out cat)) return cat;
            return 0;
        }
    }

    // ------------------------------------------------------------------
    // Icones de la barre d'outils (dessinees en GDI+, aucun fichier externe)
    // ------------------------------------------------------------------
    static class Icons
    {
        public const int Size = 20;

        static readonly Color Dark = Color.FromArgb(60, 70, 85);
        static readonly Color Blue = Color.FromArgb(30, 110, 200);
        static readonly Color Red = Color.FromArgb(205, 50, 50);
        static readonly Color Green = Color.FromArgb(40, 160, 70);
        static readonly Color Yellow = Color.FromArgb(245, 195, 60);
        static readonly Color YellowD = Color.FromArgb(185, 135, 20);

        static Pen P(Color c, float w)
        {
            Pen p = new Pen(c, w);
            p.StartCap = LineCap.Round;
            p.EndCap = LineCap.Round;
            p.LineJoin = LineJoin.Round;
            return p;
        }

        public static Bitmap Make(string kind)
        {
            Bitmap bmp = new Bitmap(Size, Size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                Draw(g, kind);
            }
            return bmp;
        }

        static void Magnifier(Graphics g)
        {
            using (Pen p = P(Dark, 1.8f)) g.DrawEllipse(p, 2.5f, 2.5f, 10, 10);
            using (Pen p = P(Dark, 2.6f)) g.DrawLine(p, 11.5f, 11.5f, 17, 17);
        }

        static void Badge(Graphics g, Color c, bool plus)
        {
            using (Brush b = new SolidBrush(c)) g.FillEllipse(b, 10, 10, 9, 9);
            using (Pen p = P(Color.White, 1.6f))
            {
                g.DrawLine(p, 12.5f, 14.5f, 16.5f, 14.5f);
                if (plus) g.DrawLine(p, 14.5f, 12.5f, 14.5f, 16.5f);
            }
        }

        static void Draw(Graphics g, string kind)
        {
            switch (kind)
            {
                case "drive":
                    using (Brush b = new SolidBrush(Color.FromArgb(205, 210, 220))) g.FillRectangle(b, 2, 7, 16, 8);
                    using (Pen p = P(Dark, 1.3f)) g.DrawRectangle(p, 2, 7, 16, 8);
                    using (Pen p = P(Dark, 1.3f)) g.DrawLine(p, 4.5f, 11, 10, 11);
                    using (Brush b = new SolidBrush(Green)) g.FillEllipse(b, 13, 9.5f, 3.5f, 3.5f);
                    break;
                case "folder":
                    {
                        PointF[] pts = { new PointF(2, 5), new PointF(8, 5), new PointF(10, 7.5f), new PointF(18, 7.5f),
                                         new PointF(18, 16), new PointF(2, 16) };
                        using (Brush b = new SolidBrush(Yellow)) g.FillPolygon(b, pts);
                        using (Pen p = P(YellowD, 1.3f)) g.DrawPolygon(p, pts);
                    }
                    break;
                case "reload":
                    using (Pen p = P(Blue, 2.2f)) g.DrawArc(p, 4, 4, 12, 12, 40, 280);
                    using (Brush b = new SolidBrush(Blue))
                        g.FillPolygon(b, new PointF[] { new PointF(17.5f, 9.5f), new PointF(17.5f, 3.5f), new PointF(11.5f, 8.5f) });
                    break;
                case "cancel":
                    using (Pen p = P(Red, 2.6f))
                    {
                        g.DrawLine(p, 5, 5, 15, 15);
                        g.DrawLine(p, 15, 5, 5, 15);
                    }
                    break;
                case "zoomfull":
                    using (Pen p = P(Dark, 1.8f))
                    {
                        g.DrawLines(p, new PointF[] { new PointF(3, 7.5f), new PointF(3, 3), new PointF(7.5f, 3) });
                        g.DrawLines(p, new PointF[] { new PointF(12.5f, 3), new PointF(17, 3), new PointF(17, 7.5f) });
                        g.DrawLines(p, new PointF[] { new PointF(3, 12.5f), new PointF(3, 17), new PointF(7.5f, 17) });
                        g.DrawLines(p, new PointF[] { new PointF(12.5f, 17), new PointF(17, 17), new PointF(17, 12.5f) });
                    }
                    using (Brush b = new SolidBrush(Blue)) g.FillRectangle(b, 7.5f, 7.5f, 5, 5);
                    break;
                case "zoomin":
                    Magnifier(g);
                    using (Pen p = P(Blue, 1.8f))
                    {
                        g.DrawLine(p, 5.5f, 7.5f, 9.5f, 7.5f);
                        g.DrawLine(p, 7.5f, 5.5f, 7.5f, 9.5f);
                    }
                    break;
                case "zoomout":
                    Magnifier(g);
                    using (Pen p = P(Blue, 1.8f)) g.DrawLine(p, 5.5f, 7.5f, 9.5f, 7.5f);
                    break;
                case "levelmore":
                case "levelless":
                    using (Brush b = new SolidBrush(Color.FromArgb(215, 220, 230))) g.FillRectangle(b, 2, 2, 12, 12);
                    using (Pen p = P(Dark, 1.3f)) g.DrawRectangle(p, 2, 2, 12, 12);
                    using (Pen p = P(Dark, 1.0f)) g.DrawRectangle(p, 5, 5, 6, 6);
                    Badge(g, kind == "levelmore" ? Green : Red, kind == "levelmore");
                    break;
                case "free":
                    using (Brush b = new SolidBrush(Color.FromArgb(215, 220, 230))) g.FillEllipse(b, 2.5f, 2.5f, 15, 15);
                    using (Brush b = new SolidBrush(Blue)) g.FillPie(b, 2.5f, 2.5f, 15, 15, -90, 115);
                    using (Pen p = P(Dark, 1.3f)) g.DrawEllipse(p, 2.5f, 2.5f, 15, 15);
                    break;
                case "run":
                    using (Brush b = new SolidBrush(Green))
                        g.FillPolygon(b, new PointF[] { new PointF(5.5f, 3), new PointF(16.5f, 10), new PointF(5.5f, 17) });
                    break;
                case "delete":
                    {
                        PointF[] body = { new PointF(5, 6.5f), new PointF(15, 6.5f), new PointF(14, 17.5f), new PointF(6, 17.5f) };
                        using (Brush b = new SolidBrush(Color.FromArgb(250, 215, 215))) g.FillPolygon(b, body);
                        using (Pen p = P(Red, 1.5f))
                        {
                            g.DrawPolygon(p, body);
                            g.DrawLine(p, 3.5f, 5, 16.5f, 5);
                            g.DrawLine(p, 8, 3, 12, 3);
                            g.DrawLine(p, 8.5f, 9, 8.8f, 15);
                            g.DrawLine(p, 11.5f, 9, 11.2f, 15);
                        }
                    }
                    break;
                case "options":
                    using (Pen p = P(Dark, 3f))
                    {
                        for (int i = 0; i < 8; i++)
                        {
                            double a = i * Math.PI / 4;
                            g.DrawLine(p, (float)(10 + 6.2 * Math.Cos(a)), (float)(10 + 6.2 * Math.Sin(a)),
                                          (float)(10 + 8.4 * Math.Cos(a)), (float)(10 + 8.4 * Math.Sin(a)));
                        }
                    }
                    using (Pen p = P(Dark, 2.6f)) g.DrawEllipse(p, 5, 5, 10, 10);
                    using (Pen p = P(Dark, 1.2f)) g.DrawEllipse(p, 8.2f, 8.2f, 3.6f, 3.6f);
                    break;
                case "shield":
                    {
                        PointF[] sh = { new PointF(10, 2), new PointF(17, 4.5f), new PointF(16, 11), new PointF(10, 18), new PointF(4, 11), new PointF(3, 4.5f) };
                        using (Brush b = new SolidBrush(Blue)) g.FillPolygon(b, sh);
                        using (Pen p = P(Dark, 1.2f)) g.DrawPolygon(p, sh);
                        using (Pen p = P(Color.White, 1.8f)) g.DrawLines(p, new PointF[] { new PointF(6.5f, 9.5f), new PointF(9, 12), new PointF(13.5f, 7) });
                    }
                    break;
                case "diag":
                    using (Brush b = new SolidBrush(Color.FromArgb(235, 238, 245))) g.FillRectangle(b, 4, 3.5f, 12, 15);
                    using (Pen p = P(Dark, 1.3f)) g.DrawRectangle(p, 4, 3.5f, 12, 15);
                    using (Brush b = new SolidBrush(Dark)) g.FillRectangle(b, 7, 2, 6, 3);
                    using (Pen p = P(Blue, 1.4f))
                    {
                        g.DrawLine(p, 7, 8.5f, 13, 8.5f);
                        g.DrawLine(p, 7, 11.5f, 13, 11.5f);
                        g.DrawLine(p, 7, 14.5f, 11, 14.5f);
                    }
                    break;
                case "about":
                    using (Brush b = new SolidBrush(Blue)) g.FillEllipse(b, 2, 2, 16, 16);
                    using (Brush b = new SolidBrush(Color.White)) g.FillEllipse(b, 8.8f, 5, 2.4f, 2.4f);
                    using (Pen p = P(Color.White, 2.2f)) g.DrawLine(p, 10, 9.5f, 10, 15);
                    break;
                case "panel":
                    using (Brush b = new SolidBrush(Color.FromArgb(235, 238, 245))) g.FillRectangle(b, 2.5f, 3.5f, 15, 13);
                    using (Brush b = new SolidBrush(Blue)) g.FillRectangle(b, 11, 3.5f, 6.5f, 13);
                    using (Pen p = P(Dark, 1.3f)) g.DrawRectangle(p, 2.5f, 3.5f, 15, 13);
                    using (Pen p = P(Color.White, 1.2f))
                    {
                        g.DrawLine(p, 12.5f, 7, 16, 7);
                        g.DrawLine(p, 12.5f, 10, 16, 10);
                        g.DrawLine(p, 12.5f, 13, 16, 13);
                    }
                    break;
                case "export":
                    using (Brush b = new SolidBrush(Color.FromArgb(235, 238, 245))) g.FillRectangle(b, 3.5f, 2.5f, 11, 15);
                    using (Pen p = P(Dark, 1.3f)) g.DrawRectangle(p, 3.5f, 2.5f, 11, 15);
                    using (Pen p = P(Green, 2.2f))
                    {
                        g.DrawLine(p, 8, 11, 16.5f, 11);
                        g.DrawLine(p, 13.5f, 8, 16.5f, 11);
                        g.DrawLine(p, 13.5f, 14, 16.5f, 11);
                    }
                    break;
                case "search":
                    Magnifier(g);
                    break;
            }
        }
    }

    // ------------------------------------------------------------------
    // Vue treemap (style SpaceMonger : couleur par profondeur)
    // ------------------------------------------------------------------
    // Jeu de couleurs d'une case : remplissage + biseau clair (haut/gauche) et fonce (bas/droite), comme SpaceMonger.
    class BoxSet
    {
        public SolidBrush Fill;
        public Pen Light, Dark;
        public BoxSet(Color b, Color l, Color d)
        {
            Fill = new SolidBrush(b);
            Light = new Pen(l);
            Dark = new Pen(d);
        }
    }

    class Cell
    {
        public Node Node;   // null = case anonyme (groupe de petits elements, non selectionnable)
        public Rectangle R;
        public int Depth;       // 0 = racine de la vue
        public bool Expanded;   // dossier dont le contenu est dessine a l'interieur
        public int Header;      // hauteur de l'entete (dossiers developpes)
    }

    class TreemapView : Control
    {
        public Node Root;
        public Node FreeNode;   // bloc "espace libre" (uniquement a la racine d'un lecteur)
        public Node UncountedNode;   // bloc "non compte" (idem, optionnel)
        public string Message = "Cliquez sur Ouvrir pour choisir un lecteur ou un dossier.";

        // Palettes de SpaceMonger 1.4 (base, teinte claire, teinte foncee), valeurs exactes.
        static readonly int[] RbBase  = { 0xFF7F7F, 0xFFBF7F, 0xFFFF00, 0x7FFF7F, 0x7FFFFF, 0xBFBFFF, 0xBFBFBF, 0xFF7FFF };
        static readonly int[] RbLight = { 0xFFBFBF, 0xFFDFBF, 0xFFFFBF, 0xBFFFBF, 0xDFFFFF, 0xDFDFFF, 0xDFDFDF, 0xFFBFFF };
        static readonly int[] RbDark  = { 0xBF7F7F, 0xBF9F5F, 0xBFBF3F, 0x7FBF7F, 0x7FBFBF, 0x9F9FFF, 0x9F9F9F, 0xBF7FBF };
        static readonly int[] FxBase  = { 0xFFFFFF, 0xBFBFBF, 0x7F7F7F, 0xFF7F7F, 0xFFBF7F, 0xFFFF00, 0x7FFF7F, 0x7FFFFF, 0xBFBFFF, 0xFF7FFF };
        static readonly int[] FxLight = { 0xFFFFFF, 0xFFFFFF, 0xBFBFBF, 0xFF9F9F, 0xFFDFBF, 0xFFFFBF, 0xBFFFBF, 0xDFFFFF, 0xDFDFFF, 0xFFBFFF };
        static readonly int[] FxDark  = { 0xBFBFBF, 0x7F7F7F, 0x3F3F3F, 0xBF7F7F, 0xBF9F9F, 0xBFBF3F, 0x7FBF7F, 0x7FBFBF, 0x9F9FFF, 0xBF7FBF };

        public static readonly string[] ColorModeNames =
        {
            "Arc-en-ciel (par profondeur)", "Couleurs Windows", "Blanc", "Gris clair", "Gris fonce",
            "Rouge", "Orange", "Jaune", "Vert", "Aqua", "Bleu", "Violet", "Par type de fichier (fichiers seulement)"
        };

        static Color Rgb(int v) { return Color.FromArgb((v >> 16) & 255, (v >> 8) & 255, v & 255); }

        static readonly Color Background = Color.FromArgb(240, 240, 240);

        int maxDepth = 8;
        int minCell = 14;
        bool showLabels = true, showSizes = true, showDates = true;
        int layoutMode = 0, density = 0, bias = 0, fileColor = 0, folderColor = 0;
        int rootDepth;   // profondeur absolue de la racine de la vue : la couleur ne change pas quand on zoome

        // Seuils de cases de SpaceMonger selon la densite (-3 .. 2) : largeur x hauteur mini en pixels.
        static readonly int[] DensW = { 96, 64, 48, 32, 24, 16 };
        static readonly int[] DensH = { 64, 48, 32, 24, 16, 12 };

        public int MaxDepth { get { return maxDepth; } set { maxDepth = value; Relayout(); } }
        public int MinCell { get { return minCell; } set { minCell = Math.Max(2, value); Relayout(); } }
        public bool ShowLabels { get { return showLabels; } set { showLabels = value; Invalidate(); } }
        public bool ShowSizes { get { return showSizes; } set { showSizes = value; Invalidate(); } }
        public bool ShowDates { get { return showDates; } set { showDates = value; Invalidate(); } }
        public int LayoutMode { get { return layoutMode; } set { layoutMode = value; Relayout(); } }
        public int Density { get { return density; } set { density = Math.Max(-3, Math.Min(2, value)); Relayout(); } }
        public int Bias { get { return bias; } set { bias = Math.Max(-20, Math.Min(20, value)); Relayout(); } }
        public int FileColor { get { return fileColor; } set { fileColor = value; Invalidate(); } }
        public int FolderColor { get { return folderColor; } set { folderColor = value; Invalidate(); } }

        public bool RolloverBoxes { get; set; }
        public bool ShowNameTips = true, ShowInfoTips = true;
        public int NameTipDelay = 125, InfoTipDelay = 250;
        public int InfoFlags = 24;   // 1 chemin, 2 nom, 8 date, 16 taille, 32 attributs

        public Node Selected { get; private set; }

        public event Action<Node> HoverChanged;
        public event Action<Node> NodeActivated;
        public event Action<Node, Point> ContextRequested;
        public event Action SelectionChanged;

        readonly List<Cell> cells = new List<Cell>();
        bool dirty = true;
        Node hoverNode;
        Cell hoverCell;
        Point lastPt;
        bool nameShown, infoShown;
        readonly ToolTip tipName = new ToolTip();
        readonly ToolTip tipInfo = new ToolTip();
        readonly System.Windows.Forms.Timer nameTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer infoTimer = new System.Windows.Forms.Timer();
        readonly Pen rolloverPen = new Pen(Color.FromArgb(0, 0, 160), 2f);
        readonly SolidBrush rolloverBrush = new SolidBrush(Color.FromArgb(70, 255, 255, 255));

        readonly Font font = new Font("Segoe UI", 8f);
        readonly StringFormat fmtL = new StringFormat();
        readonly StringFormat fmtC = new StringFormat();
        readonly BoxSet[] rainbow = new BoxSet[8];
        readonly BoxSet[] fixedSets = new BoxSet[10];
        readonly BoxSet[] typeSets;
        readonly BoxSet windowsSet;
        readonly SolidBrush textBrush = new SolidBrush(Color.Black);
        readonly Pen borderPen = new Pen(Color.Black);
        readonly Pen selOuter = new Pen(Color.Black, 3f);
        readonly Pen selInner = new Pen(Color.White, 1f);

        public TreemapView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            tipName.ShowAlways = true; tipName.UseFading = false; tipName.UseAnimation = false;
            tipInfo.ShowAlways = true; tipInfo.UseFading = false; tipInfo.UseAnimation = false;
            nameTimer.Tick += delegate { nameTimer.Stop(); ShowNameTip(); };
            infoTimer.Tick += delegate { infoTimer.Stop(); ShowInfoTip(); };
            fmtL.Trimming = StringTrimming.EllipsisCharacter;
            fmtL.FormatFlags = StringFormatFlags.NoWrap;
            fmtC.Trimming = StringTrimming.EllipsisCharacter;
            fmtC.FormatFlags = StringFormatFlags.NoWrap;
            fmtC.Alignment = StringAlignment.Center;
            fmtC.LineAlignment = StringAlignment.Center;
            for (int i = 0; i < 8; i++) rainbow[i] = new BoxSet(Rgb(RbBase[i]), Rgb(RbLight[i]), Rgb(RbDark[i]));
            for (int i = 0; i < 10; i++) fixedSets[i] = new BoxSet(Rgb(FxBase[i]), Rgb(FxLight[i]), Rgb(FxDark[i]));
            windowsSet = new BoxSet(SystemColors.Control, SystemColors.ControlLightLight, SystemColors.ControlDark);
            typeSets = new BoxSet[Util.CategoryColors.Length];
            for (int i = 0; i < typeSets.Length; i++)
                typeSets[i] = new BoxSet(Util.CategoryColors[i], ControlPaint.LightLight(Util.CategoryColors[i]), ControlPaint.Dark(Util.CategoryColors[i]));
        }

        BoxSet GetSet(bool isFile, int mode, int absDepth, string name)
        {
            if (isFile && mode == 12) return typeSets[Util.CategoryOf(name)];
            if (mode == 1) return windowsSet;
            if (mode >= 2 && mode <= 11) return fixedSets[mode - 2];
            return rainbow[absDepth & 7];
        }

        public void Relayout()
        {
            dirty = true;
            hoverNode = null;
            hoverCell = null;
            HideTips();
            Invalidate();
        }

        public void Select(Node n)
        {
            if (n == Selected) return;
            Selected = n;
            Invalidate();
            if (SelectionChanged != null) SelectionChanged();
        }

        // Le contenu s'adapte a la taille de la fenetre : on recalcule la disposition a chaque redimensionnement.
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            dirty = true;
            hoverCell = null;
            HideTips();
            Invalidate();
        }

        // ---- Disposition (treemap "squarified") ----

        void BuildLayout()
        {
            cells.Clear();
            hoverCell = null;
            if (Root == null || Root.Size <= 0) return;
            Rectangle r = ClientRectangle;
            if (r.Width < 4 || r.Height < 4) return;
            r.Width -= 1;
            r.Height -= 1;
            rootDepth = 0;
            for (Node p = Root.Parent; p != null; p = p.Parent) rootDepth++;
            LayoutNode(Root, r, 0);
        }

        void LayoutNode(Node n, Rectangle r, int depth)
        {
            Cell c = new Cell();
            c.Node = n;
            c.R = r;
            c.Depth = depth;
            cells.Add(c);

            if (!n.IsDir || n.Size <= 0 || depth >= maxDepth) return;

            List<Node> kids = n.Children;
            if (depth == 0 && (FreeNode != null || UncountedNode != null))
            {
                kids = new List<Node>(n.Children);
                if (FreeNode != null) kids.Add(FreeNode);
                if (UncountedNode != null) kids.Add(UncountedNode);
                kids.Sort(delegate(Node a, Node b) { return b.Size.CompareTo(a.Size); });
            }
            if (kids.Count == 0) return;

            Rectangle inner;
            if (depth == 0)
            {
                inner = r;   // la racine de la vue n'a ni cadre ni entete
            }
            else
            {
                if (layoutMode != 0 && (r.Width < 24 || r.Height < 24)) return;
                int hdr = font.Height;
                int m = (layoutMode == 0) ? 3 : 2;   // marges de SpaceMonger : 3 px
                inner = new Rectangle(r.X + m, r.Y + hdr, r.Width - 2 * m, r.Height - hdr - m);
                if (inner.Width < 4 || inner.Height < 4) return;
                c.Expanded = true;
                c.Header = hdr;
            }
            if (layoutMode == 0) SmLayoutList(kids, inner, depth + 1);
            else Squarify(kids, inner, depth + 1);
        }

        // ---- Disposition de SpaceMonger : bissection recursive equilibree (FolderView.cpp, SizeFolders) ----
        // Principe : on repartit les elements (tries par taille decroissante) en deux groupes de tailles voisines,
        // on coupe le rectangle proportionnellement, puis on recommence dans chaque moitie tant qu'elle est assez grande.
        // Une moitie trop petite devient une case anonyme (couleur de la profondeur, non selectionnable).
        void SmLayoutList(List<Node> list, Rectangle r, int depth)
        {
            List<Node> l1 = new List<Node>(), l2 = new List<Node>();
            long s1 = 0, s2 = 0;
            foreach (Node n in list)
            {
                if (n.Size <= 0) continue;
                if (s1 <= s2) { l1.Add(n); s1 += n.Size; }
                else { l2.Add(n); s2 += n.Size; }
            }
            long tot = s1 + s2;
            if (tot == 0) return;

            int wb = 8, hb = 8;
            if (bias > 0) wb = bias + 8;
            else if (bias < 0) hb = -bias + 8;

            Rectangle ra, rb;
            if ((long)r.Width * wb / 8 > (long)r.Height * hb / 8)
            {
                int w1 = (int)((long)r.Width * s1 / tot);
                ra = new Rectangle(r.X, r.Y, w1, r.Height);
                rb = new Rectangle(r.X + w1, r.Y, r.Width - w1, r.Height);
            }
            else
            {
                int h1 = (int)((long)r.Height * s1 / tot);
                ra = new Rectangle(r.X, r.Y, r.Width, h1);
                rb = new Rectangle(r.X, r.Y + h1, r.Width, r.Height - h1);
            }
            SmPlace(l1, ra, depth);
            SmPlace(l2, rb, depth);
        }

        void SmPlace(List<Node> l, Rectangle r, int depth)
        {
            if (l.Count == 0) return;
            bool big = r.Width > DensW[density + 3] && r.Height > DensH[density + 3];
            if (l.Count > 1 && big) { SmLayoutList(l, r, depth); return; }
            if (big)
            {
                LayoutNode(l[0], r, depth);
            }
            else if (r.Width >= 2 && r.Height >= 2)
            {
                Cell a = new Cell();   // case anonyme : masque les petits elements du groupe
                a.Node = null;
                a.R = r;
                a.Depth = depth;
                cells.Add(a);
            }
        }

        void Squarify(List<Node> k, Rectangle rc, int depth)
        {
            int cnt = k.Count;   // trie par taille decroissante
            while (cnt > 0 && k[cnt - 1].Size <= 0) cnt--;
            if (cnt == 0) return;

            double total = 0;
            for (int q = 0; q < cnt; q++) total += k[q].Size;

            double x = rc.X, y = rc.Y, w = rc.Width, h = rc.Height;
            double scale = (w * h) / total;   // surface (px) par octet
            int i = 0;
            while (i < cnt)
            {
                double side = Math.Min(w, h);
                if (side < 1) break;

                // Choix des elements de la rangee : on ajoute tant que le pire ratio s'ameliore.
                double rowSum = 0, worst = double.MaxValue;
                int j = i;
                while (j < cnt)
                {
                    double s = rowSum + k[j].Size;
                    double areaSum = s * scale;
                    double amax = k[i].Size * scale;
                    double amin = k[j].Size * scale;
                    double wr = Math.Max(side * side * amax / (areaSum * areaSum),
                                         areaSum * areaSum / (side * side * amin));
                    if (j > i && wr > worst) break;
                    worst = wr;
                    rowSum = s;
                    j++;
                }

                double thick = rowSum * scale / side;
                bool column = (w >= h);   // rangee en colonne a gauche, sinon bande en haut
                double pos = column ? y : x;
                double end = column ? y + h : x + w;

                for (int m = i; m < j; m++)
                {
                    double len = k[m].Size * scale / thick;
                    double a1 = (m == j - 1) ? end : pos + len;
                    Rectangle cr = column ? Edge(x, pos, x + thick, a1) : Edge(pos, y, a1, y + thick);
                    pos += len;
                    if (cr.Width >= minCell && cr.Height >= minCell) LayoutNode(k[m], cr, depth);   // les mini-cases ne sont pas dessinees
                }

                if (column) { x += thick; w -= thick; }
                else { y += thick; h -= thick; }
                i = j;
            }
        }

        static Rectangle Edge(double x0, double y0, double x1, double y1)
        {
            int ax = (int)Math.Round(x0), ay = (int)Math.Round(y0);
            int bx = (int)Math.Round(x1), by = (int)Math.Round(y1);
            return new Rectangle(ax, ay, bx - ax, by - ay);
        }

        // ---- Dessin ----

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Background);
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            if (Root == null || Root.Size <= 0)
            {
                string msg = (Root != null) ? "Dossier vide (ou inaccessible)." : Message;
                g.DrawString(msg, font, textBrush, new RectangleF(0, 0, Width, Height), fmtC);
                return;
            }

            if (dirty) { BuildLayout(); dirty = false; }

            Cell sel = null;
            foreach (Cell c in cells)
            {
                DrawCell(g, c);
                if (Selected != null && c.Node == Selected) sel = c;
            }
            if (RolloverBoxes && hoverCell != null && hoverCell.Node != Root)
            {
                g.FillRectangle(rolloverBrush, hoverCell.R);
                foreach (Cell c in cells)
                {
                    if (c.Depth > 0 && c.R.Contains(lastPt))
                        g.DrawRectangle(rolloverPen, c.R.X + 1, c.R.Y + 1, c.R.Width - 2, c.R.Height - 2);
                }
            }
            if (sel != null)
            {
                Rectangle hr = sel.R;
                hr.Inflate(-1, -1);
                g.DrawRectangle(selOuter, hr);
                hr.Inflate(-2, -2);
                if (hr.Width > 2 && hr.Height > 2) g.DrawRectangle(selInner, hr);
            }
        }

        // Case biseautee : fond, biseau clair (haut/gauche) et fonce (bas/droite), cadre noir d'1 px.
        void DrawBox(Graphics g, BoxSet bs, Rectangle r)
        {
            g.FillRectangle(bs.Fill, r);
            if (r.Width > 5 && r.Height > 5)
            {
                g.DrawLine(bs.Light, r.X + 1, r.Y + 1, r.Right - 2, r.Y + 1);
                g.DrawLine(bs.Light, r.X + 1, r.Y + 1, r.X + 1, r.Bottom - 2);
                g.DrawLine(bs.Dark, r.X + 1, r.Bottom - 1, r.Right - 1, r.Bottom - 1);
                g.DrawLine(bs.Dark, r.Right - 1, r.Y + 1, r.Right - 1, r.Bottom - 1);
            }
            g.DrawRectangle(borderPen, r);
        }

        void DrawCell(Graphics g, Cell c)
        {
            Rectangle r = c.R;
            Node n = c.Node;
            if (c.Depth == 0) return;   // racine de la vue : seulement le fond

            int cd = rootDepth + c.Depth - 1;   // profondeur absolue : la couleur ne change pas quand on zoome

            if (n == null)   // case anonyme
            {
                DrawBox(g, GetSet(false, folderColor, cd, null), r);
                return;
            }

            if (n.IsFree)
            {
                if (n.IsUncounted)
                {
                    using (HatchBrush hb = new HatchBrush(HatchStyle.WideUpwardDiagonal, Color.FromArgb(205, 205, 205), Color.FromArgb(240, 240, 240)))
                        g.FillRectangle(hb, r);
                    g.DrawRectangle(borderPen, r);
                }
                g.DrawString(n.Name, font, textBrush, r, fmtC);
                return;
            }

            DrawBox(g, n.IsDir ? GetSet(false, folderColor, cd, null) : GetSet(true, fileColor, cd, n.Name), r);
            if (!showLabels) return;

            if (n.IsDir)
            {
                string t = showSizes ? n.Name + "   " + Util.FormatSize(n.Size) : n.Name;
                if (c.Expanded) DrawLabel(g, t, new Rectangle(r.X, r.Y, r.Width, c.Header), fmtL, 30, font.Height - 2);
                else DrawLabel(g, t, r, fmtL, 30, 13);
                return;
            }

            // Fichier : nom, taille et date au centre selon la place disponible.
            string txt = n.Name;
            if (showSizes && r.Width >= 40 && r.Height >= 26)
            {
                txt += "\n" + Util.FormatSize(n.Size);
                if (showDates && r.Width >= 80 && r.Height >= 44)
                    txt += "\n" + n.Modified.ToString("dd MMM yyyy H:mm:ss");
            }
            DrawLabel(g, txt, r, fmtC, 30, 13);
        }

        void DrawLabel(Graphics g, string text, Rectangle r, StringFormat f, int minW, int minH)
        {
            if (r.Width < minW || r.Height < minH) return;
            RectangleF rf = new RectangleF(r.X + 2, r.Y + 1, r.Width - 3, r.Height - 2);
            g.DrawString(text, font, textBrush, rf, f);
        }

        // ---- Souris ----

        Cell HitTest(Point p)
        {
            for (int i = cells.Count - 1; i >= 0; i--)
                if (cells[i].R.Contains(p)) return cells[i];
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (e.Location == lastPt) return;   // mouvement parasite (ex. apparition d'une bulle) : on ne touche a rien
            lastPt = e.Location;
            Cell c = HitTest(e.Location);
            Node n = (c != null) ? c.Node : null;
            HideTips();
            if (c != hoverCell)
            {
                hoverCell = c;
                if (RolloverBoxes) Invalidate();
            }
            if (n != hoverNode)
            {
                hoverNode = n;
                if (HoverChanged != null) HoverChanged(n);
            }
            if (n != null && n != Root && !n.IsFree)
            {
                if (ShowNameTips) { nameTimer.Interval = Math.Max(1, NameTipDelay); nameTimer.Start(); }
                if (ShowInfoTips) { infoTimer.Interval = Math.Max(1, InfoTipDelay); infoTimer.Start(); }
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            // Une bulle qui apparait sous le curseur declenche un MouseLeave : on l'ignore si le curseur est encore dans la vue.
            if (ClientRectangle.Contains(PointToClient(Cursor.Position))) return;
            HideTips();
            if (hoverCell != null)
            {
                hoverCell = null;
                if (RolloverBoxes) Invalidate();
            }
            if (hoverNode != null)
            {
                hoverNode = null;
                if (HoverChanged != null) HoverChanged(null);
            }
        }

        // ---- Info-bulles (comme SpaceMonger : bulle de nom + bulle d'informations, chacune avec son delai) ----

        public void HideTips()
        {
            nameTimer.Stop();
            infoTimer.Stop();
            if (nameShown) { tipName.Hide(this); nameShown = false; }
            if (infoShown) { tipInfo.Hide(this); infoShown = false; }
        }

        bool NameIsClipped(Cell c)
        {
            Node n = c.Node;
            string t = n.Name;
            Rectangle r = c.R;
            if (n.IsDir)
            {
                if (showSizes) t = n.Name + "   " + Util.FormatSize(n.Size);
                if (c.Expanded) r = new Rectangle(c.R.X, c.R.Y, c.R.Width, c.Header);
            }
            if (!showLabels || r.Width < 30 || r.Height < 13) return true;   // aucun libelle dessine
            using (Graphics g = CreateGraphics())
                return g.MeasureString(t, font).Width > r.Width - 3;
        }

        void ShowNameTip()
        {
            Cell c = hoverCell;
            if (c == null || c.Node == null || c.Node == Root || c.Node.IsFree) return;
            if (!NameIsClipped(c)) return;
            int tx = c.R.X - 2, ty = c.R.Y - 1;
            Size ts = TextRenderer.MeasureText(c.Node.Name, SystemFonts.StatusFont);
            Rectangle tipRect = new Rectangle(tx, ty, ts.Width + 14, ts.Height + 10);
            if (tipRect.Contains(lastPt))
            {
                tipRect.Inflate(6, 6);
                if (tipRect.Contains(lastPt)) ty = lastPt.Y + 24;   // sinon la bulle passe sous le curseur
            }
            tipName.Show(c.Node.Name, this, tx, ty, 30000);
            nameShown = true;
        }

        static string AttrText(string path)
        {
            try
            {
                FileAttributes a = File.GetAttributes(path);
                List<string> l = new List<string>();
                if ((a & FileAttributes.Archive) != 0) l.Add("Archive");
                if ((a & FileAttributes.Compressed) != 0) l.Add("Compresse");
                if ((a & FileAttributes.Directory) != 0) l.Add("Dossier");
                if ((a & FileAttributes.Encrypted) != 0) l.Add("Chiffre");
                if ((a & FileAttributes.Hidden) != 0) l.Add("Cache");
                if ((a & FileAttributes.Offline) != 0) l.Add("Hors ligne");
                if ((a & FileAttributes.ReadOnly) != 0) l.Add("Lecture seule");
                if ((a & FileAttributes.ReparsePoint) != 0) l.Add("Point de reparse");
                if ((a & FileAttributes.SparseFile) != 0) l.Add("Fragmente (sparse)");
                if ((a & FileAttributes.System) != 0) l.Add("Systeme");
                if ((a & FileAttributes.Temporary) != 0) l.Add("Temporaire");
                return string.Join(", ", l.ToArray());
            }
            catch (Exception) { return ""; }
        }

        string BuildInfoText(Node n)
        {
            StringBuilder sb = new StringBuilder();
            int f = InfoFlags;
            if ((f & 1) != 0) sb.AppendLine(n.FullPath);
            if ((f & 2) != 0) sb.AppendLine(n.Name);
            string line2 = "";
            if ((f & 16) != 0)
            {
                line2 = Util.FormatSize(n.Size);
                if (n.IsDir) line2 += " - " + n.FileCount + " fichiers";
            }
            if ((f & 32) != 0)
            {
                string at = AttrText(n.FullPath);
                if (at.Length > 0) line2 += (line2.Length > 0 ? " / " : "") + at;
            }
            if (line2.Length > 0) sb.AppendLine(line2);
            if ((f & 8) != 0)
            {
                try
                {
                    DateTime d = n.IsDir ? Directory.GetLastWriteTime(n.FullPath) : n.Modified;
                    sb.AppendLine(d.ToString("dd MMM yyyy H:mm:ss"));
                }
                catch (Exception) { }
            }
            return sb.ToString().TrimEnd();
        }

        void ShowInfoTip()
        {
            Cell c = hoverCell;
            if (c == null || c.Node == null || c.Node == Root || c.Node.IsFree) return;
            string text = BuildInfoText(c.Node);
            if (text.Length == 0) return;
            tipInfo.Show(text, this, lastPt.X + 20, lastPt.Y + 24, 30000);
            infoShown = true;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            HideTips();
            if (e.Button != MouseButtons.Left && e.Button != MouseButtons.Right) return;
            Cell c = HitTest(e.Location);
            Node n = (c != null) ? c.Node : null;
            if (n == null || n == Root || n.IsFree) n = null;
            Select(n);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left) return;
            Cell c = HitTest(e.Location);
            if (c != null && c.Node != null && c.Node.IsDir && c.Node != Root && NodeActivated != null)
                NodeActivated(c.Node);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Right) return;
            if (Selected != null && ContextRequested != null) ContextRequested(Selected, e.Location);
        }
    }

    // ------------------------------------------------------------------
    // Panneau de details (arborescence, plus gros fichiers, types, age, recherche)
    // ------------------------------------------------------------------
    class LvSorter : System.Collections.IComparer
    {
        public int Col;
        public bool Desc = true;
        public int Compare(object a, object b)
        {
            ListViewItem x = (ListViewItem)a, y = (ListViewItem)b;
            object tx = x.SubItems[Col].Tag, ty = y.SubItems[Col].Tag;
            int r;
            if (tx is long && ty is long) r = ((long)tx).CompareTo((long)ty);
            else r = string.Compare(x.SubItems[Col].Text, y.SubItems[Col].Text, StringComparison.CurrentCultureIgnoreCase);
            return Desc ? -r : r;
        }
    }

    class DetailsPanel : UserControl
    {
        public event Action<Node, bool> NodePicked;   // (noeud, zoom)
        public event Action<string> SearchRequested;
        TextBox txtQuery;
        TabControl tabs;
        TabPage tpTree, tpTop, tpTypes, tpAge, tpFind;
        TreeView tree;
        ListView lvTop, lvTypes, lvAge, lvFind;
        Node scan, scope;
        const int TopCount = 300;

        public DetailsPanel()
        {
            tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tpTree = new TabPage("Arborescence");
            tpTop = new TabPage("Gros fichiers");
            tpTypes = new TabPage("Types");
            tpAge = new TabPage("Age");
            tpFind = new TabPage("Recherche");
            tree = new TreeView();
            tree.Dock = DockStyle.Fill;
            tree.HideSelection = false;
            tree.BeforeExpand += delegate(object s, TreeViewCancelEventArgs e) { Populate(e.Node); };
            tree.AfterSelect += delegate(object s, TreeViewEventArgs e)
            {
                Node n = e.Node.Tag as Node;
                if (n != null && NodePicked != null) NodePicked(n, false);
            };
            tree.NodeMouseDoubleClick += delegate(object s, TreeNodeMouseClickEventArgs e)
            {
                Node n = e.Node.Tag as Node;
                if (n != null && n.IsDir && NodePicked != null) NodePicked(n, true);
            };
            tpTree.Controls.Add(tree);

            lvTop = MakeList(new string[] { "Nom", "Taille", "Modifie", "Dossier" }, new int[] { 150, 70, 80, 220 }, new bool[] { false, true, true, false });
            lvTypes = MakeList(new string[] { "Type", "Fichiers", "Taille", "%" }, new int[] { 130, 70, 80, 55 }, new bool[] { false, true, true, true });
            lvAge = MakeList(new string[] { "Derniere modification", "Fichiers", "Taille", "%" }, new int[] { 150, 70, 80, 55 }, new bool[] { false, true, true, true });
            lvFind = MakeList(new string[] { "Nom", "Taille", "Dossier" }, new int[] { 160, 70, 250 }, new bool[] { false, true, false });
            tpTop.Controls.Add(lvTop);
            tpTypes.Controls.Add(lvTypes);
            tpAge.Controls.Add(lvAge);
            tpFind.Controls.Add(lvFind);
            Panel bar = new Panel();
            bar.Dock = DockStyle.Top;
            bar.Height = 30;
            Button go = new Button();
            go.Text = "Rechercher";
            go.Dock = DockStyle.Right;
            go.Width = 85;
            txtQuery = new TextBox();
            txtQuery.Dock = DockStyle.Fill;
            txtQuery.KeyDown += delegate(object s2, KeyEventArgs e2)
            {
                if (e2.KeyCode == Keys.Enter && SearchRequested != null) { SearchRequested(txtQuery.Text); e2.Handled = true; e2.SuppressKeyPress = true; }
            };
            go.Click += delegate { if (SearchRequested != null) SearchRequested(txtQuery.Text); };
            bar.Controls.Add(txtQuery);
            bar.Controls.Add(go);
            bar.Padding = new Padding(2, 4, 2, 2);
            tpFind.Controls.Add(bar);
            tabs.TabPages.AddRange(new TabPage[] { tpTree, tpTop, tpTypes, tpAge, tpFind });
            Controls.Add(tabs);
        }

        ListView MakeList(string[] cols, int[] widths, bool[] numeric)
        {
            ListView lv = new ListView();
            lv.Dock = DockStyle.Fill;
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.HideSelection = false;
            lv.MultiSelect = false;
            for (int i = 0; i < cols.Length; i++) lv.Columns.Add(cols[i], widths[i], numeric[i] ? HorizontalAlignment.Right : HorizontalAlignment.Left);
            LvSorter sorter = new LvSorter();
            lv.ListViewItemSorter = null;
            lv.ColumnClick += delegate(object s, ColumnClickEventArgs e)
            {
                if (sorter.Col == e.Column) sorter.Desc = !sorter.Desc; else { sorter.Col = e.Column; sorter.Desc = numeric[e.Column]; }
                lv.ListViewItemSorter = sorter;
                lv.Sort();
            };
            lv.ItemActivate += delegate
            {
                if (lv.SelectedItems.Count == 0) return;
                Node n = lv.SelectedItems[0].Tag as Node;
                if (n != null && NodePicked != null) NodePicked(n, false);
            };
            ContextMenuStrip cm = new ContextMenuStrip();
            cm.Items.Add("Ouvrir dans l'Explorateur", null, delegate
            {
                Node n = Current(lv);
                if (n != null) try { Process.Start("explorer.exe", n.IsDir ? "\"" + n.FullPath + "\"" : "/select,\"" + n.FullPath + "\""); } catch (Exception) { }
            });
            cm.Items.Add("Copier le chemin", null, delegate
            {
                Node n = Current(lv);
                if (n != null) try { Clipboard.SetText(n.FullPath); } catch (Exception) { }
            });
            lv.ContextMenuStrip = cm;
            return lv;
        }

        static Node Current(ListView lv)
        {
            return lv.SelectedItems.Count == 0 ? null : lv.SelectedItems[0].Tag as Node;
        }

        static ListViewItem Row(Node n, params object[] cells)
        {
            ListViewItem it = new ListViewItem(cells[0].ToString());
            it.Tag = n;
            it.UseItemStyleForSubItems = false;
            for (int i = 1; i < cells.Length; i++)
            {
                string t; object tag = null;
                object[] pair = cells[i] as object[];
                if (pair != null) { t = (string)pair[0]; tag = pair[1]; } else t = cells[i].ToString();
                ListViewItem.ListViewSubItem si = it.SubItems.Add(t);
                si.Tag = tag;
            }
            return it;
        }

        static object[] SizeCell(long b) { return new object[] { Util.FormatSize(b), b }; }
        static object[] DateCell(DateTime d) { return new object[] { d.ToString("dd/MM/yyyy"), d.Ticks }; }
        static object[] NumCell(long v, string text) { return new object[] { text, v }; }

        // Arbre : toute l'analyse (ne change pas quand on zoome).
        public void SetScan(Node root)
        {
            scan = root;
            tree.BeginUpdate();
            tree.Nodes.Clear();
            if (root != null)
            {
                TreeNode t = MakeNode(root, root.Size);
                tree.Nodes.Add(t);
                t.Expand();
            }
            tree.EndUpdate();
        }

        TreeNode MakeNode(Node n, long parentSize)
        {
            double pct = parentSize > 0 ? n.Size * 100.0 / parentSize : 100;
            TreeNode t = new TreeNode((n.IsDir ? n.Name : n.Name) + "   " + Util.FormatSize(n.Size) + "   (" + pct.ToString("F1") + " %)");
            t.Tag = n;
            if (n.IsDir && n.Children.Count > 0) t.Nodes.Add(new TreeNode("..."));
            return t;
        }

        void Populate(TreeNode t)
        {
            Node n = t.Tag as Node;
            if (n == null) return;
            if (t.Nodes.Count == 1 && t.Nodes[0].Tag == null && t.Nodes[0].Text == "...")
            {
                t.Nodes.Clear();
                int shown = 0;
                foreach (Node c in n.Children)
                {
                    if (shown++ >= 400) { t.Nodes.Add(new TreeNode("... (" + (n.Children.Count - 400) + " autres elements)")); break; }
                    t.Nodes.Add(MakeNode(c, n.Size));
                }
            }
        }

        // Listes : suivent le dossier affiche dans la vue.
        public void SetScope(Node root)
        {
            scope = root;
            lvTop.BeginUpdate(); lvTypes.BeginUpdate(); lvAge.BeginUpdate();
            lvTop.Items.Clear(); lvTypes.Items.Clear(); lvAge.Items.Clear();
            if (root != null) Compute(root);
            lvTop.EndUpdate(); lvTypes.EndUpdate(); lvAge.EndUpdate();
        }

        void Compute(Node root)
        {
            List<Node> top = new List<Node>();
            long minTop = 0;
            Dictionary<string, long[]> types = new Dictionary<string, long[]>();
            long[] ageCount = new long[6];
            long[] ageSize = new long[6];
            long total = 0;
            DateTime now = DateTime.Now;
            Stack<Node> st = new Stack<Node>();
            st.Push(root);
            while (st.Count > 0)
            {
                Node n = st.Pop();
                if (n.IsDir)
                {
                    foreach (Node c in n.Children) st.Push(c);
                    continue;
                }
                total += n.Size;
                if (n.Size >= minTop || top.Count < TopCount)
                {
                    top.Add(n);
                    if (top.Count >= TopCount * 3)
                    {
                        top.Sort(delegate(Node a, Node b) { return b.Size.CompareTo(a.Size); });
                        top.RemoveRange(TopCount, top.Count - TopCount);
                        minTop = top[TopCount - 1].Size;
                    }
                }
                string ext = System.IO.Path.GetExtension(n.Name);
                ext = string.IsNullOrEmpty(ext) ? "(sans extension)" : ext.ToLowerInvariant();
                long[] t;
                if (!types.TryGetValue(ext, out t)) { t = new long[2]; types[ext] = t; }
                t[0]++; t[1] += n.Size;
                double days = (now - n.Modified).TotalDays;
                int bk = days < 30 ? 0 : days < 182 ? 1 : days < 365 ? 2 : days < 730 ? 3 : days < 1826 ? 4 : 5;
                ageCount[bk]++; ageSize[bk] += n.Size;
            }
            top.Sort(delegate(Node a, Node b) { return b.Size.CompareTo(a.Size); });
            if (top.Count > TopCount) top.RemoveRange(TopCount, top.Count - TopCount);
            foreach (Node n in top)
            {
                string dir = n.Parent != null ? n.Parent.FullPath : "";
                lvTop.Items.Add(Row(n, n.Name, SizeCell(n.Size), DateCell(n.Modified), dir));
            }
            List<KeyValuePair<string, long[]>> tl = new List<KeyValuePair<string, long[]>>(types);
            tl.Sort(delegate(KeyValuePair<string, long[]> a, KeyValuePair<string, long[]> b) { return b.Value[1].CompareTo(a.Value[1]); });
            int k = 0;
            foreach (KeyValuePair<string, long[]> kv in tl)
            {
                if (k++ >= 300) break;
                ListViewItem it = Row(null, kv.Key, NumCell(kv.Value[0], kv.Value[0].ToString("N0")), SizeCell(kv.Value[1]),
                    NumCell(total > 0 ? kv.Value[1] * 1000 / total : 0, (total > 0 ? kv.Value[1] * 100.0 / total : 0).ToString("F1")));
                it.SubItems[0].BackColor = Util.CategoryColors[Util.CategoryOf("x" + (kv.Key.StartsWith("(") ? "" : kv.Key))];
                lvTypes.Items.Add(it);
            }
            string[] names = { "Moins d'un mois", "1 a 6 mois", "6 a 12 mois", "1 a 2 ans", "2 a 5 ans", "Plus de 5 ans" };
            for (int i = 0; i < 6; i++)
                lvAge.Items.Add(Row(null, names[i], NumCell(ageCount[i], ageCount[i].ToString("N0")), SizeCell(ageSize[i]),
                    NumCell(total > 0 ? ageSize[i] * 1000 / total : 0, (total > 0 ? ageSize[i] * 100.0 / total : 0).ToString("F1"))));
        }

        public void FocusSearch()
        {
            tabs.SelectedTab = tpFind;
            txtQuery.Focus();
            txtQuery.SelectAll();
        }

        public string[] Search(string text)
        {
            lvFind.BeginUpdate();
            lvFind.Items.Clear();
            int found = 0;
            bool more = false;
            if (scope != null && !string.IsNullOrEmpty(text))
            {
                string q = text.ToLowerInvariant();
                Stack<Node> st = new Stack<Node>();
                st.Push(scope);
                while (st.Count > 0 && !more)
                {
                    Node n = st.Pop();
                    if (n != scope && n.Name.ToLowerInvariant().Contains(q))
                    {
                        if (found >= 1000) { more = true; break; }
                        found++;
                        lvFind.Items.Add(Row(n, n.Name, SizeCell(n.Size), n.Parent != null ? n.Parent.FullPath : ""));
                    }
                    foreach (Node c in n.Children) st.Push(c);
                }
            }
            lvFind.EndUpdate();
            tabs.SelectedTab = tpFind;
            return new string[] { found.ToString(), more ? "1" : "0" };
        }
    }

    // ------------------------------------------------------------------
    // Fenetre principale
    // ------------------------------------------------------------------
    class MainForm : Form
    {
        Settings cfg;
        TreemapView view;
        DetailsPanel panel;
        Splitter split;
        bool panelStale = true;
        ToolStripButton btnPanel, btnExport;
        ToolStripDropDownButton btnOpen;
        ToolStripButton btnDepthLess, btnDepthMore, btnDiag, btnAdmin;
        readonly bool isAdmin = IsAdmin();
        ToolStripButton btnReload, btnCancel, btnZoomFull, btnZoomIn, btnZoomOut, btnFree, btnRun, btnDelete;
        ToolStripStatusLabel lblInfo, lblHover;
        System.Windows.Forms.Timer timer;
        Scanner scanner;
        Thread worker;
        Stopwatch watch = new Stopwatch();
        Node scanRoot;
        Node freeNode;
        Node uncountedNode;
        long driveTotal, driveFree;
        string curPath;
        string startPath;

        public MainForm(string startPath)
        {
            this.startPath = startPath;
            cfg = Settings.Load();

            Text = AppName;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
            Width = cfg.Width;
            Height = cfg.Height;
            StartPosition = FormStartPosition.CenterScreen;
            if (cfg.SavePos && cfg.HasPos && PositionIsVisible(cfg.WinX, cfg.WinY, cfg.Width, cfg.Height))
            {
                StartPosition = FormStartPosition.Manual;
                Location = new Point(cfg.WinX, cfg.WinY);
                if (cfg.WinState == 2) WindowState = FormWindowState.Maximized;
            }
            KeyPreview = true;
            AllowDrop = true;

            view = new TreemapView();
            view.Dock = DockStyle.Fill;
            ApplyViewSettings();
            view.HoverChanged += OnHover;
            view.NodeActivated += delegate(Node n) { SetView(n); };
            view.ContextRequested += ShowContext;
            view.SelectionChanged += delegate { UpdateButtons(); UpdateTitle(); };

            StatusStrip ss = new StatusStrip();
            lblInfo = new ToolStripStatusLabel("Pret.");
            lblHover = new ToolStripStatusLabel("");
            lblHover.Spring = true;
            lblHover.TextAlign = ContentAlignment.MiddleRight;
            ss.Items.Add(lblInfo);
            ss.Items.Add(lblHover);

            ToolStrip ts = new ToolStrip();
            ts.GripStyle = ToolStripGripStyle.Hidden;
            btnOpen = new ToolStripDropDownButton("Ouvrir");
            btnOpen.ShowDropDownArrow = true;
            btnOpen.DropDownOpening += delegate { FillDrives(); };
            btnOpen.Image = Icons.Make("drive");
            FillDrives();
            btnReload = new ToolStripButton("Recharger", Icons.Make("reload"), delegate { StartScan(curPath); });
            btnCancel = new ToolStripButton("Annuler", Icons.Make("cancel"), delegate { if (scanner != null) scanner.Cancel = true; });
            btnZoomFull = new ToolStripButton("Zoom tout", Icons.Make("zoomfull"), delegate { if (scanRoot != null) SetView(scanRoot); });
            btnZoomIn = new ToolStripButton("Zoom +", Icons.Make("zoomin"), delegate { ZoomIn(); });
            btnZoomOut = new ToolStripButton("Zoom -", Icons.Make("zoomout"), delegate { GoUp(); });
            btnDepthLess = new ToolStripButton("Niveaux -", Icons.Make("levelless"), delegate { ChangeDepth(-1); });
            btnDepthMore = new ToolStripButton("Niveaux +", Icons.Make("levelmore"), delegate { ChangeDepth(1); });
            btnFree = new ToolStripButton("Espace libre", Icons.Make("free"), delegate
            {
                cfg.FreeSpace = !cfg.FreeSpace;
                btnFree.Checked = cfg.FreeSpace;
                if (view.Root != null) SetView(view.Root);
            });
            btnFree.Checked = cfg.FreeSpace;
            btnRun = new ToolStripButton("Lancer", Icons.Make("run"), delegate { RunNode(view.Selected); });
            btnRun.ToolTipText = "Ouvrir l'element selectionne avec son programme associe";
            btnDelete = new ToolStripButton("Supprimer", Icons.Make("delete"), delegate { DeleteNode(view.Selected); });
            btnPanel = new ToolStripButton("Details", Icons.Make("panel"), delegate { TogglePanel(); });
            btnPanel.ToolTipText = "Afficher / masquer le panneau de details (F4) : arborescence, gros fichiers, types, age";
            btnPanel.Checked = cfg.ShowPanel;
            btnExport = new ToolStripButton("Exporter", Icons.Make("export"), delegate { ExportCsv(); });
            btnExport.ToolTipText = "Exporter la vue en CSV (dossiers et fichiers de plus de 10 Mo)";
            ts.Items.Add(btnOpen);
            ts.Items.Add(btnReload);
            ts.Items.Add(btnCancel);
            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(btnZoomFull);
            ts.Items.Add(btnZoomIn);
            ts.Items.Add(btnZoomOut);
            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(btnDepthLess);
            ts.Items.Add(btnDepthMore);
            ts.Items.Add(btnFree);
            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(btnRun);
            ts.Items.Add(btnDelete);
            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(btnPanel);
            ts.Items.Add(btnExport);
            ts.Items.Add(new ToolStripSeparator());
            btnAdmin = new ToolStripButton("Admin", Icons.Make("shield"), delegate { RestartAsAdmin(); });
            btnAdmin.ToolTipText = "Relancer SpaceLite en administrateur (acces aux dossiers proteges ; le glisser-deposer ne fonctionne plus en mode administrateur)";
            btnAdmin.Enabled = !isAdmin;
            ts.Items.Add(btnAdmin);
            btnDiag = new ToolStripButton("Diagnostic", Icons.Make("diag"), delegate { ShowDiagnostic(); });
            btnDiag.ToolTipText = "Comparer l'espace compte avec l'espace utilise du lecteur, liens et dossiers non lus";
            ts.Items.Add(btnDiag);
            ts.Items.Add(new ToolStripButton("Options", Icons.Make("options"), delegate { ShowSetup(); }));
            ts.Items.Add(new ToolStripButton("A propos", Icons.Make("about"), delegate { ShowAbout(); }));
            ts.ImageScalingSize = new Size(Icons.Size, Icons.Size);
            foreach (ToolStripItem it in ts.Items)
            {
                if (it is ToolStripSeparator) continue;
                it.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
                it.TextImageRelation = TextImageRelation.ImageAboveText;
            }

            panel = new DetailsPanel();
            panel.Dock = DockStyle.Right;
            panel.Width = cfg.PanelWidth;
            panel.NodePicked += Reveal;
            panel.SearchRequested += DoSearch;
            split = new Splitter();
            split.Dock = DockStyle.Right;
            split.Width = 5;
            panel.Visible = split.Visible = cfg.ShowPanel;

            // Le controle Fill doit etre ajoute en premier.
            Controls.Add(view);
            Controls.Add(split);
            Controls.Add(panel);
            Controls.Add(ss);
            Controls.Add(ts);

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 200;
            timer.Tick += delegate
            {
                if (scanner != null)
                    lblInfo.Text = "Analyse : " + scanner.FilesSeen + " fichiers - " + scanner.Current;
            };

            // Glisser-deposer : tous les controles doivent l'accepter (la vue couvre la fenetre).
            Control[] dropTargets = { this, view, ss, ts };
            foreach (Control dc in dropTargets)
            {
                dc.AllowDrop = true;
                dc.DragEnter += OnDragEnterFiles;
                dc.DragDrop += OnDropFiles;
            }
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Back) { GoUp(); e.Handled = true; }
                else if (e.KeyCode == Keys.F5) { StartScan(curPath); }
                else if (e.KeyCode == Keys.Delete && !panel.ContainsFocus) { DeleteNode(view.Selected); }
                else if (e.KeyCode == Keys.F4) { TogglePanel(); }
                else if (e.Control && e.KeyCode == Keys.F) { if (!panel.Visible) TogglePanel(); panel.FocusSearch(); e.Handled = true; }
            };
            FormClosing += delegate
            {
                if (scanner != null) scanner.Cancel = true;
                Rectangle b = (WindowState == FormWindowState.Normal) ? Bounds : RestoreBounds;
                cfg.Width = b.Width;
                cfg.Height = b.Height;
                if (WindowState != FormWindowState.Minimized)
                {
                    cfg.WinX = b.X;
                    cfg.WinY = b.Y;
                    cfg.HasPos = true;
                    cfg.WinState = (WindowState == FormWindowState.Maximized) ? 2 : 0;
                }
                cfg.ShowPanel = panel.Visible;
                if (panel.Visible) cfg.PanelWidth = panel.Width;
                cfg.Save();
            };
            Shown += delegate
            {
                string p = !string.IsNullOrEmpty(this.startPath) ? this.startPath : null;
                if (p == null && cfg.ReopenLast && !string.IsNullOrEmpty(cfg.LastPath) && Directory.Exists(cfg.LastPath))
                    p = cfg.LastPath;
                if (p != null) StartScan(p);
            };

            btnCancel.Enabled = false;
            UpdateButtons();
        }

        // Evite de restaurer une fenetre hors de tout ecran (ecran debranche, resolution changee).
        static bool PositionIsVisible(int x, int y, int w, int h)
        {
            Rectangle r = new Rectangle(x, y, w, h);
            foreach (Screen sc in Screen.AllScreens)
            {
                Rectangle i = Rectangle.Intersect(sc.WorkingArea, r);
                if (i.Width >= 100 && i.Height >= 50) return true;
            }
            return false;
        }

        void ApplyViewSettings()
        {
            view.MaxDepth = cfg.MaxDepth;
            view.MinCell = cfg.MinCell;
            view.ShowLabels = cfg.ShowLabels;
            view.ShowSizes = cfg.ShowSizes;
            view.ShowDates = cfg.ShowDates;
            view.LayoutMode = cfg.LayoutMode;
            view.Density = cfg.Density;
            view.Bias = cfg.Bias;
            view.FileColor = cfg.FileColor;
            view.FolderColor = cfg.FolderColor;
            view.RolloverBoxes = cfg.Rollover;
            view.ShowNameTips = cfg.ShowNameTips;
            view.NameTipDelay = cfg.NameTipDelay;
            view.ShowInfoTips = cfg.ShowInfoTips;
            view.InfoTipDelay = cfg.InfoTipDelay;
            view.InfoFlags = cfg.InfoFlags;
            view.Invalidate();
        }

        void OnDragEnterFiles(object sender, DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        }

        void OnDropFiles(object sender, DragEventArgs e)
        {
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            string p = files[0];
            if (File.Exists(p)) p = System.IO.Path.GetDirectoryName(p);   // un fichier => son dossier
            BeginInvoke(new MethodInvoker(delegate { StartScan(p); }));   // rend la main a l'Explorateur
        }

        void ChangeDepth(int delta)
        {
            int d = Math.Max(1, Math.Min(99, view.MaxDepth + delta));
            view.MaxDepth = d;
            cfg.MaxDepth = d;
            lblInfo.Text = "Niveaux affiches : " + d;
        }

        // Liste des lecteurs (reconstruite a chaque ouverture du menu).
        void FillDrives()
        {
            btnOpen.DropDownItems.Clear();
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable ||
                                          d.DriveType == DriveType.Network))
                        {
                            string name = d.Name;
                            string label = name + "  " + d.VolumeLabel + "  (" + Util.FormatSize(d.TotalSize) +
                                           ", " + Util.FormatSize(d.AvailableFreeSpace) + " libres)";
                            btnOpen.DropDownItems.Add(label, Icons.Make("drive"), delegate { StartScan(name); });
                        }
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            if (btnOpen.DropDownItems.Count == 0) btnOpen.DropDownItems.Add("(aucun lecteur)").Enabled = false;
            btnOpen.DropDownItems.Add(new ToolStripSeparator());
            btnOpen.DropDownItems.Add("Dossier...", Icons.Make("folder"), delegate { Browse(); });
        }

        // ---- Analyse ----

        void Browse()
        {
            using (FolderBrowserDialog fb = new FolderBrowserDialog())
            {
                fb.Description = "Dossier a analyser";
                if (!string.IsNullOrEmpty(curPath) && Directory.Exists(curPath)) fb.SelectedPath = curPath;
                if (fb.ShowDialog(this) == DialogResult.OK) StartScan(fb.SelectedPath);
            }
        }

        void StartScan(string path)
        {
            if (worker != null && worker.IsAlive) return;
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                if (!string.IsNullOrEmpty(path))
                    MessageBox.Show("Dossier introuvable : " + path, "SpaceLite", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            curPath = path;
            cfg.LastPath = path;
            scanner = new Scanner();
            Scanner sc = scanner;
            view.Select(null);
            view.FreeNode = null;
            view.UncountedNode = null;
            view.Root = null;
            panel.SetScan(null);
            panel.SetScope(null);
            panelStale = true;
            view.Message = "Analyse en cours...";
            view.Relayout();
            scanRoot = null;
            SetBusy(true);
            watch.Reset();
            watch.Start();
            timer.Start();

            worker = new Thread(delegate()
            {
                Node r = null;
                try { r = sc.ScanRoot(path); } catch (Exception) { }
                try { BeginInvoke(new MethodInvoker(delegate { ScanDone(r, sc.Cancel); })); }
                catch (Exception) { }
            }, 64 * 1024 * 1024);
            worker.IsBackground = true;
            worker.Start();
        }

        void ScanDone(Node r, bool cancelled)
        {
            timer.Stop();
            watch.Stop();
            scanRoot = r;
            freeNode = null;
            uncountedNode = null;
            driveTotal = 0;
            driveFree = 0;
            if (r != null) PrepareFreeSpace(r);
            SetBusy(false);
            view.Message = "Aucun resultat.";
            panelStale = true;
            SetView(r);
            if (r != null)
                lblInfo.Text = (cancelled ? "Interrompu - " : "Termine - ") + r.FileCount + " fichiers, " +
                               Util.FormatSize(r.Size) + " en " + (watch.ElapsedMilliseconds / 1000.0).ToString("F1") + " s";
        }

        // Bloc "espace libre" : seulement si on a analyse la racine d'un lecteur.
        void PrepareFreeSpace(Node r)
        {
            try
            {
                string root = System.IO.Path.GetPathRoot(r.FullPath);
                if (root == null || !string.Equals(root.TrimEnd('\\'), r.FullPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    return;
                DriveInfo di = new DriveInfo(root);
                driveTotal = di.TotalSize;
                driveFree = di.AvailableFreeSpace;
                long gap = (driveTotal - driveFree) - r.Size;   // utilise - compte
                if (gap > 0)
                {
                    Node u = new Node();
                    u.IsFree = true;
                    u.IsUncounted = true;
                    u.Size = gap;
                    u.Name = "<Non compte : " + Util.FormatSize(gap) + ">\nSystem Volume Information,\nmetadonnees NTFS, clusters,\ndossiers illisibles...";
                    uncountedNode = u;
                }
                if (driveFree <= 0 || driveTotal <= 0) return;
                Node f = new Node();
                f.IsFree = true;
                f.Size = driveFree;
                f.Name = "<Espace libre : " + (driveFree * 100.0 / driveTotal).ToString("F1") + " %>\n" +
                         Util.FormatSize(driveFree) + " libres\n" +
                         "Fichiers : " + r.FileCount + "\n" +
                         "Dossiers : " + r.DirCount;
                freeNode = f;
            }
            catch (Exception) { }
        }

        void SetBusy(bool busy)
        {
            btnOpen.Enabled = !busy;
            btnCancel.Enabled = busy;
            UpdateButtons();
        }

        // ---- Navigation ----

        void SetView(Node n)
        {
            view.Select(null);
            view.FreeNode = (cfg.FreeSpace && n != null && n == scanRoot) ? freeNode : null;
            view.UncountedNode = (cfg.ShowUncounted && n != null && n == scanRoot) ? uncountedNode : null;
            view.Root = n;
            view.Relayout();
            UpdateTitle();
            UpdateButtons();
            SyncPanel(false);
        }

        // ---- Panneau de details, recherche, export ----

        void SyncPanel(bool scanChanged)
        {
            if (!panel.Visible) { panelStale = true; return; }
            Cursor old = Cursor;
            Cursor = Cursors.WaitCursor;
            try
            {
                if (scanChanged || panelStale) panel.SetScan(scanRoot);
                panel.SetScope(view.Root);
                panelStale = false;
            }
            finally { Cursor = old; }
        }

        void TogglePanel()
        {
            bool show = !panel.Visible;
            panel.Visible = split.Visible = show;
            btnPanel.Checked = show;
            if (show) SyncPanel(false);
        }

        static bool IsUnder(Node root, Node n)
        {
            for (Node p = n; p != null; p = p.Parent) if (p == root) return true;
            return false;
        }

        void Reveal(Node n, bool zoom)
        {
            if (n == null || n.IsFree) return;
            Node target = n.IsDir ? n : n.Parent;
            if (zoom && n.IsDir) SetView(n);
            else if (!IsUnder(view.Root, n) && target != null) SetView(target);
            else if (n == view.Root) return;
            view.Select(n);
        }

        void DoSearch(string text)
        {
            string q = text.Trim();
            if (q.Length == 0 || view.Root == null) return;
            string[] r = panel.Search(q);
            lblInfo.Text = r[0] + (r[1] == "1" ? "+" : "") + " resultat(s) pour \"" + q + "\" dans " + view.Root.FullPath;
        }

        static string Csv(string v)
        {
            if (v.IndexOfAny(new char[] { ';', '"', '\n', '\r' }) >= 0) return "\"" + v.Replace("\"", "\"\"") + "\"";
            return v;
        }

        void ExportCsv()
        {
            Node root = view.Root;
            if (root == null) return;
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "CSV (*.csv)|*.csv";
                d.FileName = "SpaceLite_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".csv";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("Type;Chemin;Taille (octets);Taille;Fichiers;Dossiers;% du parent;Modifie");
                    Stack<Node> st = new Stack<Node>();
                    st.Push(root);
                    long big = 10L * 1024 * 1024;
                    while (st.Count > 0)
                    {
                        Node n = st.Pop();
                        if (n.IsDir || n.Size >= big)
                        {
                            double pct = (n.Parent != null && n.Parent.Size > 0) ? n.Size * 100.0 / n.Parent.Size : 100;
                            sb.AppendLine((n.IsDir ? "Dossier" : "Fichier") + ";" + Csv(n.FullPath) + ";" + n.Size + ";" + Csv(Util.FormatSize(n.Size)) + ";" +
                                n.FileCount + ";" + n.DirCount + ";" + pct.ToString("F1") + ";" + (n.IsDir ? "" : n.Modified.ToString("yyyy-MM-dd HH:mm")));
                        }
                        for (int i = n.Children.Count - 1; i >= 0; i--) st.Push(n.Children[i]);
                    }
                    File.WriteAllText(d.FileName, sb.ToString(), new UTF8Encoding(true));
                    lblInfo.Text = "Export ecrit : " + d.FileName;
                }
                catch (Exception ex) { MessageBox.Show("Export impossible : " + ex.Message, "SpaceLite", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        void UpdateTitle()
        {
            Node r = view.Root;
            if (r == null) { Text = AppName; return; }
            Node sel = view.Selected;
            if (sel != null)
            {
                string ts = sel.FullPath;
                if (driveTotal > 0) ts += " - " + (sel.Size * 100.0 / driveTotal).ToString("F1") + " % du lecteur";
                Text = ts + " - " + Util.FormatSize(sel.Size) + " - " + AppName;
                return;
            }
            string t = r.FullPath;
            if (r == scanRoot && driveTotal > 0)
                t += " - " + Util.FormatSize(driveTotal) + " au total - " + Util.FormatSize(driveFree) + " libres";
            else
                t += " - " + Util.FormatSize(r.Size);
            Text = t + " - " + AppName;
        }

        void GoUp()
        {
            if (view.Root != null && view.Root.Parent != null) SetView(view.Root.Parent);
        }

        void ZoomIn()
        {
            Node t = view.Selected;
            if (t == null) return;
            if (!t.IsDir) t = t.Parent;
            if (t != null && t != view.Root) SetView(t);
        }

        void UpdateButtons()
        {
            bool busy = (worker != null && worker.IsAlive && scanRoot == null);
            Node root = view.Root;
            Node sel = view.Selected;
            btnReload.Enabled = !busy && !string.IsNullOrEmpty(curPath);
            btnZoomOut.Enabled = root != null && root.Parent != null;
            btnZoomFull.Enabled = root != null && root != scanRoot;
            Node target = (sel == null) ? null : (sel.IsDir ? sel : sel.Parent);
            btnZoomIn.Enabled = target != null && target != root;
            btnRun.Enabled = sel != null;
            btnDiag.Enabled = scanRoot != null;
            btnDelete.Enabled = sel != null && sel.Parent != null;
        }

        void OnHover(Node n)
        {
            if (n == null) { lblHover.Text = ""; return; }
            if (n.IsFree) { lblHover.Text = n.Name.Replace("\n", "  -  "); return; }
            double pct = (view.Root != null && view.Root.Size > 0) ? n.Size * 100.0 / view.Root.Size : 0;
            lblHover.Text = n.FullPath + "  -  " + Util.FormatSize(n.Size) +
                            (n.IsDir ? "  -  " + n.FileCount + " fichiers" : "") +
                            (driveTotal > 0 ? "  -  " + (n.Size * 100.0 / driveTotal).ToString("F1") + " % du lecteur" : "") +
                            "  -  " + pct.ToString("F1") + " % de la vue";
        }

        // ---- Options ----

        void ShowSetup()
        {
            using (Form f = new Form())
            {
                f.Text = "Options";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MaximizeBox = false;
                f.MinimizeBox = false;
                f.ShowInTaskbar = false;
                f.ClientSize = new Size(400, 400);

                TabControl tabs = new TabControl();
                tabs.SetBounds(8, 8, 384, 344);
                TabPage t1 = new TabPage("Affichage");
                TabPage t3 = new TabPage("Disposition et couleurs");
                TabPage t2 = new TabPage("Info-bulles et fenetre");
                tabs.TabPages.Add(t1);
                tabs.TabPages.Add(t3);
                tabs.TabPages.Add(t2);

                // --- Onglet Affichage ---
                Label l1 = new Label(); l1.Text = "Profondeur maximale :"; l1.SetBounds(12, 18, 170, 20);
                NumericUpDown nd = new NumericUpDown(); nd.Minimum = 1; nd.Maximum = 99;
                nd.Value = Math.Min(99, Math.Max(1, cfg.MaxDepth)); nd.SetBounds(220, 14, 60, 22);
                Label l3 = new Label(); l3.Text = "Taille mini d'une case (px) :"; l3.SetBounds(12, 48, 190, 20);
                NumericUpDown nm = new NumericUpDown(); nm.Minimum = 2; nm.Maximum = 100;
                nm.Value = Math.Min(100, Math.Max(2, cfg.MinCell)); nm.SetBounds(220, 44, 60, 22);
                Label l3b = new Label(); l3b.Text = "(mode Squarified seulement ; en mode SpaceMonger : voir Densite)"; l3b.ForeColor = SystemColors.GrayText; l3b.SetBounds(12, 70, 360, 18);
                CheckBox c1 = new CheckBox(); c1.Text = "Afficher les noms"; c1.Checked = cfg.ShowLabels; c1.SetBounds(12, 98, 340, 22);
                CheckBox c2 = new CheckBox(); c2.Text = "Afficher les tailles"; c2.Checked = cfg.ShowSizes; c2.SetBounds(12, 124, 340, 22);
                CheckBox c3 = new CheckBox(); c3.Text = "Afficher les dates (grands fichiers)"; c3.Checked = cfg.ShowDates; c3.SetBounds(12, 150, 340, 22);
                CheckBox c4 = new CheckBox(); c4.Text = "Afficher l'espace libre du lecteur"; c4.Checked = cfg.FreeSpace; c4.SetBounds(12, 176, 340, 22);
                CheckBox c6 = new CheckBox(); c6.Text = "Afficher l'espace non compte (utilise - compte)"; c6.Checked = cfg.ShowUncounted; c6.SetBounds(12, 202, 350, 22);
                CheckBox c5 = new CheckBox(); c5.Text = "Surbrillance des cases sous le curseur (rollover)"; c5.Checked = cfg.Rollover; c5.SetBounds(12, 228, 350, 22);
                t1.Controls.AddRange(new Control[] { l1, nd, l3, nm, l3b, c1, c2, c3, c4, c6, c5 });

                // --- Onglet Disposition et couleurs ---
                Label ll = new Label(); ll.Text = "Disposition :"; ll.SetBounds(12, 18, 100, 20);
                ComboBox cbL = new ComboBox(); cbL.DropDownStyle = ComboBoxStyle.DropDownList;
                cbL.Items.Add("Bissection equilibree (comme SpaceMonger)");
                cbL.Items.Add("Squarified (cases plus carrees)");
                cbL.SelectedIndex = cfg.LayoutMode;
                cbL.SetBounds(115, 14, 250, 22);
                Label ld = new Label(); ld.Text = "Densite :"; ld.SetBounds(12, 52, 100, 20);
                ComboBox cbD = new ComboBox(); cbD.DropDownStyle = ComboBoxStyle.DropDownList;
                cbD.Items.Add("Tres peu de cases (96x64)");
                cbD.Items.Add("Peu de cases (64x48)");
                cbD.Items.Add("Normal (48x32)");
                cbD.Items.Add("Beaucoup de fichiers (32x24) - defaut");
                cbD.Items.Add("Tres beaucoup de fichiers (24x16)");
                cbD.Items.Add("Enormement de fichiers (16x12)");
                cbD.SelectedIndex = cfg.Density + 3;
                cbD.SetBounds(115, 48, 250, 22);
                Label lb = new Label(); lb.Text = "Biais :"; lb.SetBounds(12, 90, 100, 20);
                TrackBar tb = new TrackBar(); tb.Minimum = -20; tb.Maximum = 20; tb.TickFrequency = 5; tb.LargeChange = 5;
                tb.Value = Math.Max(-20, Math.Min(20, cfg.Bias));
                tb.SetBounds(105, 82, 265, 40);
                Label lbh = new Label(); lbh.Text = "Cases plus hautes"; lbh.ForeColor = SystemColors.GrayText; lbh.SetBounds(115, 120, 120, 18);
                Label lbw = new Label(); lbw.Text = "Cases plus larges"; lbw.ForeColor = SystemColors.GrayText; lbw.TextAlign = ContentAlignment.TopRight; lbw.SetBounds(245, 120, 120, 18);
                Label lf = new Label(); lf.Text = "Couleur des fichiers :"; lf.SetBounds(12, 160, 140, 20);
                ComboBox cbF = new ComboBox(); cbF.DropDownStyle = ComboBoxStyle.DropDownList;
                foreach (string nme in TreemapView.ColorModeNames) cbF.Items.Add(nme);
                cbF.SelectedIndex = cfg.FileColor;
                cbF.SetBounds(155, 156, 210, 22);
                Label lo = new Label(); lo.Text = "Couleur des dossiers :"; lo.SetBounds(12, 194, 140, 20);
                ComboBox cbO = new ComboBox(); cbO.DropDownStyle = ComboBoxStyle.DropDownList;
                for (int i = 0; i < 12; i++) cbO.Items.Add(TreemapView.ColorModeNames[i]);
                cbO.SelectedIndex = cfg.FolderColor;
                cbO.SetBounds(155, 190, 210, 22);
                Label ln = new Label(); ln.Text = "Densite et biais ne s'appliquent qu'a la disposition SpaceMonger. La profondeur affichee se regle dans l'onglet Affichage ou avec Niveaux -/+.";
                ln.ForeColor = SystemColors.GrayText; ln.SetBounds(12, 236, 360, 50);
                t3.Controls.AddRange(new Control[] { ll, cbL, ld, cbD, lb, tb, lbh, lbw, lf, cbF, lo, cbO, ln });

                // --- Onglet Info-bulles et fenetre ---
                CheckBox n1 = new CheckBox(); n1.Text = "Info-bulle du nom (si le nom est tronque)"; n1.Checked = cfg.ShowNameTips; n1.SetBounds(12, 14, 350, 22);
                Label nl = new Label(); nl.Text = "Delai (ms) :"; nl.SetBounds(34, 42, 80, 20);
                NumericUpDown nn = new NumericUpDown(); nn.Minimum = 1; nn.Maximum = 99999;
                nn.Value = Math.Min(99999, Math.Max(1, cfg.NameTipDelay)); nn.SetBounds(120, 38, 70, 22);
                CheckBox i1 = new CheckBox(); i1.Text = "Info-bulle d'informations"; i1.Checked = cfg.ShowInfoTips; i1.SetBounds(12, 78, 350, 22);
                CheckBox ip = new CheckBox(); ip.Text = "Chemin complet"; ip.Checked = (cfg.InfoFlags & 1) != 0; ip.SetBounds(34, 104, 160, 22);
                CheckBox inm = new CheckBox(); inm.Text = "Nom"; inm.Checked = (cfg.InfoFlags & 2) != 0; inm.SetBounds(200, 104, 150, 22);
                CheckBox id = new CheckBox(); id.Text = "Date / heure"; id.Checked = (cfg.InfoFlags & 8) != 0; id.SetBounds(34, 130, 160, 22);
                CheckBox isz = new CheckBox(); isz.Text = "Taille"; isz.Checked = (cfg.InfoFlags & 16) != 0; isz.SetBounds(200, 130, 150, 22);
                CheckBox ia = new CheckBox(); ia.Text = "Attributs"; ia.Checked = (cfg.InfoFlags & 32) != 0; ia.SetBounds(34, 156, 160, 22);
                Label il = new Label(); il.Text = "Delai (ms) :"; il.SetBounds(34, 188, 80, 20);
                NumericUpDown ni = new NumericUpDown(); ni.Minimum = 1; ni.Maximum = 99999;
                ni.Value = Math.Min(99999, Math.Max(1, cfg.InfoTipDelay)); ni.SetBounds(120, 184, 70, 22);
                CheckBox w1 = new CheckBox(); w1.Text = "Memoriser la position et l'etat de la fenetre"; w1.Checked = cfg.SavePos; w1.SetBounds(12, 232, 360, 22);
                CheckBox w2 = new CheckBox(); w2.Text = "Rouvrir le dernier dossier au demarrage"; w2.Checked = cfg.ReopenLast; w2.SetBounds(12, 258, 360, 22);
                EventHandler sync = delegate
                {
                    nl.Enabled = nn.Enabled = n1.Checked;
                    ip.Enabled = inm.Enabled = id.Enabled = isz.Enabled = ia.Enabled = il.Enabled = ni.Enabled = i1.Checked;
                };
                n1.CheckedChanged += sync;
                i1.CheckedChanged += sync;
                sync(null, EventArgs.Empty);
                t2.Controls.AddRange(new Control[] { n1, nl, nn, i1, ip, inm, id, isz, ia, il, ni, w1, w2 });

                Button ok = new Button(); ok.Text = "OK"; ok.DialogResult = DialogResult.OK; ok.SetBounds(228, 364, 75, 26);
                Button cancel = new Button(); cancel.Text = "Annuler"; cancel.DialogResult = DialogResult.Cancel; cancel.SetBounds(313, 364, 75, 26);
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                f.Controls.AddRange(new Control[] { tabs, ok, cancel });

                if (f.ShowDialog(this) != DialogResult.OK) return;

                cfg.MaxDepth = (int)nd.Value;
                cfg.MinCell = (int)nm.Value;
                cfg.ShowLabels = c1.Checked;
                cfg.ShowSizes = c2.Checked;
                cfg.ShowDates = c3.Checked;
                cfg.FreeSpace = c4.Checked;
                cfg.ShowUncounted = c6.Checked;
                cfg.Rollover = c5.Checked;
                cfg.LayoutMode = cbL.SelectedIndex;
                cfg.Density = cbD.SelectedIndex - 3;
                cfg.Bias = tb.Value;
                cfg.FileColor = cbF.SelectedIndex;
                cfg.FolderColor = cbO.SelectedIndex;
                cfg.ShowNameTips = n1.Checked;
                cfg.NameTipDelay = (int)nn.Value;
                cfg.ShowInfoTips = i1.Checked;
                cfg.InfoTipDelay = (int)ni.Value;
                cfg.InfoFlags = (ip.Checked ? 1 : 0) | (inm.Checked ? 2 : 0) | (id.Checked ? 8 : 0) |
                                (isz.Checked ? 16 : 0) | (ia.Checked ? 32 : 0);
                cfg.SavePos = w1.Checked;
                cfg.ReopenLast = w2.Checked;
                btnFree.Checked = cfg.FreeSpace;
                ApplyViewSettings();
                if (view.Root != null) SetView(view.Root);
            }
        }

        public static bool IsAdmin()
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch (Exception) { return false; }
        }

        string AppName { get { return isAdmin ? "SpaceLite (administrateur)" : "SpaceLite"; } }

        void RestartAsAdmin()
        {
            if (worker != null && worker.IsAlive) return;
            if (MessageBox.Show("Relancer SpaceLite en administrateur ?\n\nWindows demandera une confirmation.\n" +
                    "En mode administrateur, le glisser-deposer depuis l'Explorateur ne fonctionne pas (utilisez Ouvrir).",
                    "SpaceLite", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            cfg.Save();   // le nouveau processus relit le .ini
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                if (!string.IsNullOrEmpty(curPath))
                    psi.Arguments = "\"" + curPath + (curPath.EndsWith("\\") ? "\\" : "") + "\"";   // "C:\\" : evite que \" soit pris pour un guillemet
                Process.Start(psi);
                Close();
            }
            catch (System.ComponentModel.Win32Exception) { }   // confirmation Windows refusee
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Relance impossible", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        string BuildDiagnostic()
        {
            if (scanRoot == null || scanner == null) return "Aucune analyse disponible.";
            Node r = scanRoot;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Analyse de : " + r.FullPath);
            sb.AppendLine("Compte par SpaceLite : " + Util.FormatSize(r.Size) + "  (" + r.FileCount + " fichiers, " + r.DirCount + " dossiers)");
            if (driveTotal > 0)
            {
                long used = driveTotal - driveFree;
                sb.AppendLine("Lecteur : capacite " + Util.FormatSize(driveTotal) + ", libre " + Util.FormatSize(driveFree) +
                              ", utilise " + Util.FormatSize(used));
                long gap = used - r.Size;
                sb.AppendLine("Ecart (utilise - compte) : " + Util.FormatSize(gap) + "  (" + (gap * 100.0 / used).ToString("F1") + " % de l'espace utilise)");
            }
            sb.AppendLine();
            sb.AppendLine("Points de reparse rencontres :");
            if (scanner.TagCount.Count == 0) sb.AppendLine("  aucun");
            foreach (KeyValuePair<uint, int> kv in scanner.TagCount)
            {
                bool skipped = (kv.Key == Scanner.TagSymlink || kv.Key == Scanner.TagMount);
                long bytes;
                scanner.TagBytes.TryGetValue(kv.Key, out bytes);
                sb.AppendLine("  0x" + kv.Key.ToString("X8") + "  " + Scanner.TagName(kv.Key) + " : " + kv.Value + " elements" +
                              (skipped ? " (ignores)" : (bytes > 0 ? ", fichiers comptes : " + Util.FormatSize(bytes) : " (comptes)")));
            }
            sb.AppendLine();
            sb.AppendLine("Liens ignores (symboliques / jonctions) : " + scanner.SkippedCount);
            foreach (string l in scanner.SkippedLinks) sb.AppendLine("  " + l);
            sb.AppendLine();
            sb.AppendLine("Dossiers non lus (acces refuse ou erreur) : " + scanner.UnreadCount +
                          (IsAdmin() ? "   [SpaceLite tourne deja en administrateur]" : "   [essayez le bouton Admin]"));
            List<KeyValuePair<string, int>> groups = new List<KeyValuePair<string, int>>(scanner.UnreadByParent);
            groups.Sort(delegate(KeyValuePair<string, int> x, KeyValuePair<string, int> y) { return y.Value.CompareTo(x.Value); });
            int shown = 0;
            foreach (KeyValuePair<string, int> g in groups)
            {
                if (g.Value <= 5) continue;
                if (shown++ == 0) sb.AppendLine("  Groupes (plus de 5 dossiers non lus dans le meme dossier parent) :");
                sb.AppendLine("    " + g.Value + " x  dans " + g.Key);
            }
            int single = 0;
            foreach (string u in scanner.Unread)
            {
                string par = null;
                try { par = System.IO.Path.GetDirectoryName(u); } catch (Exception) { }
                int cnt;
                if (par == null || !scanner.UnreadByParent.TryGetValue(par, out cnt)) cnt = 1;
                if (cnt > 5) continue;
                if (single++ == 0) sb.AppendLine("  Dossiers isoles :");
                if (single > 120) { sb.AppendLine("    ..."); break; }
                sb.AppendLine("    " + u);
            }
            sb.AppendLine();
            sb.AppendLine("Note : l'espace utilise d'un lecteur inclut aussi des elements qu'un parcours de dossiers ne voit pas");
            sb.AppendLine("(System Volume Information, metadonnees NTFS, journaux, corbeille d'autres comptes, espace alloue par cluster).");
            sb.AppendLine("Les liens physiques (WinSxS) sont comptes autant de fois qu'ils apparaissent, comme SpaceMonger.");
            return sb.ToString();
        }

        void ShowDiagnostic()
        {
            string text = BuildDiagnostic();
            using (Form f = new Form())
            {
                f.Text = "Diagnostic de l'analyse";
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(780, 520);
                f.ShowInTaskbar = false;
                TextBox tb = new TextBox();
                tb.Multiline = true;
                tb.ReadOnly = true;
                tb.ScrollBars = ScrollBars.Both;
                tb.WordWrap = false;
                tb.Font = new Font("Consolas", 9f);
                tb.Text = text;
                tb.SelectionStart = 0;
                tb.SelectionLength = 0;
                tb.SetBounds(8, 8, 764, 468);
                tb.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
                Button copy = new Button(); copy.Text = "Copier"; copy.SetBounds(612, 484, 75, 26);
                copy.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
                copy.Click += delegate { try { Clipboard.SetText(text); } catch (Exception) { } };
                Button close = new Button(); close.Text = "Fermer"; close.DialogResult = DialogResult.Cancel; close.SetBounds(697, 484, 75, 26);
                close.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
                f.CancelButton = close;
                f.Controls.AddRange(new Control[] { tb, copy, close });
                f.ShowDialog(this);
            }
        }

        void ShowAbout()
        {
            MessageBox.Show("SpaceLite 1.0\nAnalyseur d'espace disque portable (treemap + details).\n\n" +
                "Clic : selectionner - Double-clic : entrer dans un dossier\n" +
                "Retour arriere : remonter - F5 : recharger - Suppr : corbeille\n" +
                "F4 : panneau de details - Ctrl+F : rechercher\n" +
                "Clic droit : menu (Explorateur, copier le chemin, corbeille)\n\n" +
                "Reglages : SpaceLite.ini a cote de l'exe, cree seulement si vous changez les options.\n\n" +
                "Licence MIT. La disposition et les palettes derivent de SpaceMonger 1.x (Sean Werkema, MIT) : voir THIRD-PARTY-NOTICES.md.", "A propos de SpaceLite");
        }

        // ---- Menu contextuel et actions ----

        void ShowContext(Node n, Point pt)
        {
            ContextMenuStrip cm = new ContextMenuStrip();
            cm.Items.Add("Ouvrir dans l'Explorateur", null, delegate { OpenInExplorer(n); });
            cm.Items.Add("Copier le chemin", null, delegate
            {
                try { Clipboard.SetText(n.FullPath); } catch (Exception) { }
            });
            if (n.IsDir && n != view.Root)
                cm.Items.Add("Entrer dans ce dossier", null, delegate { SetView(n); });
            if (n.Parent != null)
            {
                cm.Items.Add(new ToolStripSeparator());
                cm.Items.Add("Mettre a la corbeille...", null, delegate { DeleteNode(n); });
            }
            cm.Show(view, pt);
        }

        static void OpenInExplorer(Node n)
        {
            try { Process.Start("explorer.exe", "/select,\"" + n.FullPath + "\""); }
            catch (Exception) { }
        }

        static void RunNode(Node n)
        {
            if (n == null) return;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(n.FullPath);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Ouverture impossible", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void DeleteNode(Node n)
        {
            if (n == null || n.Parent == null) return;
            string what = n.IsDir ? "le dossier" : "le fichier";
            if (MessageBox.Show("Mettre " + what + " a la corbeille ?\n\n" + n.FullPath + "\n(" + Util.FormatSize(n.Size) + ")",
                    "Confirmation", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            try
            {
                if (n.IsDir)
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(n.FullPath,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                else
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(n.FullPath,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Suppression impossible", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            bool still = n.IsDir ? Directory.Exists(n.FullPath) : File.Exists(n.FullPath);
            if (still) return;   // annule ou echec

            for (Node p = n.Parent; p != null; p = p.Parent)
            {
                p.Size -= n.Size;
                p.FileCount -= n.FileCount;
                if (n.IsDir) p.DirCount -= n.DirCount + 1;
            }
            n.Parent.Children.Remove(n);
            lblHover.Text = "";
            if (view.Root == n) SetView(n.Parent);
            else { view.Select(null); view.Relayout(); }
            UpdateTitle();
            panelStale = true;
            SyncPanel(true);
        }
    }

    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch (Exception) { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args.Length > 0 ? args[0] : null));
        }
    }
}
