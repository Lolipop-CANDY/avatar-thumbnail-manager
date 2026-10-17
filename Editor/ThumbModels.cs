#if UNITY_EDITOR
// ============================================================================
//  データモデル / 設定 / 保存(AvatarDatabase.json) / 履歴
// ============================================================================
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LollipopCandy.AvatarHub
{
    #region Enums

    /// <summary>撮影方式。</summary>
    internal enum CaptureMode
    {
        /// <summary>シーン上の「今の見た目」をそのまま撮る(既定)。</summary>
        SceneExact = 0,
        /// <summary>NDMFでビルド処理した結果を撮る(MAの既定状態が適用される)。</summary>
        NdmfBuild = 1
    }

    internal enum BgMode
    {
        Auto = 0,
        Solid = 1,
        Gradient = 2,
        Transparent = 3,
        Image = 4
    }

    internal enum BackgroundStyle
    {
        Analogous = 0,
        Complementary = 1
    }

    internal enum Framing
    {
        Full = 0,
        Bust = 1,
        Face = 2,
        Custom = 3
    }

    internal enum OutFormat
    {
        Png = 0,
        Jpg = 1
    }

    #endregion

    #region Serializable parts

    [System.Serializable]
    internal sealed class BackgroundSpec
    {
        public BgMode mode = BgMode.Auto;
        public BackgroundStyle style = BackgroundStyle.Analogous;
        public Color solid = new Color(0.2f, 0.2f, 0.2f, 1f);
        public Color top = new Color(0.98f, 0.82f, 0.90f, 1f);
        public Color bottom = new Color(0.78f, 0.85f, 0.98f, 1f);
        public string imagePath = "";

        public BackgroundSpec Clone()
        {
            return (BackgroundSpec)MemberwiseClone();
        }

        public int Hash()
        {
            unchecked
            {
                int h = (int)mode * 31 + (int)style;
                h = h * 31 + solid.GetHashCode();
                h = h * 31 + top.GetHashCode();
                h = h * 31 + bottom.GetHashCode();
                h = h * 31 + (imagePath == null ? 0 : imagePath.GetHashCode());
                return h;
            }
        }
    }

    [System.Serializable]
    internal sealed class BlendOverride
    {
        public string shape = "";
        public float weight = 100f;
    }

    [System.Serializable]
    internal sealed class ExprPreset
    {
        public string name = "";
        public List<BlendOverride> blends = new List<BlendOverride>();
    }

    /// <summary>アバター個別の撮影設定(カメラ・ポーズ・表情・背景)。DBに保存される。</summary>
    [System.Serializable]
    internal sealed class AvatarLook
    {
        public bool hasCamera;
        public Vector3 camPos = new Vector3(0f, 0.1f, 1f);
        public Vector3 camRot = new Vector3(0f, 180f, 0f);
        public bool usePanYaw;
        public int framing = (int)Framing.Custom;

        public string clipGuid = "";
        public float clipTime;
        public List<BlendOverride> blends = new List<BlendOverride>();

        public bool bgOverride;
        public BackgroundSpec bg = new BackgroundSpec();

        public bool HasPoseOrExpression
        {
            get { return !string.IsNullOrEmpty(clipGuid) || (blends != null && blends.Count > 0); }
        }

        public int PoseHash()
        {
            unchecked
            {
                int h = clipGuid == null ? 0 : clipGuid.GetHashCode();
                h = h * 31 + clipTime.GetHashCode();
                if (blends != null)
                {
                    for (int i = 0; i < blends.Count; i++)
                    {
                        h = h * 31 + (blends[i].shape == null ? 0 : blends[i].shape.GetHashCode());
                        h = h * 31 + blends[i].weight.GetHashCode();
                    }
                }
                return h;
            }
        }

        public void CopyPoseFrom(AvatarLook other)
        {
            clipGuid = other.clipGuid;
            clipTime = other.clipTime;
            blends = other.blends.Select(b => new BlendOverride { shape = b.shape, weight = b.weight }).ToList();
        }
    }

    #endregion

    #region Settings

    /// <summary>ツール設定。EditorWindow に直列化(Undo対象) + EditorPrefs に永続化。</summary>
    [System.Serializable]
    internal sealed class ThumbSettings
    {
        private const string PrefKey = "LollipopCandy.AvatarThumbnailManager.Settings.v3";
        private const string PrefKeyV2 = "LollipopCandy.AvatarThumbnailManager.Settings.v2";

        public static readonly Vector3 DefaultPosition = new Vector3(0f, 0.1f, 1f);
        public static readonly Vector3 DefaultRotation = new Vector3(0f, 180f, 0f);

        public bool initialized;

        // カメラ
        public Vector3 camPos = DefaultPosition;
        public Vector3 camRot = DefaultRotation;
        public bool usePanYaw;
        public bool autoHeight = true;
        public int framing = (int)Framing.Custom;
        public bool perAvatarCamera = true;

        // 背景
        public BackgroundSpec bg = new BackgroundSpec();

        // 撮影オプション
        public bool skipExisting = true;
        public bool showRetakeWarning = true;
        public bool overwriteList;
        public int captureMode = (int)CaptureMode.SceneExact;
        public bool hideBoneProxy = true;
        public bool hideEditorOnly = true;
        public string hideNames = "";
        public bool liveSync = true;
        public bool reuseSharedThumb;

        // 表示
        public bool showCropGuide = true;
        public int previewDim = 384;
        public bool compareAfterCapture = true;

        // 出力
        public int outW = 1024;
        public int outH = 1024;
        public int outFormat = (int)OutFormat.Png;
        public int jpgQuality = 92;

        // 表情プリセット
        public List<ExprPreset> exprPresets = new List<ExprPreset>();

        // v2 からの移行用(読み込み専用)
        [System.Serializable]
        private sealed class LegacyV2
        {
            public bool autoBackground = true;
            public int backgroundStyle;
            public Color backgroundColor = new Color(0.2f, 0.2f, 0.2f, 1f);
        }

        public CaptureMode Mode { get { return (CaptureMode)captureMode; } }
        public Framing FramingValue { get { return (Framing)framing; } }
        public OutFormat Format { get { return (OutFormat)outFormat; } }

        public static ThumbSettings Load()
        {
            var s = new ThumbSettings();
            string json = EditorPrefs.GetString(PrefKey, "");
            try
            {
                if (!string.IsNullOrEmpty(json))
                {
                    JsonUtility.FromJsonOverwrite(json, s);
                }
                else
                {
                    string v2 = EditorPrefs.GetString(PrefKeyV2, "");
                    if (!string.IsNullOrEmpty(v2))
                    {
                        JsonUtility.FromJsonOverwrite(v2, s); // 同名フィールド(カメラ・オプション)を引き継ぐ
                        var legacy = new LegacyV2();
                        JsonUtility.FromJsonOverwrite(v2, legacy);
                        if (!legacy.autoBackground)
                        {
                            s.bg.mode = BgMode.Solid;
                            s.bg.solid = legacy.backgroundColor;
                        }
                        s.bg.style = (BackgroundStyle)Mathf.Clamp(legacy.backgroundStyle, 0, 1);
                    }
                }
            }
            catch (System.Exception)
            {
                // 壊れた設定は無視して既定値
            }
            s.Sanitize();
            s.initialized = true;
            return s;
        }

        public void Sanitize()
        {
            if (bg == null) bg = new BackgroundSpec();
            if (exprPresets == null) exprPresets = new List<ExprPreset>();
            outW = Mathf.Clamp(outW, 64, 4096);
            outH = Mathf.Clamp(outH, 64, 4096);
            jpgQuality = Mathf.Clamp(jpgQuality, 40, 100);
            previewDim = Mathf.Clamp(previewDim, 192, 768);
            captureMode = Mathf.Clamp(captureMode, 0, 1);
            framing = Mathf.Clamp(framing, 0, 3);
        }

        public void Save()
        {
            EditorPrefs.SetString(PrefKey, JsonUtility.ToJson(this));
        }

        public void ResetCamera()
        {
            camPos = DefaultPosition;
            camRot = DefaultRotation;
            usePanYaw = false;
            framing = (int)Framing.Custom;
        }

        public float Aspect { get { return outW / (float)Mathf.Max(1, outH); } }

        public string[] HideTokens()
        {
            if (string.IsNullOrWhiteSpace(hideNames)) return new string[0];
            return hideNames
                .Split(new[] { ',', ';', '、', '\n', '\r' }, System.StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .ToArray();
        }

        public static string Extension(OutFormat f)
        {
            return f == OutFormat.Jpg ? ".jpg" : ".png";
        }
    }

    #endregion

    #region Entry / DB

    internal sealed class CheckIssue
    {
        public int level; // 0=情報 1=警告 2=エラー
        public string text;
        public CheckIssue(int level, string text) { this.level = level; this.text = text; }
    }

    /// <summary>リスト1行ぶんの実行時データ。</summary>
    internal sealed class AvatarEntry
    {
        public string id;            // GlobalObjectId(シーン内で名前変更に強い)
        public string blueprintId;   // VRChat Blueprint ID(アップロード済みなら)
        public string name;
        public string thumbnailPath; // 生成時に固定。名前変更で変わらない
        public string memo;
        public string tag = "";
        public bool manual;
        public bool selected = true;
        public AvatarLook look = new AvatarLook();

        // 実行時のみ
        public bool hasThumb;        // File.Exists のキャッシュ
        public System.DateTime thumbTime;
        public Texture2D thumbTex;   // リスト表示用キャッシュ
        public GameObject sceneObject;
        public bool duplicateName;

        public bool IsMissing { get { return sceneObject == null; } }
        public bool IsHidden { get { return sceneObject != null && !sceneObject.activeInHierarchy; } }
    }

    // AvatarDatabase.json (旧版と互換: 既存フィールドはそのまま、追加フィールドは無くても読める)
    [System.Serializable]
    internal sealed class AvatarRecord
    {
        public string id;
        public string blueprintId;
        public string name;
        public string thumbnailPath;
        public string memo;
        public bool manual;
        public string tag = "";
        public AvatarLook look = new AvatarLook();
    }

    [System.Serializable]
    internal sealed class ProjectRecord
    {
        public string projectName;
        public string scenePath = "";
        public List<AvatarRecord> avatars = new List<AvatarRecord>();
    }

    [System.Serializable]
    internal sealed class DatabaseRecord
    {
        public List<ProjectRecord> projects = new List<ProjectRecord>();
    }

    internal static class ThumbStore
    {
        public const string LogFolder = "Assets/AvatarThumbnailManager/log";
        public const string HistoryFolder = "Assets/AvatarThumbnailManager/log/_history";
        private const string DbFileName = "AvatarDatabase.json";

        private static string DbPath { get { return LogFolder + "/" + DbFileName; } }

        public static string SceneKey(Scene scene)
        {
            return string.IsNullOrEmpty(scene.name) ? "Untitled" : scene.name;
        }

        public static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "_";
            char[] bad = Path.GetInvalidFileNameChars();
            return new string(value.Select(c => System.Array.IndexOf(bad, c) >= 0 ? '_' : c).ToArray());
        }

        public static string MakeThumbPath(string sceneKey, string avatarName, ICollection<string> used)
        {
            string dir = LogFolder + "/" + Sanitize(sceneKey);
            string baseName = Sanitize(avatarName);
            string path = dir + "/" + baseName + ".png";
            int n = 2;
            while (used.Contains(path))
            {
                path = dir + "/" + baseName + "_" + n + ".png";
                n++;
            }
            return path;
        }

        /// <summary>拡張子だけ差し替える(出力形式の変更用)。</summary>
        public static string WithExtension(string path, string ext)
        {
            return Path.ChangeExtension(path, ext).Replace('\\', '/');
        }

        public static DatabaseRecord ReadAll()
        {
            try
            {
                if (File.Exists(DbPath))
                {
                    string json = File.ReadAllText(DbPath);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        DatabaseRecord db = JsonUtility.FromJson<DatabaseRecord>(json);
                        if (db != null && db.projects != null) return db;
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[AvatarThumbnail] データベース読み込みに失敗: " + e.Message);
            }
            return new DatabaseRecord();
        }

        public static List<AvatarRecord> Load(string sceneKey)
        {
            ProjectRecord project = ReadAll().projects.FirstOrDefault(p => p.projectName == sceneKey);
            return (project != null && project.avatars != null) ? project.avatars : new List<AvatarRecord>();
        }

        public static void Save(string sceneKey, string scenePath, IEnumerable<AvatarEntry> entries)
        {
            try
            {
                DatabaseRecord db = ReadAll();
                ProjectRecord project = db.projects.FirstOrDefault(p => p.projectName == sceneKey);
                if (project == null)
                {
                    project = new ProjectRecord { projectName = sceneKey };
                    db.projects.Add(project);
                }
                if (!string.IsNullOrEmpty(scenePath)) project.scenePath = scenePath;

                project.avatars = entries.Select(e => new AvatarRecord
                {
                    id = e.id ?? "",
                    blueprintId = e.blueprintId ?? "",
                    name = e.name ?? "",
                    thumbnailPath = e.thumbnailPath ?? "",
                    memo = e.memo ?? "",
                    manual = e.manual,
                    tag = e.tag ?? "",
                    look = e.look ?? new AvatarLook()
                }).ToList();

                Directory.CreateDirectory(LogFolder);
                File.WriteAllText(DbPath, JsonUtility.ToJson(db, true));
            }
            catch (System.Exception e)
            {
                Debug.LogError("[AvatarThumbnail] データベース保存に失敗: " + e.Message);
            }
        }
    }

    #endregion

    #region History

    /// <summary>再撮影時に上書き前の画像を退避し、比較・復元できるようにする。</summary>
    internal static class ThumbHistory
    {
        private const int Keep = 5;

        private static string DirFor(string thumbPath)
        {
            string scene = Path.GetFileName(Path.GetDirectoryName(thumbPath) ?? "");
            return ThumbStore.HistoryFolder + "/" + scene;
        }

        private static string Stem(string thumbPath)
        {
            return Path.GetFileNameWithoutExtension(thumbPath);
        }

        /// <summary>現在の画像を履歴フォルダへコピーする。退避したパスを返す(無ければ null)。</summary>
        public static string Archive(string thumbPath)
        {
            try
            {
                if (!File.Exists(thumbPath)) return null;
                string dir = DirFor(thumbPath);
                Directory.CreateDirectory(dir);
                string stamp = System.DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string dst = dir + "/" + Stem(thumbPath) + "_" + stamp + Path.GetExtension(thumbPath);
                int n = 2;
                while (File.Exists(dst))
                {
                    dst = dir + "/" + Stem(thumbPath) + "_" + stamp + "-" + n + Path.GetExtension(thumbPath);
                    n++;
                }
                File.Copy(thumbPath, dst, false);
                Prune(thumbPath);
                return dst;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[AvatarThumbnail] 履歴の保存に失敗: " + e.Message);
                return null;
            }
        }

        /// <summary>新しい順の履歴ファイル一覧。</summary>
        public static List<string> List(string thumbPath)
        {
            var result = new List<string>();
            try
            {
                string dir = DirFor(thumbPath);
                if (!Directory.Exists(dir)) return result;
                string prefix = Stem(thumbPath) + "_";
                foreach (string f in Directory.GetFiles(dir))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".png" && ext != ".jpg") continue;
                    string stem = Path.GetFileNameWithoutExtension(f);
                    if (!stem.StartsWith(prefix, System.StringComparison.Ordinal)) continue;
                    // 「name_2」のような別アバターと混ざらないよう、末尾が日時形式のものだけ採用
                    string tail = stem.Substring(prefix.Length);
                    if (tail.Length < 15 || tail[8] != '-') continue;
                    result.Add(f.Replace('\\', '/'));
                }
                result.Sort((a, b) => string.CompareOrdinal(b, a));
            }
            catch (System.Exception) { }
            return result;
        }

        private static void Prune(string thumbPath)
        {
            List<string> all = List(thumbPath);
            for (int i = Keep; i < all.Count; i++)
            {
                try
                {
                    if (AssetDatabase.IsValidFolder(Path.GetDirectoryName(all[i]).Replace('\\', '/')))
                    {
                        AssetDatabase.DeleteAsset(all[i]);
                    }
                    else if (File.Exists(all[i]))
                    {
                        File.Delete(all[i]);
                    }
                }
                catch (System.Exception) { }
            }
        }
    }

    #endregion
}
#endif
