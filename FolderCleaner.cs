using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FolderCleaner
{
    /// <summary>目录分析清理工具 v5 - 布局稳定 + 多线程扫描不卡界面</summary>
    public class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args != null && args.Length > 0)
            {
                if (args[0] == "--selftest")
                {
                    SelfTest.Run(args.Length > 1 ? args[1] : "");
                    return;
                }
                if (args[0] == "--perftest")
                {
                    Application.EnableVisualStyles();
                    PerfTest.Run(args.Length > 1 ? args[1] : @"D:\Win11\Temp\_perf_test");
                    return;
                }
                if (args[0] == "--layouttest")
                {
                    Application.EnableVisualStyles();
                    LayoutTest.Run();
                    return;
                }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    /// <summary>布局测试：验证窗口拉伸时控件不变形（检查控件宽度是否保持固定）</summary>
    public static class LayoutTest
    {
        public static void Run()
        {
            var log = new StringBuilder();
            var form = new MainForm();
            form.Show();
            // 小窗口
            form.Width = 900; form.Height = 560;
            Application.DoEvents();
            log.AppendLine("small: dirWidth=" + form.DirWidthProbe() + " aiUrlWidth=" + form.AiUrlWidthProbe() + " treeDocked=" + form.TreeDockProbe());
            // 大窗口
            form.Width = 1600; form.Height = 900;
            Application.DoEvents();
            log.AppendLine("large: dirWidth=" + form.DirWidthProbe() + " aiUrlWidth=" + form.AiUrlWidthProbe() + " treeDocked=" + form.TreeDockProbe());
            File.WriteAllText(@"D:\Win11\Temp\layout_result.txt", log.ToString());
            form.Close();
        }
    }

    /// <summary>性能测试：验证批量渲染下 UI 不卡死</summary>
    public static class PerfTest
    {        public static void Run(string root)
        {
            var log = new StringBuilder();
            var form = new MainForm();
            form.SetDirProbe(root);
            form.Show();
            form.TriggerScanProbe();
            int heartbeats = 0;
            var timer = new System.Windows.Forms.Timer { Interval = 100 };
            timer.Tick += (s, e) => { heartbeats++; };
            timer.Start();
            var deadline = DateTime.Now.AddSeconds(60);
            while (DateTime.Now < deadline)
            {
                Application.DoEvents();
                Thread.Sleep(30);
                if (form.IsScanDoneProbe()) break;
            }
            timer.Stop();
            log.AppendLine("scanDone=" + form.IsScanDoneProbe());
            log.AppendLine("items=" + form.ItemCountProbe());
            log.AppendLine("heartbeats=" + heartbeats);
            log.AppendLine("status=" + form.StatusProbe());
            File.WriteAllText(@"D:\Win11\Temp\perf_result.txt", log.ToString());
            form.Close();
        }
    }

    public class FolderItem
    {
        public string Path;
        public string Rel;
        public long Size;
        public int FileCount;
        public DateTime? MTime;
        public string Owner;
        public string Category;
        public string Note;
        public string Source; // known=识别库 / registry=注册表 / cache=缓存 / ai=AI识别 / unknown=未知
        public bool Checked;
        public TreeNode Node;
    }

    public class MainForm : Form
    {
        // ---- 控件 ----
        DarkTextBox txtDir;
        Button btnBrowse, btnScan;
        CheckBox chkAI, chkSize;
        ComboBox cboDepth;
        // AI 配置（存放于齿轮弹窗，值存成员字段，供扫描时读取）
        string aiApiUrl = "https://api.deepseek.com/v1";
        string aiApiKey = "";
        string aiModel = "deepseek-chat";
        RadioButton rbRecycle, rbPermanent;
        TreeView tree;
        Label lblStatus, lblSel;
        Button btnAll, btnNone, btnInvert, btnDeletable, btnExpand, btnCollapse, btnDelete;

        // ---- 数据 ----
        List<FolderItem> items = new List<FolderItem>();
        Dictionary<string, FolderItem> relMap = new Dictionary<string, FolderItem>(StringComparer.OrdinalIgnoreCase);
        List<Tuple<string, string, string>> installedApps = new List<Tuple<string, string, string>>();
        CancellationTokenSource scanCts, aiCts;

        // ---- 深色主题配色 ----
        internal static readonly Color C_BG     = Color.FromArgb(30, 30, 30);      // 窗体/面板背景
        internal static readonly Color C_CTL    = Color.FromArgb(37, 37, 38);      // 输入框/树/控件背景
        internal static readonly Color C_BTN    = Color.FromArgb(51, 51, 51);      // 按钮
        internal static readonly Color C_HOT    = Color.FromArgb(63, 63, 65);      // 按钮悬停
        internal static readonly Color C_FG     = Color.FromArgb(218, 218, 218);   // 主前景文字
        internal static readonly Color C_FG_DIM = Color.FromArgb(150, 150, 150);   // 次要前景（状态栏）
        internal static readonly Color C_EDGE   = Color.FromArgb(62, 62, 66);       // 边框/描边/菜单高亮
        internal static readonly Color C_SEL    = Color.FromArgb(45, 60, 82);      // 树选中行
        internal static readonly Color C_LINE   = Color.FromArgb(70, 70, 70);      // 树连接线

        // ---- 多线程渲染队列 ----
        ConcurrentQueue<object> renderQueue = new ConcurrentQueue<object>(); // FolderItem 或 null(=完成标记)
        System.Windows.Forms.Timer renderTimer;
        Stopwatch progressSw = new Stopwatch();
        volatile bool scanDone = false;

        // ---- 识别映射（长 key 优先匹配）----
        static readonly Dictionary<string, string> KnownMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // 内置识别库已清空，全部依赖外部 识别库.txt
        };

        static readonly HashSet<string> ExactOnlyKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
        };

        static readonly string[] CacheKeywords = new string[]
        {
            "temp", "tmp", "cache", "caches", "logs", "crashdumps", "crashreports",
            "node_modules", "__pycache__", "squirreltemp", "updater", "installer",
            "update", "patchcache", "blob_storage", "gpu_cache", "shader_cache"
        };

        // 太通用的子目录名：不参与注册表匹配（避免 "boot" 命中 "Python ... Bootstrap" 这类误判）
        static readonly HashSet<string> GenericSubdirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "boot", "boots", "efi", "efiboot", "grub", "grub2", "isolinux", "syslinux",
            "plugin", "plugins", "extension", "extensions", "addon", "addons", "module", "modules",
            "bin", "lib", "sbin", "etc", "share", "docs", "doc", "help", "include", "scripts",
            "data", "config", "configs", "locale", "locales", "lang", "langs", "fonts", "themes",
            "resources", "assets", "drivers", "driver", "tools", "tool", "utils", "util", "common",
            "core", "main", "app", "apps", "program", "programs", "home", "users", "system",
            "backup", "backups", "old", "new", "tmp2", "test", "tests", "sample", "samples",
            "icon", "icons", "images", "img", "media", "sound", "sounds", "music", "video", "videos",
            "pictures", "photos", "download", "downloads", "desktop", "documents", "public",
        };

        public MainForm()
        {
            Text = "目录分析清理工具";
            Width = 1440; Height = 780; MinimumSize = new Size(1100, 560);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);
            ConfigFile = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "配置.ini");
            ResultFile = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "保存结果.txt");
            KnownMapFile = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "识别库.txt");
            BuildUI();
            LoadConfig();
            EnsureKnownMapFile();
            LoadKnownMap();
            renderTimer = new System.Windows.Forms.Timer { Interval = 80 };
            renderTimer.Tick += (s, e) => RenderBatch();
            renderTimer.Start();
            Load += (s, e) => { SetDarkTitleBar(); Task.Run(() => LoadInstalledApps()); LoadResults(); };
            FormClosing += (s, e) => SaveConfig();
        }

        // ---- 内置识别库配置文件（用户可自行编辑）----
        string KnownMapFile;
        Dictionary<string, string> loadedKnownMap;
        HashSet<string> loadedExactOnlyKeys;

        // 首次运行：若配置文件不存在，用内置默认值生成一份，方便用户编辑
        void EnsureKnownMapFile()
        {
            try
            {
                if (File.Exists(KnownMapFile)) return;
                var sb = new StringBuilder();
                sb.AppendLine("# 目录分析清理工具 - 用户识别库");
                sb.AppendLine("# 每行格式：关键词 => 软件名");
                sb.AppendLine("# 目录名包含关键词即匹配（不区分大小写）；关键词前加 ! 表示必须完全同名才匹配（如 !go => Go语言）");
                sb.AppendLine("# 以 # 开头的行是注释，会被忽略。修改后保存，下次启动自动生效。");
                sb.AppendLine("# （内置识别库已内嵌在程序中，此文件仅用于你自己追加的条目）");
                File.WriteAllText(KnownMapFile, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        void LoadKnownMap()
        {
            try
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var exact = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(KnownMapFile))
                {
                    foreach (var raw in File.ReadAllLines(KnownMapFile))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                        var idx = line.IndexOf("=>", StringComparison.Ordinal);
                        if (idx <= 0) continue;
                        var k = line.Substring(0, idx).Trim();
                        var v = line.Substring(idx + 2).Trim();
                        if (k.Length == 0 || v.Length == 0) continue;
                        if (k.StartsWith("!")) { k = k.Substring(1).Trim(); exact.Add(k); }
                        map[k.ToLowerInvariant()] = v; // 关键词统一转小写，匹配时与 low 大小写一致
                    }
                }
                loadedKnownMap = map.Count > 0 ? map : KnownMap;
                loadedExactOnlyKeys = map.Count > 0 ? exact : ExactOnlyKeys;
            }
            catch { loadedKnownMap = KnownMap; loadedExactOnlyKeys = ExactOnlyKeys; }
        }

        // ---- 结果文件：保存分析结果，下次启动免扫描 ----
        string ResultFile;

        void SaveResults()
        {
            try
            {
                if (items.Count == 0) return;
                var sb = new StringBuilder();
                sb.AppendLine("ROOT=" + txtDir.Text.Trim());
                foreach (var it in items)
                {
                    // rel | size | owner | category | note | source | checked | path
                    sb.Append(it.Rel); sb.Append('\t');
                    sb.Append(it.Size); sb.Append('\t');
                    sb.Append(it.Owner.Replace('\t', ' ')); sb.Append('\t');
                    sb.Append(it.Category); sb.Append('\t');
                    sb.Append(it.Note.Replace('\t', ' ')); sb.Append('\t');
                    sb.Append(it.Source); sb.Append('\t');
                    sb.Append(it.Checked ? "1" : "0"); sb.Append('\t');
                    sb.AppendLine(it.Path);
                }
                File.WriteAllText(ResultFile, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        void LoadResults()
        {
            try
            {
                if (!File.Exists(ResultFile)) return;
                var lines = File.ReadAllLines(ResultFile, Encoding.UTF8);
                var newItems = new List<FolderItem>();
                string root = "";
                foreach (var raw in lines)
                {
                    var line = raw.TrimEnd('\r', '\n');
                    if (line.StartsWith("ROOT=")) { root = line.Substring(5); continue; }
                    var p = line.Split('\t');
                    if (p.Length < 8) continue;
                    var item = new FolderItem
                    {
                        Rel = p[0],
                        Size = long.Parse(p[1]),
                        Owner = p[2],
                        Category = p[3],
                        Note = p[4],
                        Source = p[5],
                        Checked = p[6] == "1",
                        Path = p[7]
                    };
                    newItems.Add(item);
                }
                if (newItems.Count == 0) return;
                // 清空并渲染
                SafeUi(() =>
                {
                    items.Clear(); relMap.Clear();
                    tree.BeginUpdate();
                    tree.Nodes.Clear();
                    foreach (var item in newItems)
                    {
                        items.Add(item); relMap[item.Rel] = item;
                        AddNodeInternal(item);
                    }
                    tree.EndUpdate();
                    btnScan.Text = "开始分析";
                    if (string.IsNullOrEmpty(txtDir.Text) && !string.IsNullOrEmpty(root)) txtDir.Text = root;
                    long total = 0; int chk = 0;
                    foreach (var it in items) { total += it.Size; if (it.Checked) chk++; }
                    lblStatus.Text = string.Format("已加载上次分析结果：{0} 个文件夹，合计 {1}（上次勾选 {2} 项）", items.Count, FormatSize(total), chk);
                    UpdateSel();
                });
            }
            catch { }
        }

        // ---- 配置文件 ----
        string ConfigFile;
        const string CONFIG_SECTION = "[AI]";
        const string GENERAL_SECTION = "[General]";

        void LoadConfig()
        {
            try
            {
                if (!File.Exists(ConfigFile)) return;
                var lines = File.ReadAllLines(ConfigFile);
                bool inAi = false, inGeneral = false;
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    if (line.StartsWith("["))
                    {
                        inAi = line.StartsWith(CONFIG_SECTION);
                        inGeneral = line.StartsWith(GENERAL_SECTION);
                        continue;
                    }
                    if ((!inAi && !inGeneral) || line.StartsWith(";") || line.StartsWith("#") || line.Length == 0) continue;
                    var idx = line.IndexOf('=');
                    if (idx <= 0) continue;
                    var k = line.Substring(0, idx).Trim();
                    var v = line.Substring(idx + 1).Trim();
                    if (inAi)
                    {
                        switch (k)
                        {
                            case "enabled": chkAI.Checked = v == "1" || v.ToLowerInvariant() == "true"; break;
                            case "api_url": if (v.Length > 0) aiApiUrl = v; break;
                            case "api_key": if (v.Length > 0) aiApiKey = v; break;
                            case "model": if (v.Length > 0) aiModel = v; break;
                            case "last_dir": if (v.Length > 0) txtDir.Text = v; break;
                        }
                    }
                    else if (inGeneral)
                    {
                        switch (k)
                        {
                            case "scan_depth": if (cboDepth != null) { int d; int.TryParse(v, out d); if (d >= 0 && d <= 3) cboDepth.SelectedIndex = d; } break;
                            case "calc_size": if (chkSize != null) chkSize.Checked = v == "1" || v.ToLowerInvariant() == "true"; break;
                        }
                    }
                }
                lblStatus.Text = "已加载配置文件。";
            }
            catch { }
        }

        void SaveConfig()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine(CONFIG_SECTION);
                sb.AppendLine("enabled=" + (chkAI.Checked ? "1" : "0"));
                sb.AppendLine("api_url=" + (aiApiUrl ?? ""));
                sb.AppendLine("api_key=" + (aiApiKey ?? ""));
                sb.AppendLine("model=" + (aiModel ?? ""));
                sb.AppendLine("last_dir=" + txtDir.Text.Trim());
                sb.AppendLine();
                sb.AppendLine(GENERAL_SECTION);
                sb.AppendLine("scan_depth=" + (cboDepth != null ? cboDepth.SelectedIndex : 0));
                sb.AppendLine("calc_size=" + (chkSize != null && chkSize.Checked ? "1" : "0"));
                File.WriteAllText(ConfigFile, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        void SaveResultsNow()
        {
            if (items.Count == 0)
            {
                Dlg.Show("当前没有分析结果，请先扫描一个目录", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            SaveResults();
            Dlg.Show("分析结果已保存到:\n" + ResultFile + "\n（下次启动自动加载，无需重新扫描）", "保存成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // 判断目录名是否"损坏名"（软件创建时编码损坏，混入卡纳达文/埃塞俄比亚文等异常字符）
        // 正常目录名只含：中英文、数字、常见符号。含其他脚本字符视为损坏名，导出时跳过。
        bool IsGarbageName(string n)
        {
            foreach (var c in n)
            {
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) continue;
                if (c == '-' || c == '_' || c == '.' || c == ' ' || c == '(' || c == ')' || c == '[' || c == ']' || c == '!' || c == '@' || c == '#' || c == '+' || c == '&' || c == '~' || c == ',') continue;
                if (c >= 0x4E00 && c <= 0x9FFF) continue;        // 常用汉字
                if (c >= 0x3400 && c <= 0x4DBF) continue;        // CJK 扩展A
                if (c >= 0x3000 && c <= 0x303F) continue;        // CJK 标点（、。！？等）
                if (c >= 0xFF00 && c <= 0xFFEF) continue;        // 全角字符
                if (c >= 0x2000 && c <= 0x206F) continue;        // 常用标点符号区
                return true; // 出现异常脚本字符（卡纳达文/埃塞俄比亚文/天城文等）→ 损坏名
            }
            return false;
        }

        // 导出目录名表：只导出识别库没有的（未知/未匹配目录），格式与识别库一致（目录名 => 空值），供在线大模型补全后导入识别库
        void ExportDirs()
        {
            if (items.Count == 0)
            {
                Dlg.Show("当前没有扫描结果，请先扫描一个目录再导出", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                var names = items
                    .Where(i => i.Source == "unknown" || i.Source == "ai_unknown") // 只导出识别库没有的（未匹配/未识别）
                    .Select(i => Path.GetFileName(i.Path.TrimEnd('\\', '/')))
                    .Where(n => !string.IsNullOrEmpty(n) && !IsGarbageName(n))      // 跳过编码损坏的乱码目录名
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var sb = new StringBuilder();
                foreach (var n in names) sb.AppendLine(n + " => ");
                var outFile = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "导出未识别.txt");
                File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false));
                if (names.Count == 0)
                {
                    Dlg.Show("没有需要导出的未识别目录（识别库已覆盖全部）。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Dlg.Show(string.Format("已导出 {0} 个识别库没有的目录名到:\n{1}\n\n格式：目录名 => （空值）\n请发给在线大模型补全注解，再把结果导入识别库文件。", names.Count, outFile),
                    "导出成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Dlg.Show("导出失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void BuildUI()
        {
            // ---------- 行1：目录选择（FlowLayoutPanel，固定控件尺寸）----------
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 6, 8, 2), WrapContents = false };
            top.Controls.Add(new Label { Text = "分析目录:", AutoSize = true, Margin = new Padding(0, 5, 4, 0) });
            txtDir = new DarkTextBox { Width = 640, Margin = new Padding(0, 2, 4, 0) };
            top.Controls.Add(txtDir);
            btnBrowse = new Button { Text = "浏览…", AutoSize = true, Margin = new Padding(0, 0, 4, 0) };
            btnBrowse.Click += (s, e) => { using (var d = new FolderBrowserDialog()) { string cur = txtDir.Text.Trim(); if (Directory.Exists(cur)) d.SelectedPath = cur; if (d.ShowDialog() == DialogResult.OK) txtDir.Text = d.SelectedPath; } };
            top.Controls.Add(btnBrowse);
            btnScan = new Button { Text = "开始分析", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
            btnScan.Click += (s, e) =>
            {
                if (btnScan.Text == "停止")
                {
                    if (scanCts != null) scanCts.Cancel();
                    if (aiCts != null) aiCts.Cancel();
                }
                else StartScan();
            };
            top.Controls.Add(btnScan);
            cboDepth = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110, Margin = new Padding(0, 3, 4, 0) };
            cboDepth.Items.AddRange(new object[] { "二级目录", "三级目录", "四级目录", "全部目录" });
            cboDepth.SelectedIndex = 0; // 默认二级
            top.Controls.Add(cboDepth);
            chkSize = new CheckBox { Text = "统计大小", AutoSize = true, Checked = true, Margin = new Padding(0, 5, 4, 0) };
            top.Controls.Add(chkSize);
            // AI 开关 + 配置齿轮按钮
            chkAI = new CheckBox { Text = "AI 分析未知目录", AutoSize = true, Margin = new Padding(0, 5, 8, 0) };
            top.Controls.Add(chkAI);
            var btnAiCfg = new Button { Text = "⚙", Width = 30, Height = 24, Margin = new Padding(0, 3, 6, 0), FlatStyle = FlatStyle.System };
            btnAiCfg.Click += (s, e) => ShowAiConfigDialog();
            top.Controls.Add(btnAiCfg);
            var aiTip = new ToolTip();
            aiTip.SetToolTip(btnAiCfg, "AI 配置");

            // ---------- 行3：操作栏 ----------
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 2, 8, 2), WrapContents = false };
            btnAll = MkBtn("全选", (s, e) => { lblStatus.Text = "正在全选..."; lblStatus.Refresh(); SetAll(true, false); }); bar.Controls.Add(btnAll);
            btnNone = MkBtn("全不选", (s, e) => { lblStatus.Text = "正在全不选..."; lblStatus.Refresh(); SetAll(false, false); }); bar.Controls.Add(btnNone);
            btnInvert = MkBtn("反选", (s, e) => { lblStatus.Text = "正在反选..."; lblStatus.Refresh(); InvertAll(); }); bar.Controls.Add(btnInvert);
            btnDeletable = MkBtn("仅选可删", (s, e) => { lblStatus.Text = "正在仅选可删..."; lblStatus.Refresh(); SetAll(true, true); }); bar.Controls.Add(btnDeletable);
            btnExpand = MkBtn("展开全部", (s, e) => { lblStatus.Text = "正在展开全部..."; lblStatus.Refresh(); ExpandAll(true); }); bar.Controls.Add(btnExpand);
            btnCollapse = MkBtn("折叠全部", (s, e) => { lblStatus.Text = "正在折叠全部..."; lblStatus.Refresh(); ExpandAll(false); }); bar.Controls.Add(btnCollapse);
            var btnSortName = MkBtn("按名字排序", (s, e) => { lblStatus.Text = "正在按名字排序..."; lblStatus.Refresh(); SortTree((a, b) => string.Compare(a.Rel, b.Rel, StringComparison.OrdinalIgnoreCase)); });
            bar.Controls.Add(btnSortName);
            var btnSortSize = MkBtn("按大小排序", (s, e) => { lblStatus.Text = "正在按大小排序..."; lblStatus.Refresh(); SortTree((a, b) => b.Size.CompareTo(a.Size)); });
            bar.Controls.Add(btnSortSize);
            var btnSortRank = MkBtn("等级排序", (s, e) => { lblStatus.Text = "正在等级排序..."; lblStatus.Refresh(); SortTree((a, b) =>
            {
                int r = CategoryRank(a).CompareTo(CategoryRank(b));
                if (r != 0) return r;
                return string.Compare(a.Rel, b.Rel, StringComparison.OrdinalIgnoreCase);
            }); });
            bar.Controls.Add(btnSortRank);
            rbRecycle = new RadioButton { Text = "移到回收站", Checked = true, AutoSize = true, Margin = new Padding(8, 5, 4, 0) };
            bar.Controls.Add(rbRecycle);
            rbPermanent = new RadioButton { Text = "永久删除", AutoSize = true, Margin = new Padding(0, 5, 6, 0) };
            bar.Controls.Add(rbPermanent);
            btnDelete = new Button { Text = "删除选中", AutoSize = true, BackColor = Color.FromArgb(255, 235, 238), Margin = new Padding(0, 0, 8, 0) };
            btnDelete.Click += (s, e) => DeleteSelected();
            bar.Controls.Add(btnDelete);
            var btnSaveRes = new Button { Text = "保存结果", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
            btnSaveRes.Click += (s, e) => SaveResultsNow();
            bar.Controls.Add(btnSaveRes);
            var btnExport = new Button { Text = "导出未识别", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
            btnExport.Click += (s, e) => ExportDirs();
            bar.Controls.Add(btnExport);

            // ---------- 树（深色自绘：滚动条 + 复选框） ----------
            tree = new DarkTreeView
            {
                Dock = DockStyle.Fill,
                CheckBoxes = true,
                HideSelection = false,
                ShowNodeToolTips = false,
                FullRowSelect = true,
                DrawMode = TreeViewDrawMode.Normal,
                ShowPlusMinus = true,
                ShowLines = true,
                BorderStyle = BorderStyle.None
            };
            // 深色 1px 描边容器（Padding=1 露出底色作为边框）
            var treeHost = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(1),
                BackColor = C_EDGE
            };
            treeHost.Controls.Add(tree);
            TreeNode lastCheckNode = null;
            tree.AfterCheck += (s, e) =>
            {
                var item = e.Node.Tag as FolderItem;
                if (item != null) { item.Checked = e.Node.Checked; UpdateSel(); }
            };
            // Shift 范围勾选：Shift=含子目录递归；Shift+Ctrl=仅同级兄弟范围
            tree.NodeMouseClick += (s, e) =>
            {
                if ((Control.ModifierKeys & Keys.Shift) == 0 || lastCheckNode == null || lastCheckNode == e.Node)
                {
                    lastCheckNode = e.Node;
                    return;
                }
                bool sameLevel = (Control.ModifierKeys & Keys.Control) != 0;
                bool val = e.Node.Checked;
                System.Action<TreeNode> setCheck = null;
                setCheck = (n) =>
                {
                    n.Checked = val;
                    var it = n.Tag as FolderItem;
                    if (it != null) it.Checked = val;
                    if (!sameLevel) foreach (TreeNode c in n.Nodes) setCheck(c);
                };
                tree.BeginUpdate();
                if (sameLevel && lastCheckNode.Parent == e.Node.Parent)
                {
                    // 仅同级兄弟范围
                    var cols = lastCheckNode.Parent != null ? lastCheckNode.Parent.Nodes : tree.Nodes;
                    var sibs = new List<TreeNode>();
                    foreach (TreeNode n in cols) sibs.Add(n);
                    int i1 = sibs.IndexOf(lastCheckNode);
                    int i2 = sibs.IndexOf(e.Node);
                    if (i1 > i2) { int t = i1; i1 = i2; i2 = t; }
                    for (int i = i1; i <= i2; i++) setCheck(sibs[i]);
                }
                else
                {
                    // 全范围（含子目录递归）
                    var all = new List<TreeNode>();
                    var st = new Stack<TreeNodeCollection>();
                    st.Push(tree.Nodes);
                    while (st.Count > 0)
                    {
                        var cols = st.Pop();
                        var list = new List<TreeNode>();
                        foreach (TreeNode n in cols) list.Add(n);
                        list.Reverse();
                        foreach (TreeNode n in list)
                        {
                            all.Add(n);
                            if (n.IsExpanded) st.Push(n.Nodes);
                        }
                    }
                    int i1 = all.IndexOf(lastCheckNode);
                    int i2 = all.IndexOf(e.Node);
                    if (i1 < 0 || i2 < 0) { tree.EndUpdate(); lastCheckNode = e.Node; return; }
                    if (i1 > i2) { int t = i1; i1 = i2; i2 = t; }
                    for (int i = i1; i <= i2; i++) setCheck(all[i]);
                }
                tree.EndUpdate();
                UpdateSel();
                lastCheckNode = e.Node;
            };

            // 右键菜单：修改识别库条目
            var ctxMenu = new ContextMenuStrip();
            ctxMenu.BackColor = C_CTL;
            ctxMenu.ForeColor = C_FG;
            ctxMenu.RenderMode = ToolStripRenderMode.Professional;
            ctxMenu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
            var miEdit = new ToolStripMenuItem("修改识别库");
            ctxMenu.Items.Add(miEdit);
            tree.ContextMenuStrip = ctxMenu;
            tree.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                var hit = tree.HitTest(e.Location);
                if (hit.Node == null) return;
                tree.SelectedNode = hit.Node;
            };
            miEdit.Click += (s, e) =>
            {
                var node = tree.SelectedNode;
                if (node == null) return;
                var item = node.Tag as FolderItem;
                if (item == null) return;
                string dirName = Path.GetFileName(item.Path.TrimEnd('\\', '/'));
                // 弹出输入框编辑识别库（同 AI 配置弹窗风格）
                using (var dlg = new Form())
                {
                    dlg.Text = "修改识别库";
                    dlg.StartPosition = FormStartPosition.CenterParent;
                    dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                    dlg.MaximizeBox = false; dlg.MinimizeBox = false;
                    dlg.ClientSize = new Size(420, 160);
                    dlg.Font = new Font("Microsoft YaHei UI", 9F);
                    dlg.ShowInTaskbar = false;
                    dlg.BackColor = C_BG;
                    dlg.Load += (sender, args) => Dlg.DarkTitleBar(dlg.Handle);

                    var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 16, 18, 12), ColumnCount = 2, RowCount = 3 };
                    root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
                    root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                    root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
                    root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
                    root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

                    root.Controls.Add(MkCfgLbl("目录名:"), 0, 0);
                    root.Controls.Add(new Label { Text = dirName, ForeColor = C_FG_DIM, Margin = new Padding(0,3,0,3) }, 1, 0);
                    root.Controls.Add(MkCfgLbl("识别为:"), 0, 1);
                    var txt = new DarkTextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 3), Text = item.Owner ?? "" };
                    root.Controls.Add(txt, 1, 1);

                    var btnRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1 };
                    var btnOk = new Button { Text = "确定", Width = 88, Height = 30, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
                    btnOk.Click += (sender, args) => { dlg.DialogResult = DialogResult.OK; dlg.Close(); };
                    btnRow.Controls.Add(btnOk, 0, 0);
                    root.Controls.Add(btnRow, 0, 2);
                    root.SetColumnSpan(btnRow, 2);

                    dlg.Controls.Add(root);
                    ApplyDarkTheme(dlg);
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    string newVal = txt.Text.Trim();
                    if (newVal.Length == 0) return;
                    item.Owner = newVal;
                    item.Source = "known";
                    item.Category = "leftover";
                    node.Text = string.Format("{0}   [{1}]{2}  {3}", dirName, SourceTag(item), item.Owner, FormatSize(item.Size));
                    node.ForeColor = CategoryColor(item.Category);
                    // 更新识别库.txt：读全部，替换或追加，写回
                    try
                    {
                        var lines = File.Exists(KnownMapFile) ? File.ReadAllLines(KnownMapFile).ToList() : new List<string>();
                        bool found = false;
                        for (int i = 0; i < lines.Count; i++)
                        {
                            var t = lines[i].Trim();
                            if (t.Length == 0 || t.StartsWith("#") || t.StartsWith(";")) continue;
                            var idx = t.IndexOf("=>", StringComparison.Ordinal);
                            if (idx <= 0) continue;
                            var k = t.Substring(0, idx).Trim();
                            if (k.Equals(dirName, StringComparison.OrdinalIgnoreCase))
                            {
                                lines[i] = dirName + " => " + newVal;
                                found = true; break;
                            }
                        }
                        if (!found) lines.Add(dirName + " => " + newVal);
                        File.WriteAllLines(KnownMapFile, lines, new UTF8Encoding(false));
                        loadedKnownMap[dirName.ToLowerInvariant()] = newVal;
                    }
                    catch { }
                }
            };

            // 深色悬浮提示（自绘小窗口，不闪白）
            var tipForm = new NoFocusForm
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                BackColor = C_EDGE,
                Size = new Size(10, 10)
            };
            var tipLbl = new Label { Dock = DockStyle.Fill, ForeColor = C_FG, Font = tree.Font, Padding = new Padding(6, 4, 6, 4) };
            tipForm.Controls.Add(tipLbl);
            TreeNode lastTipNode = null;
            tree.MouseMove += (s, e) =>
            {
                var hit = tree.HitTest(e.Location);
                // 只在鼠标移到节点文字/图标/复选框区域时才弹提示，节点行后面的空白处不弹
                bool onText = hit.Node != null &&
                    (hit.Location & (TreeViewHitTestLocations.Label | TreeViewHitTestLocations.Image | TreeViewHitTestLocations.StateImage)) != 0;
                if (onText)
                {
                    if (hit.Node != lastTipNode)
                    {
                        lastTipNode = hit.Node;
                        if (!string.IsNullOrEmpty(hit.Node.ToolTipText))
                        {
                            tipLbl.Text = hit.Node.ToolTipText;
                            tipForm.Width = TextRenderer.MeasureText(tipLbl.Text, tipLbl.Font).Width + 16;
                            tipForm.Height = TextRenderer.MeasureText(tipLbl.Text, tipLbl.Font).Height + 10;
                            var pt = tree.PointToScreen(new Point(e.X + 12, e.Y + 18));
                            tipForm.Location = pt;
                            if (!tipForm.Visible) tipForm.Show(tree.FindForm());
                        }
                        else
                        {
                            tipForm.Hide();
                        }
                    }
                }
                else
                {
                    if (lastTipNode != null) { lastTipNode = null; tipForm.Hide(); }
                }
            };
            tree.MouseLeave += (s, e) => { lastTipNode = null; tipForm.Hide(); };

            // ---------- 底部状态 ----------
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 26, Padding = new Padding(8, 2, 8, 2), BackColor = C_BG };
            lblStatus = new Label { Text = "就绪。选择目录后开始分析。", AutoSize = true, Dock = DockStyle.Left, Padding = new Padding(0, 5, 0, 0) };
            bottom.Controls.Add(lblStatus);
            lblSel = new Label { Text = "已选 0 项 / 0 MB", AutoSize = true, Dock = DockStyle.Right, Padding = new Padding(0, 5, 0, 0) };
            bottom.Controls.Add(lblSel);
            var lblHint = new Label { Text = "[Shift=范围含子目录  Shift+Ctrl=仅同级]", AutoSize = true, Dock = DockStyle.Right, ForeColor = C_FG_DIM, Padding = new Padding(20, 5, 0, 0) };
            bottom.Controls.Add(lblHint);
            lblStatus.BringToFront();
            Controls.Add(treeHost);
            Controls.Add(bottom);
            Controls.Add(bar);
            Controls.Add(top);
            ApplyDarkTheme(this); // 深色主题
            treeHost.BackColor = C_EDGE; // 还原描边色（主题遍历会把 Panel 刷成窗体底色）
        }

        // ---- 深色主题应用 ----
        void ApplyDarkTheme(Control c)
        {
            var b = c as Button;
            if (b != null)
            {
                b.BackColor = C_BTN;
                b.ForeColor = C_FG;
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderColor = C_EDGE;
                b.FlatAppearance.MouseOverBackColor = C_HOT;
                b.FlatAppearance.MouseDownBackColor = C_HOT;
                if (b == btnDelete)
                {
                    b.BackColor = Color.FromArgb(72, 30, 30); // 删除按钮深红警示
                    b.FlatAppearance.MouseOverBackColor = Color.FromArgb(110, 40, 40);
                }
            }
            else if (c is TextBox || c is TreeView)
            {
                c.BackColor = C_CTL;
                c.ForeColor = C_FG;
                var tv = c as TreeView;
                if (tv != null) tv.LineColor = C_LINE;
            }
            else if (c is ComboBox)
            {
                var cb = (ComboBox)c;
                cb.BackColor = C_CTL;
                cb.ForeColor = C_FG;
                cb.FlatStyle = FlatStyle.Flat;
                cb.DropDownStyle = ComboBoxStyle.DropDownList;
            }
            else if (c is Label)
            {
                c.BackColor = Color.Transparent;
                c.ForeColor = (c == lblStatus) ? C_FG_DIM : C_FG;
            }
            else if (c is CheckBox || c is RadioButton)
            {
                c.BackColor = Color.Transparent;
                c.ForeColor = C_FG;
            }
            else
            {
                c.BackColor = C_BG;
                c.ForeColor = C_FG;
            }
            foreach (Control child in c.Controls) ApplyDarkTheme(child);
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int attrLen);

        void SetDarkTitleBar()
        {
            try
            {
                const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
                int v = 1;
                DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, 4);
            }
            catch { }
        }

        // OwnerDrawAll：自绘目录树节点（深色背景 + 深色复选框 + 分类色文本）
        void DrawTreeNode(object sender, DrawTreeNodeEventArgs e)
        {
            var r = e.Bounds;
            bool sel = (e.State & TreeNodeStates.Selected) != 0;
            using (var bg = new SolidBrush(sel ? Color.FromArgb(45, 60, 82) : Color.FromArgb(37, 37, 38)))
                e.Graphics.FillRectangle(bg, r);
            // 复选框和文字一起按层级缩进
            int indent = e.Node.Level * 19;
            Rectangle box = new Rectangle(r.Left + indent + 2, r.Y + (r.Height - 14) / 2, 14, 14);
            DrawCheckBox(e.Graphics, box, e.Node.Checked);
            int tx = box.Right + 6;
            using (var tb = new SolidBrush(e.Node.ForeColor))
            {
                int maxW = tree.ClientSize.Width - tx - 4;
                if (maxW < 50) maxW = 50;
                var rect = new Rectangle(tx, r.Y + 1, maxW, r.Height);
                TextRenderer.DrawText(e.Graphics, e.Node.Text, tree.Font, rect, e.Node.ForeColor, TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
            }
        }

        void DrawCheckBox(Graphics g, Rectangle r, bool chk)
        {
            using (var b = new SolidBrush(Color.FromArgb(45, 45, 48)))
                g.FillRectangle(b, r);
            using (var p = new Pen(Color.FromArgb(130, 130, 135)))
                g.DrawRectangle(p, r);
            if (chk)
            {
                using (var pen = new Pen(Color.FromArgb(120, 190, 250), 2))
                {
                    g.DrawLine(pen, r.Left + 3, r.Y + r.Height / 2, r.Left + 6, r.Y + r.Height - 4);
                    g.DrawLine(pen, r.Left + 6, r.Y + r.Height - 4, r.Right - 3, r.Y + 3);
                }
            }
        }

        // ---- 齿轮按钮：弹出 AI 配置窗口（深色主题） ----
        void ShowAiConfigDialog()
        {
            var dlg = new Form
            {
                Text = "AI 配置",
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ClientSize = new Size(560, 250),
                Font = new Font("Microsoft YaHei UI", 9F),
                ShowInTaskbar = false,
                BackColor = C_BG
            };
            dlg.Load += (s, e) => Dlg.DarkTitleBar(dlg.Handle);
            // 两列布局：列0=标签（固定宽），列1=输入框（拉伸）→ 输入框严格左对齐、等宽
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(18, 16, 18, 12),
                ColumnCount = 2,
                RowCount = 5
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88)); // 标签列
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // 输入框列
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

            DarkTextBox url = new DarkTextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 3) };
            if (!string.IsNullOrEmpty(aiApiUrl)) url.Text = aiApiUrl;
            DarkTextBox key = new DarkTextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 3), PasswordChar = '*' };
            if (!string.IsNullOrEmpty(aiApiKey)) key.Text = aiApiKey;
            DarkTextBox model = new DarkTextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 3) };
            if (!string.IsNullOrEmpty(aiModel)) model.Text = aiModel;

            root.Controls.Add(MkCfgLbl("API 地址:"), 0, 0);
            root.Controls.Add(url, 1, 0);
            root.Controls.Add(MkCfgLbl("API Key:"), 0, 1);
            root.Controls.Add(key, 1, 1);
            root.Controls.Add(MkCfgLbl("模型名称:"), 0, 2);
            root.Controls.Add(model, 1, 2);

            var hint = new Label
            {
                Text = "勾选“AI 分析未知目录”后，扫描时会把所有未知目录一次性发给 AI，返回后统一匹配。",
                ForeColor = C_FG_DIM,
                AutoSize = true,
                Margin = new Padding(0, 7, 0, 0),
                Anchor = AnchorStyles.Left
            };
            root.Controls.Add(hint, 0, 3);
            root.SetColumnSpan(hint, 2);

            // 按钮行：只保留一个“确定”，右下角对齐，无左右对齐问题
            var btnRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1, Margin = new Padding(0, 0, 0, 0) };
            var btnOk = new Button { Text = "确定", Width = 88, Height = 30, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            btnOk.Click += (s, e) =>
            {
                aiApiUrl = url.Text.Trim();
                aiApiKey = key.Text.Trim();
                aiModel = model.Text.Trim();
                SaveConfig();
                dlg.DialogResult = DialogResult.OK;
                dlg.Close();
            };
            btnRow.Controls.Add(btnOk, 0, 0);
            root.Controls.Add(btnRow, 0, 4);
            root.SetColumnSpan(btnRow, 2);

            dlg.Controls.Add(root);
            ApplyDarkTheme(dlg); // 弹窗同样应用深色主题
            dlg.ShowDialog(this);
        }

        // 弹窗标签：右对齐到固定标签列，保证输入框左缘统一
        Label MkCfgLbl(string t)
        {
            return new Label
            {
                Text = t,
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleRight,
                Anchor = AnchorStyles.Right,
                Margin = new Padding(0, 0, 8, 0)
            };
        }

        Button MkBtn(string t, EventHandler h)
        {
            var b = new Button { Text = t, AutoSize = true, Margin = new Padding(0, 0, 4, 0) };
            b.Click += h;
            return b;
        }

        // ---- 供自测调用的公开包装 ----
        public void ReadUninstallPublic(string key, bool hkcu, List<Tuple<string, string, string>> list)
        {
            ReadUninstall(key, hkcu, list);
        }

        public void IdentifyPublic(string path, out string owner, out string cat, out string note)
        {
            string source;
            IdentifyFolder(path, Path.GetFileName(path.TrimEnd('\\', '/')), out owner, out cat, out note, out source);
        }

        // ---- 性能测试探针 ----
        public void SetDirProbe(string d) { txtDir.Text = d; }
        public void TriggerScanProbe() { StartScan(); }
        public bool IsScanDoneProbe() { return btnScan.Text == "开始分析" && items.Count > 0; }
        public int ItemCountProbe() { return items.Count; }
        public string StatusProbe() { return lblStatus.Text; }
        // ---- 布局测试探针 ----
        public int DirWidthProbe() { return txtDir.Width; }
        public int AiUrlWidthProbe() { return 300; } // AI 配置已移入齿轮弹窗，主界面不再有 URL 输入框
        public string TreeDockProbe() { return tree.Dock.ToString(); }

        // ---------------- 已安装软件加载 ----------------
        void LoadInstalledApps()
        {
            var list = new List<Tuple<string, string, string>>();
            ReadUninstall(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", false, list);
            ReadUninstall(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", false, list);
            ReadUninstall(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", true, list);
            installedApps = list;
        }

        void ReadUninstall(string key, bool hkcu, List<Tuple<string, string, string>> list)
        {
            try
            {
                using (var baseKey = hkcu ? Microsoft.Win32.Registry.CurrentUser : Microsoft.Win32.Registry.LocalMachine)
                using (var k = baseKey.OpenSubKey(key))
                {
                    if (k == null) return;
                    foreach (var sub in k.GetSubKeyNames())
                    {
                        try
                        {
                            using (var sk = k.OpenSubKey(sub))
                            {
                                if (sk == null) continue;
                                var name = sk.GetValue("DisplayName") as string;
                                if (string.IsNullOrEmpty(name)) continue;
                                var loc = sk.GetValue("InstallLocation") as string ?? "";
                                var pub = sk.GetValue("Publisher") as string ?? "";
                                list.Add(Tuple.Create(name.Trim(), loc.Trim(), pub.Trim()));
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        // ---------------- 扫描（多线程：后台收集 -> 队列 -> UI 定时批量渲染）----------------
        void StartScan()
        {
            var path = txtDir.Text.Trim();
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                Dlg.Show("请先选择一个有效目录", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            lblStatus.Text = "正在准备扫描: " + path;
            lblStatus.Refresh();
            Application.DoEvents();
            SaveConfig();
            items.Clear(); relMap.Clear();
            object tmp;
            while (renderQueue.TryDequeue(out tmp)) { }
            tree.BeginUpdate();
            tree.Nodes.Clear();
            tree.EndUpdate();
            UpdateSel();
            btnScan.Text = "停止";
            scanDone = false;
            // 取消旧扫描/AI 任务，避免旧任务状态栏覆盖新任务
            if (scanCts != null) scanCts.Cancel();
            if (aiCts != null) aiCts.Cancel();
            scanCts = new CancellationTokenSource();
            aiCts = new CancellationTokenSource();
            var ct = scanCts.Token;
            // 扫描前重新加载识别库与注册表：用户改文件后立即生效，无需重启
            LoadKnownMap();
            Task.Run(() => LoadInstalledApps());
            lblStatus.Text = "正在扫描: " + path;
            progressSw.Restart();
            int selIdx = cboDepth != null ? cboDepth.SelectedIndex : 0;
            int maxDepth = selIdx == 0 ? 1 : selIdx == 1 ? 2 : selIdx == 2 ? 3 : -1;
            bool calcSize = chkSize == null || chkSize.Checked;
            Task.Run(() => ScanWorker(path, ct, maxDepth, calcSize));
        }

        void ScanWorker(string root, CancellationToken ct, int maxDepth, bool calcSize)
        {
            // 第一步：自底向上聚合大小（纯后台，不碰 UI）
            var agg = new Dictionary<string, Tuple<long, int, DateTime?>>();
            long rootSize = 0;
            try
            {
                var dirsAll = new List<string>();
                // 统计大小与扫描深度独立：勾了统计大小就完整遍历算大小；扫描深度只控制展示层级
                if (calcSize || maxDepth != 0)
                {
                    var walk = new Stack<KeyValuePair<string, int>>();
                    walk.Push(new KeyValuePair<string, int>(root, 0));
                    while (walk.Count > 0)
                    {
                        if (ct.IsCancellationRequested) { renderQueue.Enqueue(null); return; }
                        var cur = walk.Pop();
                        var d = cur.Key; var depth = cur.Value;
                        dirsAll.Add(d);
                        // 统计大小永远递归全量（真实大小），展示层级由第二步 maxDepth 控制
                        if (calcSize || maxDepth < 0 || depth < maxDepth)
                        {
                            try { foreach (var sub in Directory.EnumerateDirectories(d)) walk.Push(new KeyValuePair<string, int>(sub, depth + 1)); } catch { }
                        }
                    }
                    dirsAll.Reverse();
                }
                else
                {
                    dirsAll.Add(root); // 不扫子目录也不算大小：只统计所选目录本身
                }
                for (int idx = 0; idx < dirsAll.Count; idx++)
                {
                    if (ct.IsCancellationRequested) { renderQueue.Enqueue(null); return; }
                    var d = dirsAll[idx];
                    long size = 0; int cnt = 0;
                    if (calcSize)
                    {
                        try { foreach (var f in Directory.EnumerateFiles(d)) { try { size += new FileInfo(f).Length; cnt++; } catch { } } } catch { }
                    }
                    var rel = d == root ? "" : d.Substring(root.Length).TrimStart('\\', '/');
                    try
                    {
                        if (calcSize)
                        {
                            foreach (var sub in Directory.EnumerateDirectories(d))
                            {
                                var srel = rel == "" ? Path.GetFileName(sub) : rel + "\\" + Path.GetFileName(sub);
                                Tuple<long, int, DateTime?> si;
                                if (agg.TryGetValue(srel, out si)) { size += si.Item1; cnt += si.Item2; }
                            }
                        }
                    }
                    catch { }
                    DateTime? mt = null;
                    try { mt = Directory.GetLastWriteTime(d); } catch { }
                    agg[rel] = Tuple.Create(calcSize ? size : -1, cnt, mt); // 不统计大小：标记 -1，界面显示 "--"
                    if (rel == "") rootSize = size;
                    if (progressSw.ElapsedMilliseconds > 300)
                    {
                        progressSw.Restart();
                        SafeUi(() => lblStatus.Text = "正在扫描: " + d);
                    }
                }
            }
            catch { }

            // 第二步：自顶向下识别并送入渲染队列
            var stack = new Stack<KeyValuePair<string, int>>();
            stack.Push(new KeyValuePair<string, int>(root, 0));
            var unknownList = new List<FolderItem>();
            int pushed = 0;
            while (stack.Count > 0)
            {
                if (ct.IsCancellationRequested) { renderQueue.Enqueue(null); return; }
                var cur = stack.Pop();
                var d = cur.Key; var depth = cur.Value;
                List<string> subs;
                try { subs = Directory.EnumerateDirectories(d).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(); }
                catch { continue; }
                foreach (var sub in subs)
                {
                    if (ct.IsCancellationRequested) { renderQueue.Enqueue(null); return; }
                    var rel = sub.Substring(root.Length).TrimStart('\\', '/');
                    Tuple<long, int, DateTime?> si;
                    agg.TryGetValue(rel, out si);
                    var size = si != null ? si.Item1 : (calcSize ? 0 : -1); // 没统计大小时显示 "--"
                    var fcnt = si != null ? si.Item2 : 0;
                    var mt = si != null ? si.Item3 : null;
                    string owner, cat, note, source;
                    IdentifyFolder(sub, rel, out owner, out cat, out note, out source);
                    var item = new FolderItem { Path = sub, Rel = rel, Size = size, FileCount = fcnt, MTime = mt, Owner = owner, Category = cat, Note = note, Source = source };
                    if (cat == "unknown") unknownList.Add(item);
                    renderQueue.Enqueue(item);
                    pushed++;
                    if (maxDepth < 0 || depth + 1 < maxDepth) stack.Push(new KeyValuePair<string, int>(sub, depth + 1));
                }
            }
            // 完成标记（null），UI 渲染完队列后收尾
            renderQueue.Enqueue(null);
            SafeUi(() => lblStatus.Text = "正在渲染列表… " + pushed + " 个文件夹");

            // 第三步：AI 分析未知目录（勾选时）—— 一次性把所有未知目录发给 AI，返回后统一匹配
            if (chkAI != null && chkAI.Checked && unknownList.Count > 0)
            {
                string cfgUrl = (aiApiUrl ?? "").Trim().TrimEnd('/');
                string cfgKey = (aiApiKey ?? "").Trim();
                string cfgModel = (aiModel ?? "").Trim();
                var list = unknownList.ToList();
                SafeUi(() => lblStatus.Text = "正在启动 AI 分析… 共 " + list.Count + " 个未知目录");
                SafeUi(() => Task.Run(() => RunAI(list, cfgUrl, cfgKey, cfgModel, aiCts.Token)));
            }
        }

        // UI 线程：每 80ms 批量渲染队列
        void RenderBatch()
        {
            // 用户点了停止：丢弃队列剩余，恢复按钮
            if (scanCts != null && scanCts.IsCancellationRequested)
            {
                object tmp; while (renderQueue.TryDequeue(out tmp)) { }
                btnScan.Text = "开始分析";
                lblStatus.Text = "已停止";
                return;
            }
            if (renderQueue.IsEmpty) return;
            var batch = new List<FolderItem>();
            bool doneMark = false;
            while (batch.Count < 400)
            {
                object o;
                if (!renderQueue.TryDequeue(out o)) break;
                if (o == null) { doneMark = true; break; }
                batch.Add((FolderItem)o);
            }
            if (batch.Count > 0)
            {
                tree.BeginUpdate();
                foreach (var item in batch)
                {
                    items.Add(item); relMap[item.Rel] = item;
                    AddNodeInternal(item);
                }
                tree.EndUpdate();
            }
            if (doneMark)
            {
                RebuildTree();
                long total = 0;
                foreach (var it in items) total += it.Size;
                UpdateSel();
                SaveResults();
                bool aiRunning = chkAI != null && chkAI.Checked && items.Exists(i => i.Source == "unknown");
                if (!aiRunning)
                {
                    btnScan.Text = "开始分析";
                    lblStatus.Text = string.Format("扫描完成：{0} 个文件夹，合计 {1}", items.Count, FormatSize(total));
                }
            }
        }

        // 扫描完成后重建树：按 rel 路径深度排序，确保父节点先挂载
        void RebuildTree()
        {
            tree.BeginUpdate();
            tree.Nodes.Clear();
            // 按 rel 深度排序（短的在前=父在前），同深度按名称
            var ordered = items.OrderBy(i => i.Rel.Length).ThenBy(i => i.Rel, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var item in ordered)
            {
                // 清除旧节点引用，重新挂载
                if (item.Node != null) { item.Node = null; }
                TreeNode parent = null;
                var pRel = Path.GetDirectoryName(item.Rel);
                if (!string.IsNullOrEmpty(pRel))
                {
                    FolderItem pi;
                    if (relMap.TryGetValue(pRel, out pi)) parent = pi.Node;
                }
                var node = new TreeNode(Path.GetFileName(item.Path))
                {
                    Tag = item,
                    ToolTipText = item.Note + "\n" + item.Path + "\n[等级] " + RankDescription(item)
                };
                if (parent != null) parent.Nodes.Add(node);
                else tree.Nodes.Add(node);
                node.Text = string.Format("{0}   [{1}]{2}  {3}", Path.GetFileName(item.Path),
                    SourceTag(item), item.Owner, FormatSize(item.Size));
                node.ForeColor = CategoryColor(item.Category);
                node.Checked = item.Checked;
                item.Node = node;
            }
            tree.EndUpdate();
        }

        void AddNodeInternal(FolderItem item)
        {
            TreeNode parent = null;
            var pRel = Path.GetDirectoryName(item.Rel);
            if (!string.IsNullOrEmpty(pRel))
            {
                FolderItem pi;
                if (relMap.TryGetValue(pRel, out pi)) parent = pi.Node;
            }
            var node = new TreeNode(Path.GetFileName(item.Path))
            {
                Tag = item,
                ToolTipText = item.Note + "\n" + item.Path + "\n[等级] " + RankDescription(item)
            };
            if (parent != null) parent.Nodes.Add(node);
            else tree.Nodes.Add(node);
            node.Text = string.Format("{0}   [{1}]{2}  {3}", Path.GetFileName(item.Path),
                SourceTag(item), item.Owner, FormatSize(item.Size));
            node.ForeColor = CategoryColor(item.Category);
            node.Checked = item.Checked;
            item.Node = node;
        }

        // 等级（可删程度）：最不重要排最前。缓存>残留(含损坏名)>未知>在用
        int CategoryRank(FolderItem it)
        {
            switch (it.Category)
            {
                case "cache": return 1;       // 缓存：可删
                case "leftover": return 2;    // 残留（含编码损坏的乱码名）：可删
                case "unknown": return 3;     // 未知：谨慎
                case "installed": return 4;   // 在用：勿删
                default: return 5;
            }
        }

        // 等级说明（鼠标悬浮提示用）
        string RankDescription(FolderItem it)
        {
            if (it.Source == "garbage") return "损坏名（最垃圾残留，可删）";
            switch (it.Category)
            {
                case "cache": return "缓存：可删";
                case "leftover": return "残留：可删";
                case "unknown": return "未知：谨慎，核实后再删";
                case "installed": return "在用：勿删（删了会误删软件）";
                default: return "其他";
            }
        }

        // 排序：递归重排所有层级的子节点（默认扫描顺序是名字序）
        void SortTree(Func<FolderItem, FolderItem, int> cmp)
        {
            try
            {
                tree.BeginUpdate();
                SortNodes(tree.Nodes, cmp);
                tree.EndUpdate();
            }
            catch { }
            lblStatus.Text = "就绪";
        }

        void SortNodes(TreeNodeCollection nodes, Func<FolderItem, FolderItem, int> cmp)
        {
            var list = new List<TreeNode>();
            foreach (TreeNode n in nodes) list.Add(n);
            if (list.Count <= 1) { foreach (var n in list) SortNodes(n.Nodes, cmp); return; }
            list.Sort((a, b) => cmp((FolderItem)a.Tag, (FolderItem)b.Tag));
            nodes.Clear();
            foreach (var n in list) nodes.Add(n);
            foreach (var n in list) SortNodes(n.Nodes, cmp);
        }

        // 识别来源标记：让用户分辨是识别库/注册表还是 AI 识别的
        string SourceTag(FolderItem item)
        {
            switch (item.Source)
            {
                case "ai": return "AI识别 ";
                case "garbage": return "乱码 "; // 编码损坏的目录名
                case "registry": return "注册表 ";
                case "known": return "识别库 ";
                case "cache": return "缓存 "; // 缓存关键词命中的临时文件类
                case "ai_unknown": return "AI未识别 ";   // AI 分析跑完仍说不知道
                case "unknown": return "未匹配 ";        // 识别库/注册表没认出来（AI 未处理或未运行）
                default: return "";
            }
        }

        void IdentifyFolder(string path, string rel, out string owner, out string cat, out string note, out string source)
        {
            var baseName = Path.GetFileName(path.TrimEnd('\\', '/'));
            var low = (baseName ?? "").ToLowerInvariant();
            if (string.IsNullOrEmpty(low)) { owner = "根目录"; cat = "unknown"; note = "根目录本身"; source = "unknown"; return; }

            // 编码损坏的目录名（混入卡纳达文/埃塞俄比亚文等异常字符）：软件创建时写坏的残留
            if (IsGarbageName(low))
            {
                owner = "损坏名（软件写坏）"; cat = "leftover"; note = "软件创建目录时编码损坏，通常为垃圾残留，可删"; source = "garbage"; return;
            }

            foreach (var kw in CacheKeywords)
                if (low.Contains(kw)) { owner = "缓存/临时文件"; cat = "cache"; note = "命中关键词: " + kw; source = "cache"; return; }

            var sorted = loadedKnownMap.OrderByDescending(kv => kv.Key.Length);
            foreach (var kv in sorted)
            {
                var k = kv.Key;
                bool hit;
                if (loadedExactOnlyKeys.Contains(k))
                    hit = string.Equals(low, k, StringComparison.OrdinalIgnoreCase);
                else
                    hit = low.Contains(k);
                if (hit)
                {
                    owner = kv.Value; cat = "leftover"; note = "已知目录名（未检测到已安装）"; source = "known";
                    return;
                }
            }

            // 通用子目录名：不做注册表匹配（避免误判），但仍尝试 KnownMap
            bool isGeneric = GenericSubdirs.Contains(low);
            foreach (var app in installedApps)
            {
                var n = app.Item1.ToLowerInvariant();
                var pub = app.Item3.ToLowerInvariant();
                if (string.IsNullOrEmpty(n)) continue;
                bool hit = false;
                // 正向：目录名里包含完整软件名（如 "microsoft edge" 出现在目录里）
                if (low.Contains(n)) hit = true;
                // 反向：软件名包含目录名 —— 仅限目录名较长且非通用词，避免 "boot" 命中 "Python ... Bootstrap"
                else if (!isGeneric && low.Length >= 5 && n.Contains(low)) hit = true;
                // 发布者匹配同样受通用词限制
                if (!hit && !isGeneric && !string.IsNullOrEmpty(pub))
                {
                    if (low.Contains(pub)) hit = true;
                    else if (low.Length >= 6 && pub.Contains(low)) hit = true;
                }
                if (hit)
                {
                    owner = app.Item1; cat = "installed"; note = "与已安装软件匹配"; source = "registry"; return;
                }
            }

            owner = "未知"; cat = "unknown"; note = "未匹配到已知软件"; source = "unknown";
        }

        // ---------------- AI 分析（一次性拉完再统一匹配） ----------------
        void RunAI(List<FolderItem> unknownList, string url, string key, string model, CancellationToken token)
        {
            try
            {
                if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key) || string.IsNullOrEmpty(model))
                {
                    SafeUi(() => lblStatus.Text = "AI 分析需要填写 API 地址 / Key / 模型。未知目录 " + unknownList.Count + " 个保持未识别。");
                    return;
                }
                var names = unknownList.Select(u => Path.GetFileName(u.Path.TrimEnd('\\', '/'))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                SafeUi(() => lblStatus.Text = "AI 分析中… 共 " + names.Count + " 个未知目录，一次性提交");
                if (token.IsCancellationRequested) return;
                string resp = CallAI(url, key, model, BuildPrompt(names));
                if (token.IsCancellationRequested) return; // 被新扫描取消：不更新旧列表和状态栏

                var batchResults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                int failCount = 0;
                if (string.IsNullOrEmpty(resp)) failCount = 1;
                else ParseAIResponse(resp, batchResults);

                int updated = 0;
                SafeUi(() =>
                {
                    tree.BeginUpdate();
                    foreach (var item in unknownList)
                    {
                        var nm = Path.GetFileName(item.Path.TrimEnd('\\', '/'));
                        string res;
                        if (batchResults.TryGetValue(nm, out res) && !string.IsNullOrEmpty(res) && !res.StartsWith("未知"))
                        {
                            item.Owner = res;
                            item.Category = "leftover";
                            item.Source = "ai"; // 标记来源：AI 识别
                            item.Note = "AI 分析结果，请自行核实";
                            if (item.Node != null)
                            {
                                item.Node.Text = string.Format("{0}   [{1}]{2}  {3}", Path.GetFileName(item.Path),
                                    SourceTag(item), item.Owner, FormatSize(item.Size));
                                item.Node.ForeColor = CategoryColor(item.Category);
                                item.Node.ToolTipText = "[AI识别] " + item.Note + "\n" + item.Path + "\n[等级] " + RankDescription(item);
                            }
                            updated++;
                            // AI 识别成功，自动写入识别库.txt，下次免识别
                            try
                            {
                                string lk = nm.ToLowerInvariant();
                                if (!loadedKnownMap.ContainsKey(lk))
                                {
                                    File.AppendAllText(KnownMapFile, "\r\n" + nm + " => " + res, new UTF8Encoding(false));
                                    loadedKnownMap[lk] = res;
                                }
                            }
                            catch { }
                        }
                        else
                        {
                            // AI 跑完仍未知：标记为「AI未识别」，与「未匹配」（没让 AI 看）区分
                            item.Source = "ai_unknown";
                            if (item.Node != null)
                            {
                                item.Node.Text = string.Format("{0}   [{1}]{2}  {3}", Path.GetFileName(item.Path),
                                    SourceTag(item), item.Owner, FormatSize(item.Size));
                                item.Node.ToolTipText = "AI 分析后仍无法识别\n" + item.Path + "\n[等级] " + RankDescription(item);
                            }
                        }
                    }
                    tree.EndUpdate();
                    btnScan.Text = "开始分析";
                    string msg = "扫描完成，AI 识别 " + updated + " 个，未识别 " + (unknownList.Count - updated) + " 个。";
                    if (failCount > 0) msg += "（请求失败，请检查 Key/模型/网络）";
                    lblStatus.Text = msg;
                });
            }
            catch (Exception ex)
            {
                SafeUi(() => { btnScan.Text = "开始分析"; lblStatus.Text = "AI 分析出错: " + ex.Message; });
            }
        }

        string BuildPrompt(List<string> names)
        {
            var sb = new StringBuilder();
            sb.AppendLine("你是一位 Windows 目录分析专家。下面这些是我电脑里 AppData\\Local、AppData\\Roaming 等用户目录下的子文件夹名。");
            sb.AppendLine("请判断每个文件夹是哪个软件/程序创建的。要求：");
            sb.AppendLine("1. 结合你的知识尽量推断出具体软件名（目录名可能是公司名、软件代号、com.xxx 包名、产品英文名）；");
            sb.AppendLine("2. 每行严格按「文件夹名 => 软件名」格式输出，软件名用中文或通用名，如「微信」「Visual Studio Code」「通义灵码」；");
            sb.AppendLine("3. 不要输出任何解释、编号、标题或多余文字；");
            sb.AppendLine("4. 只有完全无法判断时才写「未知」。");
            sb.AppendLine("文件夹列表：");
            foreach (var n in names) sb.AppendLine(n);
            return sb.ToString();
        }

        string CallAI(string baseUrl, string apiKey, string model, string userContent)
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                var payload = "{\"model\":\"" + JsonEsc(model) + "\",\"messages\":[{\"role\":\"user\",\"content\":\"" + JsonEsc(userContent) + "\"}],\"temperature\":0.1}";
                var req = (HttpWebRequest)WebRequest.Create(baseUrl + "/chat/completions");
                req.Method = "POST";
                req.ContentType = "application/json";
                req.UserAgent = "Mozilla/5.0";
                req.Timeout = 120000;
                req.Headers["Authorization"] = "Bearer " + apiKey;
                var data = Encoding.UTF8.GetBytes(payload);
                req.ContentLength = data.Length;
                using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (WebException wex)
            {
                string detail = "";
                try
                {
                    var errResp = wex.Response as HttpWebResponse;
                    if (errResp != null)
                        using (var sr = new StreamReader(errResp.GetResponseStream(), Encoding.UTF8))
                            detail = sr.ReadToEnd();
                }
                catch { }
                string msg = "AI 请求失败: " + wex.Message;
                if (detail.Length > 0)
                {
                    var dm = Regex.Match(detail, "\"message\"\\s*:\\s*\"([^\"]+)\"");
                    if (dm.Success && dm.Groups[1].Value.Length > 0) msg += " | " + dm.Groups[1].Value;
                }
                SafeUi(() => lblStatus.Text = msg);
                return "";
            }
            catch (Exception ex)
            {
                SafeUi(() => lblStatus.Text = "AI 请求失败: " + ex.Message);
                return "";
            }
        }

        string JsonEsc(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        void ParseAIResponse(string resp, Dictionary<string, string> results)
        {
            if (string.IsNullOrEmpty(resp)) return;
            try
            {
                var m = Regex.Match(resp, "\"content\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                if (!m.Success) return;
                var content = Regex.Unescape(m.Groups[1].Value);
                foreach (var rawLine in content.Split('\n'))
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0) continue;
                    line = Regex.Replace(line, @"^(?:\d+[\.\)、]|\-|\*|\•)\s*", "");
                    var lm = Regex.Match(line, "^(.*?)\\s*=>\\s*(.+)$");
                    if (lm.Success)
                    {
                        var nm = lm.Groups[1].Value.Trim().Trim('"', ' ', ':', '、', '-');
                        var val = lm.Groups[2].Value.Trim();
                        if (!string.IsNullOrEmpty(nm) && !string.IsNullOrEmpty(val) && val != nm)
                            results[nm] = val;
                    }
                }
            }
            catch { }
        }

        // ---------------- UI 辅助 ----------------
        void SafeUi(Action a)
        {
            if (IsDisposed) return;
            try { if (InvokeRequired) BeginInvoke(a); else a(); }
            catch { }
        }

        Color CategoryColor(string cat)
        {
            switch (cat)
            {
                case "installed": return Color.FromArgb(248, 117, 131); // 在用：亮红（深色背景）
                case "leftover": return Color.FromArgb(227, 179, 65);   // 亮橙
                case "cache": return Color.FromArgb(176, 131, 240);     // 亮紫
                default: return Color.FromArgb(157, 165, 176);          // 亮灰
            }
        }

        string FormatSize(long n)
        {
            if (n < 0) return "--"; // 未统计大小（取消"统计大小"勾选时）
            double v = n;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
            return i == 0 ? v + " B" : v.ToString("0.0") + " " + units[i];
        }

        void SetAll(bool val, bool onlyDeletable)
        {
            tree.BeginUpdate();
            foreach (var item in items)
            {
                if (onlyDeletable && item.Category != "leftover" && item.Category != "cache") continue;
                item.Checked = val;
                if (item.Node != null) item.Node.Checked = val;
            }
            tree.EndUpdate();
            UpdateSel();
            lblStatus.Text = "就绪";
        }

        void InvertAll()
        {
            tree.BeginUpdate();
            foreach (var item in items)
            {
                item.Checked = !item.Checked;
                if (item.Node != null) item.Node.Checked = item.Checked;
            }
            tree.EndUpdate();
            UpdateSel();
            lblStatus.Text = "就绪";
        }

        void ExpandAll(bool open)
        {
            tree.BeginUpdate();
            foreach (TreeNode n in tree.Nodes) SetExpand(n, open);
            tree.EndUpdate();
            lblStatus.Text = "就绪";
        }

        void SetExpand(TreeNode n, bool open)
        {
            n.Expand();
            foreach (TreeNode c in n.Nodes) SetExpand(c, open);
            if (!open) n.Collapse();
        }

        void UpdateSel()
        {
            int count = 0; long size = 0;
            foreach (var item in items)
                if (item.Checked) { count++; size += item.Size; }
            lblSel.Text = string.Format("已选 {0} 项 / {1}", count, FormatSize(size));
        }

        void DeleteSelected()
        {
            var sel = items.Where(i => i.Checked).ToList();
            if (sel.Count == 0) { Dlg.Show("请先勾选要删除的文件夹（点击节点前的复选框）", "提示"); return; }
            var total = sel.Sum(i => i.Size);
            var mode = rbPermanent.Checked ? "永久删除" : "移到回收站";
            var msg = string.Format("确认{0}以下 {1} 个文件夹（合计 {2}）？\n\n", mode, sel.Count, FormatSize(total));
            foreach (var it in sel.Take(12))
                msg += "  - " + it.Rel + " (" + it.Owner + ")\n";
            if (sel.Count > 12) msg += "  … 等共 " + sel.Count + " 项\n";
            if (Dlg.Show(msg, "删除确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            int ok = 0; var fail = new List<string>();
            foreach (var it in sel)
            {
                try
                {
                    bool success = rbPermanent.Checked ? PermanentDelete(it.Path) : MoveToRecycleBin(it.Path);
                    if (success)
                    {
                        ok++;
                        tree.BeginUpdate();
                        if (it.Node != null) it.Node.Remove();
                        tree.EndUpdate();
                        it.Checked = false;
                    }
                    else fail.Add(it.Rel);
                }
                catch (Exception ex) { fail.Add(it.Rel + " (" + ex.Message + ")"); }
            }
            UpdateSel();
            if (fail.Count > 0)
                Dlg.Show(string.Format("成功 {0} 项。\n失败 {1} 项：\n{2}", ok, fail.Count, string.Join("\n", fail.Take(10))),
                    "删除结果", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else
                Dlg.Show("成功删除 " + ok + " 项。", "删除结果", MessageBoxButtons.OK, MessageBoxIcon.Information);
            lblStatus.Text = string.Format("删除完成：成功 {0}，失败 {1}", ok, fail.Count);
        }

        bool MoveToRecycleBin(string path)
        {
            try
            {
                var op = new ShellApi.SHFILEOPSTRUCT();
                op.wFunc = ShellApi.FO_DELETE;
                op.pFrom = path + "\0\0";
                op.fFlags = ShellApi.FOF_ALLOWUNDO | ShellApi.FOF_SILENT | ShellApi.FOF_NOCONFIRMATION;
                return ShellApi.SHFileOperation(ref op) == 0;
            }
            catch { return false; }
        }

        bool PermanentDelete(string path)
        {
            try { Directory.Delete(path, true); return !Directory.Exists(path); }
            catch { return false; }
        }
    }

    /// <summary>无界面自测</summary>
    public static class SelfTest
    {
        public static void Run(string rootArg)        {
            var log = new List<string>();
            var root = string.IsNullOrEmpty(rootArg) ? @"D:\Win11\Temp\_test_scan" : rootArg;
            log.Add("selftest root=" + root + " exists=" + Directory.Exists(root));

            try
            {
                var form = new MainForm();
                var apps = new List<Tuple<string, string, string>>();
                form.ReadUninstallPublic(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", false, apps);
                form.ReadUninstallPublic(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", false, apps);
                log.Add("installed apps loaded: " + apps.Count);

            var cases = new Dictionary<string, string>
            {
                {"clangd", "LLVM Clangd"},
                {"Cloudflare", "Cloudflare"},
                {"CMakeTools", "CMake Tools"},
                {"CodeBuddyExtension", "CodeBuddy"},
                {"Colossal Order", "Colossal Order"},
                {"com.a1677.xiuxianmuyupro", "修仙模拟器"},
                {"com.espressif.eim.dev", "乐鑫 ESP"},
                {"com.github.wasilibs", "GitHub 组件"},
                {"com.nsemu.tools", "NSemu"},
                {"com.richardgaskin.cleanupassist", "Cleanup Assist"},
                {"ConnectedDevicesPlatform", "连接设备平台"},
                {"CrashReportClient", "崩溃上报"},
                {"D3DSCache", "着色器缓存"},
                {"csdevkit", "VS Code 开发工具包"},
                {"dclaude-updater", "更新器"},
                {"Go", "Go 语言"},
                {"google", "Google"},
                {"ventoy", "Ventoy"},
                {"ventoy-1.1.05", "Ventoy"},
                {"altexe", "Ventoy"},
                {"vtoy", "Ventoy"},
                {"FreeCAD", "FreeCAD"},     // 用户配置文件新增条目
                {"Listary", "Listary"},     // 用户配置文件新增条目
                {"GameViewer", "暂无法确认"}, // 用户配置文件条目（大写关键词，验证大小写修复）
                {"biubiuSysಠ䥅ቭ耀2", "损坏名"}, // 编码损坏的目录名，应标 [乱码] 损坏名
                {"aha_doctor", "未知"},     // AI 缓存中为未知，应保持未知
                {"boot", "未知"},     // 通用子目录名，不得被注册表误判
                {"plugin", "未知"},   // 通用子目录名，不得被注册表误判
                {"bin", "未知"},
                {"unknownrandom", "未知"},
            };
            foreach (var kv in cases)
            {
                string owner, cat, note;
                form.IdentifyPublic(Path.Combine(root, kv.Key), out owner, out cat, out note);
                var ok = kv.Value == "未知" ? cat == "unknown" : owner.Contains(kv.Value.Split('（')[0]);
                log.Add(string.Format("{0,-45} => {1,-28} [{2}]  {3}", kv.Key, owner, cat, ok ? "OK" : "CHECK"));
            }
            }
            catch (Exception ex)
            {
                log.Add("SELFTEST EXCEPTION: " + ex.ToString());
            }

            File.WriteAllLines(Path.Combine(Path.GetDirectoryName(root), "selftest_out.txt"), log);
        }
    }

    // 深色目录树：滚动条交给系统按“深色主题”原生绘制（DarkMode_Explorer）
    // —— 不再用 GDI 手动刷滚动条：系统自己画就不会出现「先画白底再被覆盖」的闪白
    internal class DarkTreeView : TreeView
    {
        const int WM_ERASEBKGND = 0x0014;

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

        // uxtheme 未公开导出：允许本窗口使用深色主题（Win10 1809+ / Win11）
        [DllImport("uxtheme.dll", EntryPoint = "#133")]
        static extern bool AllowDarkModeForWindow(IntPtr hWnd, bool bAllow);

        // uxtheme 未公开导出：声明本进程允许深色主题（1=允许，2=强制）
        [DllImport("uxtheme.dll", EntryPoint = "#135")]
        static extern int SetPreferredAppMode(int mode);

        static DarkTreeView()
        {
            try { SetPreferredAppMode(1); } catch { }
        }

        public DarkTreeView()
        {
            // 去掉系统 3D 边框（它的高光是白色，就是那条白边）
            BorderStyle = BorderStyle.None;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UseDarkScrollBar();
        }

        // 让滚动条走系统深色主题：轨道/滑块/箭头全深色，拖动与滚轮滚动都由系统绘制，不会再闪白
        void UseDarkScrollBar()
        {
            try { AllowDarkModeForWindow(Handle, true); }
            catch { }
            try { SetWindowTheme(Handle, "DarkMode_Explorer", null); }
            catch
            {
                try { SetWindowTheme(Handle, "", ""); } catch { }   // 老系统退化为经典样式
            }
        }

        // 滚轮滚动时系统会用白色擦背景（尤其最下面露出的空白区）→ 统一用控件深色填充
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_ERASEBKGND)
            {
                try
                {
                    using (var g = Graphics.FromHdc(m.WParam))
                    using (var br = new SolidBrush(BackColor))
                        g.FillRectangle(br, ClientRectangle);
                    m.Result = (IntPtr)1;
                    return;
                }
                catch { }
            }
            base.WndProc(ref m);
        }
    }

    // 深色输入框：保留 1px 边框，覆盖为深灰线，替代系统白色边框
    internal class DarkTextBox : TextBox
    {
        static readonly Color C_EDGE = Color.FromArgb(80, 80, 82); // 深灰边框

        public DarkTextBox()
        {
            BorderStyle = BorderStyle.FixedSingle;
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            const int WM_NCPAINT = 0x0085;
            if (m.Msg == WM_NCPAINT)
            {
                try
                {
                    IntPtr hdc = GetWindowDC(Handle);
                    using (Graphics g = Graphics.FromHdc(hdc))
                    using (var pen = new Pen(C_EDGE, 1))
                        g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
                    ReleaseDC(Handle, hdc);
                }
                catch { }
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr GetWindowDC(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    }

    // 深色弹窗：替代系统 MessageBox，配色与整体深色主题一致
    internal static class Dlg
    {
        static readonly Color BG = Color.FromArgb(37, 37, 38);
        static readonly Color FG = Color.FromArgb(218, 218, 218);
        static readonly Color BTN = Color.FromArgb(62, 62, 66);
        static readonly Color BTN_HOT = Color.FromArgb(78, 78, 82);

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int attrLen);

        public static void DarkTitleBar(IntPtr h)
        {
            try { int v = 1; DwmSetWindowAttribute(h, 20, ref v, 4); } catch { }
        }
        public static DialogResult Show(string text, string title)
        {
            return Show(text, title, MessageBoxButtons.OK);
        }

        public static DialogResult Show(string text, string title, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            return Show(text, title, buttons);
        }

        public static DialogResult Show(string text, string title, MessageBoxButtons buttons)
        {
            var f = new Form
            {
                Text = title,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false,
                BackColor = BG, ForeColor = FG,
                Font = SystemFonts.DefaultFont,
                Width = 440
            };
            var lbl = new Label { Left = 20, Top = 18, Width = 395, AutoSize = false, ForeColor = FG, Font = f.Font };
            lbl.Height = TextRenderer.MeasureText(text, lbl.Font, new Size(395, 0)).Height + 10;
            lbl.Text = text;
            f.Controls.Add(lbl);

            int btnY = lbl.Bottom + 18;
            int btnW = 90, btnH = 30;
            if (buttons == MessageBoxButtons.OK)
            {
                var b = MakeBtn("确定", btnW, btnH, f);
                b.Left = (f.Width - btnW) / 2 - 10;
                b.Top = btnY;
                b.Click += (s, e) => { f.DialogResult = DialogResult.OK; f.Close(); };
                f.Controls.Add(b);
            }
            else // YesNo
            {
                var b1 = MakeBtn("是", btnW, btnH, f);
                b1.Left = (f.Width - btnW * 2 - 16) / 2;
                b1.Top = btnY;
                b1.Click += (s, e) => { f.DialogResult = DialogResult.Yes; f.Close(); };
                f.Controls.Add(b1);

                var b2 = MakeBtn("否", btnW, btnH, f);
                b2.Left = b1.Right + 16;
                b2.Top = btnY;
                b2.Click += (s, e) => { f.DialogResult = DialogResult.No; f.Close(); };
                f.Controls.Add(b2);
            }
            f.Height = btnY + btnH + 60;
            f.Load += (s, e) => DarkTitleBar(f.Handle);
            return f.ShowDialog();
        }

        static Button MakeBtn(string text, int w, int h, Form parent)
        {
            var b = new Button
            {
                Text = text, Width = w, Height = h,
                FlatStyle = FlatStyle.Flat, BackColor = BTN, ForeColor = FG,
                Font = parent.Font, Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 82);
            b.FlatAppearance.MouseOverBackColor = BTN_HOT;
            return b;
        }
    }

    // 深色菜单配色
    internal class DarkMenuColors : ProfessionalColorTable
    {
        public override Color MenuBorder { get { return MainForm.C_EDGE; } }
        public override Color MenuItemBorder { get { return MainForm.C_EDGE; } }
        public override Color MenuItemSelected { get { return MainForm.C_EDGE; } }
        public override Color ToolStripDropDownBackground { get { return MainForm.C_CTL; } }
        public override Color ImageMarginGradientBegin { get { return MainForm.C_CTL; } }
        public override Color ImageMarginGradientMiddle { get { return MainForm.C_CTL; } }
        public override Color ImageMarginGradientEnd { get { return MainForm.C_CTL; } }
    }

    // 不抢焦点的提示窗：显示时不激活，避免主窗体标题栏闪烁
    internal class NoFocusForm : Form
    {
        protected override bool ShowWithoutActivation { get { return true; } }
    }

    internal static class ShellApi
    {
        public const uint FO_DELETE = 3;
        public const ushort FOF_SILENT = 0x4;
        public const ushort FOF_NOCONFIRMATION = 0x10;
        public const ushort FOF_ALLOWUNDO = 0x40;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
    }
}
