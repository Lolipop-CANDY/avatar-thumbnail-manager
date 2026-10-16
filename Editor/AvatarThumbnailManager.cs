#if UNITY_EDITOR
// ============================================================================
//  VRChatアバターサムネイル撮影管理ツール  v2.0
//
//  設計メモ
//   - 撮影は「隔離プレビューシーン」に対象アバターの複製を作って行う。
//     元のシーンのオブジェクトは一切 SetActive / Animator 操作をしないため、
//     重なったアバターや切り替えアイテムの写り込みが原理的に起きない。
//   - NDMF が入っていれば複製を NDMF で処理してから撮影する
//     (Modular Avatar の BoneProxy / 切り替え衣装・髪の初期状態が反映される)。
//   - アバターの同一性は GlobalObjectId / Blueprint ID で判定する。
//     名前を変えても別アバター扱いにならない。サムネイル保存先はDBに固定する。
//   - NDMF / VRChat SDK は直接参照せずリフレクションで呼ぶ(未導入でもコンパイルが通る)。
// ============================================================================
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LollipopCandy.AvatarHub
{
    #region Settings

    internal enum BackgroundStyle
    {
        Analogous = 0,
        Complementary = 1
    }

    /// <summary>ツール設定。EditorWindow に直列化(Undo対象) + EditorPrefs に永続化。</summary>
    [System.Serializable]
    internal sealed class ThumbSettings
    {
        private const string PrefKey = "LollipopCandy.AvatarThumbnailManager.Settings.v2";

        public static readonly Vector3 DefaultPosition = new Vector3(0f, 0.1f, 1f);
        public static readonly Vector3 DefaultRotation = new Vector3(0f, 180f, 0f);

        public bool initialized;

        // カメラ
        public Vector3 camPos = DefaultPosition;
        public Vector3 camRot = DefaultRotation;
        public bool usePanYaw;
        public bool autoHeight = true;

        // 背景
        public bool autoBackground = true;
        public BackgroundStyle backgroundStyle = BackgroundStyle.Analogous;
        public Color backgroundColor = new Color(0.2f, 0.2f, 0.2f, 1f);

        // オプション
        public bool skipExisting = true;
        public bool showRetakeWarning = true;
        public bool overwriteList;
        public bool useNdmf = true;
        public bool hideBoneProxy = true;
        public string hideNames = "";
        public bool showCropGuide = true;

        public static ThumbSettings Load()
        {
            var s = new ThumbSettings();
            string json = EditorPrefs.GetString(PrefKey, "");
            if (!string.IsNullOrEmpty(json))
            {
                try { JsonUtility.FromJsonOverwrite(json, s); }
                catch (System.Exception) { /* 壊れた設定は無視して既定値 */ }
            }
            s.initialized = true;
            return s;
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
        }

        public string[] HideTokens()
        {
            if (string.IsNullOrWhiteSpace(hideNames)) return new string[0];
            return hideNames
                .Split(new[] { ',', ';', '、', '\n', '\r' }, System.StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .ToArray();
        }
    }

    #endregion

    #region Data

    /// <summary>リスト1行ぶんの実行時データ。</summary>
    internal sealed class AvatarEntry
    {
        public string id;            // GlobalObjectId(シーン内で名前変更に強い)
        public string blueprintId;   // VRChat Blueprint ID(アップロード済みなら)
        public string name;
        public string thumbnailPath; // 生成時に固定。名前変更で変わらない
        public string memo;
        public bool manual;
        public bool selected = true;
        public bool hasThumb;        // File.Exists のキャッシュ
        public GameObject sceneObject;

        public bool IsMissing { get { return sceneObject == null; } }
        public bool IsHidden { get { return sceneObject != null && !sceneObject.activeInHierarchy; } }
    }

    // AvatarDatabase.json (旧版と互換: 既存フィールドはそのまま、id/blueprintId/manual を追加)
    [System.Serializable]
    internal sealed class AvatarRecord
    {
        public string id;
        public string blueprintId;
        public string name;
        public string thumbnailPath;
        public string memo;
        public bool manual;
    }

    [System.Serializable]
    internal sealed class ProjectRecord
    {
        public string projectName;
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

        private static DatabaseRecord ReadDatabase()
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
            ProjectRecord project = ReadDatabase().projects.FirstOrDefault(p => p.projectName == sceneKey);
            return (project != null && project.avatars != null) ? project.avatars : new List<AvatarRecord>();
        }

        public static void Save(string sceneKey, IEnumerable<AvatarEntry> entries)
        {
            try
            {
                DatabaseRecord db = ReadDatabase();
                ProjectRecord project = db.projects.FirstOrDefault(p => p.projectName == sceneKey);
                if (project == null)
                {
                    project = new ProjectRecord { projectName = sceneKey };
                    db.projects.Add(project);
                }

                project.avatars = entries.Select(e => new AvatarRecord
                {
                    id = e.id ?? "",
                    blueprintId = e.blueprintId ?? "",
                    name = e.name ?? "",
                    thumbnailPath = e.thumbnailPath ?? "",
                    memo = e.memo ?? "",
                    manual = e.manual
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

    #region Avatar identity

    internal static class AvatarIdentity
    {
        /// <summary>シーン内オブジェクトの永続ID。未保存シーンなど不安定な場合は null。</summary>
        public static string GetStableId(GameObject go)
        {
            try
            {
                GlobalObjectId gid = GlobalObjectId.GetGlobalObjectIdSlow(go);
                if (gid.identifierType != 2 || gid.targetObjectId == 0UL || gid.assetGUID.ToString().Trim('0').Length == 0) return null;
                return gid.ToString();
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        public static GameObject ResolveStableId(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            GlobalObjectId gid;
            if (!GlobalObjectId.TryParse(id, out gid)) return null;
            return GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid) as GameObject;
        }

        /// <summary>PipelineManager.blueprintId(SDKを直接参照せずリフレクション)。</summary>
        public static string GetBlueprintId(GameObject go)
        {
            foreach (Component c in go.GetComponents<Component>())
            {
                if (c == null || c.GetType().Name != "PipelineManager") continue;
                FieldInfo f = c.GetType().GetField("blueprintId",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                string v = f != null ? f.GetValue(c) as string : null;
                return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
            }
            return null;
        }

        public static bool HasComponentNamed(GameObject go, string typeName)
        {
            foreach (Component c in go.GetComponents<Component>())
            {
                if (c != null && c.GetType().Name == typeName) return true;
            }
            return false;
        }

        public static bool IsAvatar(GameObject go)
        {
            if (HasComponentNamed(go, "VRCAvatarDescriptor")) return true;
            Animator anim = go.GetComponent<Animator>();
            if (anim == null || !anim.isHuman) return false;
            return go.GetComponentsInChildren<Transform>(true)
                .Any(t => string.Equals(t.name, "Armature", System.StringComparison.OrdinalIgnoreCase));
        }
    }

    #endregion

    #region Rendering

    internal struct ColorStats
    {
        public bool valid;
        public bool hasHue;
        public float hue;
        public float luma;
    }

    internal static class BackgroundPalette
    {
        public static Color From(ColorStats st, BackgroundStyle style)
        {
            float h = st.hasHue ? st.hue : 0.58f;
            float sat = st.hasHue ? 0.30f : 0.16f;
            if (style == BackgroundStyle.Complementary)
            {
                h = Mathf.Repeat(h + 0.5f, 1f);
                sat = st.hasHue ? 0.36f : 0.16f;
            }
            float val = st.luma > 0.62f ? 0.58f : 0.93f; // 明るいアバターには暗め、暗いアバターには明るめ
            Color c = Color.HSVToRGB(h, sat, val);
            c.a = 1f;
            return c;
        }
    }

    /// <summary>NDMF(AvatarProcessor)をリフレクションで呼ぶ。</summary>
    internal static class NdmfBridge
    {
        private static bool _resolved;
        private static MethodInfo _process;
        private static MethodInfo _clean;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            System.Type t = null;
            foreach (Assembly asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                t = asm.GetType("nadena.dev.ndmf.AvatarProcessor", false);
                if (t != null) break;
            }
            if (t == null) return;
            _process = t.GetMethod("ProcessAvatar", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(GameObject) }, null);
            _clean = t.GetMethod("CleanTemporaryAssets", BindingFlags.Public | BindingFlags.Static,
                null, System.Type.EmptyTypes, null);
        }

        public static bool IsAvailable { get { Resolve(); return _process != null; } }

        public static void Process(GameObject root)
        {
            Resolve();
            _process.Invoke(null, new object[] { root });
        }

        public static void CleanTemporaryAssets()
        {
            Resolve();
            try { if (_clean != null) _clean.Invoke(null, null); }
            catch (System.Exception) { /* 掃除失敗は無視 */ }
        }
    }

    /// <summary>
    /// 1アバターぶんの撮影環境。隔離プレビューシーンに複製を置き、必要なら NDMF 処理する。
    /// プレビュー表示と撮影で同じセッションを使うため、見た目が一致する。
    /// </summary>
    internal sealed class RenderSession : System.IDisposable
    {
        private static int _alive;
        private static bool _needsClean;

        private PreviewRenderUtility _pru;
        private GameObject _root;
        private bool _ndmfApplied;

        public Bounds Bounds { get; private set; }
        public ColorStats Stats { get; private set; }
        public string Description { get; private set; }

        private RenderSession() { }

        public static RenderSession Create(GameObject source, ThumbSettings settings)
        {
            var session = new RenderSession();
            _alive++;
            try
            {
                session.Build(source, settings);
                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        private void Build(GameObject source, ThumbSettings s)
        {
            _pru = new PreviewRenderUtility();
            ConfigureStage(_pru);

            if (s.useNdmf && NdmfBridge.IsAvailable)
            {
                try
                {
                    CreateClone(source);
                    _needsClean = true;
                    NdmfBridge.Process(_root);
                    _ndmfApplied = true;
                    Description = "NDMF処理済み(Modular Avatarの設定を反映)";
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning("[AvatarThumbnail] NDMF処理に失敗したため簡易モードで撮影します: " + e.Message);
                    DestroyClone();
                    Description = "NDMF処理に失敗 → 簡易モード";
                }
            }
            else
            {
                Description = s.useNdmf ? "NDMF未導入 → 簡易モード" : "簡易モード(NDMF未使用)";
            }

            if (_root == null) CreateClone(source);

            // 簡易モード: NDMFが使えない時の最低限の写り込み対策
            if (!_ndmfApplied && s.hideBoneProxy) HideBoneProxies(_root);
            HideByNames(_root, s.HideTokens());

            Bounds = CalcBounds(_root);
            Stats = AnalyzeColors();
        }

        private static void ConfigureStage(PreviewRenderUtility pru)
        {
            Camera cam = pru.camera;
            cam.fieldOfView = 60f;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 100f;
            cam.clearFlags = CameraClearFlags.SolidColor;

            pru.lights[0].intensity = 1.1f;
            pru.lights[0].transform.rotation = Quaternion.Euler(40f, 165f, 0f);
            pru.lights[1].intensity = 0.5f;
            pru.lights[1].transform.rotation = Quaternion.Euler(15f, 140f, 0f);
            pru.ambientColor = new Color(0.45f, 0.45f, 0.45f, 1f);
        }

        /// <summary>プレビューシーン内に直接複製する(元シーンを汚さない)。</summary>
        private void CreateClone(GameObject source)
        {
            GameObject anchor = EditorUtility.CreateGameObjectWithHideFlags(
                "AvatarThumbnailAnchor", HideFlags.HideAndDontSave);
            _pru.AddSingleGO(anchor);

            GameObject clone = Object.Instantiate(source, anchor.transform, true);
            clone.name = source.name;
            clone.transform.SetParent(null, true);
            Object.DestroyImmediate(anchor);

            clone.SetActive(true);
            _root = clone;
        }

        private void DestroyClone()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            _root = null;
        }

        private static void HideBoneProxies(GameObject root)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t != root.transform && AvatarIdentity.HasComponentNamed(t.gameObject, "ModularAvatarBoneProxy"))
                {
                    t.gameObject.SetActive(false);
                }
            }
        }

        private static void HideByNames(GameObject root, string[] tokens)
        {
            if (tokens.Length == 0) return;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == root.transform) continue;
                for (int i = 0; i < tokens.Length; i++)
                {
                    if (t.name.IndexOf(tokens[i], System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        t.gameObject.SetActive(false);
                        break;
                    }
                }
            }
        }

        private static Bounds CalcBounds(GameObject root)
        {
            bool has = false;
            Bounds b = new Bounds(root.transform.position + Vector3.up * 0.8f, Vector3.one);
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(false))
            {
                if (!r.enabled) continue;
                if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                if (!has) { b = r.bounds; has = true; }
                else { b.Encapsulate(r.bounds); }
            }
            return b;
        }

        // ---- 描画 ----

        public void PlaceCamera(ThumbSettings s)
        {
            Vector3 basePoint = s.autoHeight ? Bounds.center : _root.transform.position;
            Transform t = _pru.camera.transform;
            t.position = basePoint + s.camPos;
            t.rotation = Quaternion.Euler(s.camRot.x, s.camRot.y, 0f);
        }

        /// <summary>ウィンドウ内にライブ表示(Repaint中に呼ぶ)。</summary>
        public void DrawLive(Rect rect, ThumbSettings s, Color background)
        {
            PlaceCamera(s);
            Camera cam = _pru.camera;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = background;
            _pru.BeginPreview(rect, GUIStyle.none);
            cam.Render();
            _pru.EndAndDrawPreview(rect);
        }

        /// <summary>不透明のRGB24テクスチャとして撮影する。呼び出し側で破棄すること。</summary>
        public Texture2D RenderOpaque(int size, Color background)
        {
            Texture2D src = RenderRaw(size, background);
            if (src == null) return null;

            Color32[] px = src.GetPixels32();
            for (int i = 0; i < px.Length; i++) px[i].a = 255;
            var dst = new Texture2D(src.width, src.height, TextureFormat.RGB24, false);
            dst.SetPixels32(px);
            dst.Apply(false);
            Object.DestroyImmediate(src);
            return dst;
        }

        private Texture2D RenderRaw(int size, Color background)
        {
            Camera cam = _pru.camera;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = background;
            _pru.BeginStaticPreview(new Rect(0f, 0f, size, size));
            cam.Render();
            return _pru.EndStaticPreview();
        }

        private Color32[] RenderPixels(int size, Color background)
        {
            Texture2D tex = RenderRaw(size, background);
            if (tex == null) return null;
            Color32[] px = tex.GetPixels32();
            Object.DestroyImmediate(tex);
            return px;
        }

        /// <summary>
        /// 黒背景/白背景の2回描画の差分でアバター領域を抽出し、主色相と平均輝度を求める。
        /// (背景色に依存せず、シェーダーによらず正確にマスクできる)
        /// </summary>
        private ColorStats AnalyzeColors()
        {
            const int size = 96;
            Camera cam = _pru.camera;
            Bounds b = Bounds;
            float half = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float dist = Mathf.Max(b.extents.y, b.extents.x) / half * 1.15f + b.extents.z;
            cam.transform.position = b.center + new Vector3(0f, 0f, dist);
            cam.transform.rotation = Quaternion.Euler(0f, 180f, 0f);

            Color32[] onBlack = RenderPixels(size, Color.black);
            Color32[] onWhite = RenderPixels(size, Color.white);
            if (onBlack == null || onWhite == null || onBlack.Length != onWhite.Length)
            {
                return default(ColorStats);
            }

            var samples = new List<Vector2>(); // x = hue, y = weight
            float[] hist = new float[24];
            float sumWeight = 0f, sumLuma = 0f, count = 0f;

            for (int i = 0; i < onBlack.Length; i++)
            {
                Color32 a = onBlack[i];
                Color32 w = onWhite[i];
                float alpha = 1f - ((w.r - a.r) + (w.g - a.g) + (w.b - a.b)) / (3f * 255f);
                if (alpha < 0.5f) continue;

                float inv = 1f / alpha;
                float r = Mathf.Clamp01(a.r / 255f * inv);
                float g = Mathf.Clamp01(a.g / 255f * inv);
                float bl = Mathf.Clamp01(a.b / 255f * inv);

                float h, sat, v;
                Color.RGBToHSV(new Color(r, g, bl), out h, out sat, out v);
                float weight = sat * v;

                count += 1f;
                sumLuma += 0.2126f * r + 0.7152f * g + 0.0722f * bl;
                sumWeight += weight;
                hist[Mathf.Min(23, (int)(h * 24f))] += weight;
                samples.Add(new Vector2(h, weight));
            }

            ColorStats st = default(ColorStats);
            if (count < 20f) return st;

            st.valid = true;
            st.luma = sumLuma / count;

            if (sumWeight / count > 0.05f) // ほぼ無彩色のアバターは色相なし扱い
            {
                int best = 0;
                for (int k = 1; k < hist.Length; k++) if (hist[k] > hist[best]) best = k;

                float x = 0f, y = 0f;
                for (int i = 0; i < samples.Count; i++)
                {
                    int bin = Mathf.Min(23, (int)(samples[i].x * 24f));
                    int d = Mathf.Abs(bin - best);
                    d = Mathf.Min(d, 24 - d);
                    if (d > 1) continue;
                    float ang = samples[i].x * 2f * Mathf.PI;
                    x += Mathf.Cos(ang) * samples[i].y;
                    y += Mathf.Sin(ang) * samples[i].y;
                }
                st.hasHue = true;
                st.hue = Mathf.Repeat(Mathf.Atan2(y, x) / (2f * Mathf.PI), 1f);
            }
            return st;
        }

        public void Dispose()
        {
            DestroyClone();
            if (_pru != null)
            {
                _pru.Cleanup();
                _pru = null;
            }

            _alive--;
            // 他のセッションがNDMFの一時アセットを使っている間は掃除しない
            if (_alive <= 0)
            {
                _alive = 0;
                if (_needsClean)
                {
                    _needsClean = false;
                    NdmfBridge.CleanTemporaryAssets();
                }
            }
        }
    }

    #endregion

    #region VRChat SDK icon

    internal sealed class SdkIconResult
    {
        public bool ok;
        public string message;
    }

    /// <summary>
    /// 撮影画像をVRChat SDKのアバターアイコンに設定する。
    /// SDKのAPIシグネチャはバージョンで変わりうるため、リフレクションで引数を組み立てる。
    /// 失敗時は呼び出し側が手動設定へ誘導する。
    /// </summary>
    internal static class SdkIconBridge
    {
        private const string ApiTypeName = "VRC.SDKBase.Editor.Api.VRCApi";

        /// <summary>SDKのアイコン表示(4:3)に合わせて中央を切り出した一時PNGを作る。</summary>
        public static string CreateSdkImage(string sourcePath)
        {
            Texture2D src = null;
            Texture2D dst = null;
            try
            {
                src = new Texture2D(2, 2, TextureFormat.RGB24, false);
                if (!src.LoadImage(File.ReadAllBytes(sourcePath))) return null;

                int w = src.width;
                int h = Mathf.Min(src.height, Mathf.RoundToInt(w * 3f / 4f));
                int y = (src.height - h) / 2;

                dst = new Texture2D(w, h, TextureFormat.RGB24, false);
                dst.SetPixels(src.GetPixels(0, y, w, h));
                dst.Apply(false);

                string path = Path.GetFullPath("Temp/AvatarThumbnailManager_sdk_icon.png");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, dst.EncodeToPNG());
                return path;
            }
            catch (System.Exception e)
            {
                Debug.LogError("[AvatarThumbnail] SDK用画像の作成に失敗: " + e.Message);
                return null;
            }
            finally
            {
                if (src != null) Object.DestroyImmediate(src);
                if (dst != null) Object.DestroyImmediate(dst);
            }
        }

        /// <summary>手動設定の準備: SDKパネルを開き、画像の場所を開いてパスをコピーする。</summary>
        public static void PrepareManual(string imagePath)
        {
            EditorApplication.ExecuteMenuItem("VRChat SDK/Show Control Panel");
            GUIUtility.systemCopyBuffer = imagePath;
            EditorUtility.RevealInFinder(imagePath);
        }

        public static async Task<SdkIconResult> TryUploadAsync(string blueprintId, string imagePath)
        {
            try
            {
                System.Type api = FindType(ApiTypeName);
                if (api == null) return Fail("VRChat SDK(VRCApi)が見つかりません。");

                MethodInfo update = api.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "UpdateAvatarImage");
                if (update == null) return Fail("このSDKバージョンには UpdateAvatarImage がありません。");

                object avatar = null;
                ParameterInfo[] ps = update.GetParameters();
                var args = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    ParameterInfo p = ps[i];
                    string pn = p.Name.ToLowerInvariant();
                    if (p.ParameterType == typeof(string))
                    {
                        bool isPath = pn.Contains("path") || pn.Contains("file") || pn.Contains("image");
                        args[i] = isPath ? imagePath : blueprintId;
                    }
                    else if (p.ParameterType.Name == "VRCAvatar")
                    {
                        if (avatar == null) avatar = await FetchAvatarAsync(api, blueprintId);
                        if (avatar == null) return Fail("アバター情報を取得できませんでした(SDKへのログイン・所有アバターか確認してください)。");
                        args[i] = avatar;
                    }
                    else
                    {
                        args[i] = DefaultFor(p);
                    }
                }

                await InvokeAsync(update, args);
                return new SdkIconResult { ok = true, message = "アバターアイコンを更新しました。" };
            }
            catch (System.Exception e)
            {
                return Fail(Unwrap(e).Message);
            }
        }

        private static async Task<object> FetchAvatarAsync(System.Type api, string blueprintId)
        {
            MethodInfo get = api.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "GetAvatar" && m.GetParameters().Length > 0
                                     && m.GetParameters()[0].ParameterType == typeof(string));
            if (get == null) return null;

            ParameterInfo[] ps = get.GetParameters();
            var args = new object[ps.Length];
            args[0] = blueprintId;
            for (int i = 1; i < ps.Length; i++)
            {
                args[i] = ps[i].ParameterType == typeof(bool) ? (object)true : DefaultFor(ps[i]);
            }
            return await InvokeAsync(get, args);
        }

        private static async Task<object> InvokeAsync(MethodInfo method, object[] args)
        {
            object ret = method.Invoke(null, args);
            Task task = ret as Task;
            if (task == null) return ret;

            await task;
            PropertyInfo result = task.GetType().GetProperty("Result");
            return result != null ? result.GetValue(task, null) : null;
        }

        private static object DefaultFor(ParameterInfo p)
        {
            if (p.ParameterType == typeof(System.Threading.CancellationToken))
            {
                return default(System.Threading.CancellationToken);
            }
            if (p.HasDefaultValue)
            {
                object d = p.DefaultValue;
                if (d == null || d is System.DBNull || d == Missing.Value) return null;
                return d;
            }
            return p.ParameterType.IsValueType ? System.Activator.CreateInstance(p.ParameterType) : null;
        }

        private static System.Type FindType(string fullName)
        {
            foreach (Assembly asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type t = asm.GetType(fullName, false);
                if (t != null) return t;
            }
            return null;
        }

        private static System.Exception Unwrap(System.Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            return e;
        }

        private static SdkIconResult Fail(string message)
        {
            return new SdkIconResult { ok = false, message = message };
        }
    }

    #endregion

    #region Window

    public class AvatarThumbnailManager : EditorWindow
    {
        private const int ThumbnailSize = 1024;
        private const float PreviewPixels = 320f;

        [SerializeField] private ThumbSettings s = new ThumbSettings();

        private readonly List<AvatarEntry> _entries = new List<AvatarEntry>();
        private string _sceneKey = "";
        private Vector2 _scroll;
        private bool _showAdvanced;
        private int _undoGroup = -1;

        private RenderSession _session;
        private AvatarEntry _previewEntry;

        [MenuItem("ろりぽっぷCANDY/あばたーはぶリスト作成ツール")]
        public static void ShowWindow()
        {
            AvatarThumbnailManager window = GetWindow<AvatarThumbnailManager>("あばたーはぶリスト作成");
            window.minSize = new Vector2(480f, 600f);
            window.Show();
        }

        // ---- ライフサイクル ----

        private void OnEnable()
        {
            if (s == null || !s.initialized) s = ThumbSettings.Load();

            Undo.undoRedoPerformed += OnUndoRedo;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;

            ReloadForScene();
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
            EditorSceneManager.sceneOpened -= OnSceneOpened;
            EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChanged;

            if (s != null) s.Save();
            CloseSession();
        }

        private void OnFocus()
        {
            RefreshThumbFlags();
            Repaint();
        }

        private void OnHierarchyChange()
        {
            bool renamed = false;
            foreach (AvatarEntry e in _entries)
            {
                if (e.sceneObject != null && e.name != e.sceneObject.name)
                {
                    e.name = e.sceneObject.name; // 表示名だけ追従。ID・保存先は変わらない
                    renamed = true;
                }
            }
            if (renamed) SaveEntries();
            Repaint();
        }

        private void OnUndoRedo() { Repaint(); }
        private void OnSceneOpened(Scene scene, OpenSceneMode mode) { ReloadForScene(); }
        private void OnActiveSceneChanged(Scene previous, Scene next) { ReloadForScene(); }

        // ---- GUI ----

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("アバターサムネイル撮影ツール", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            DrawCameraSection();
            EditorGUILayout.Space();
            DrawBackgroundSection();
            EditorGUILayout.Space();
            DrawOptionsSection();
            EditorGUILayout.Space();
            DrawPreviewSection();
            DrawListSection();
            EditorGUILayout.Space();
            DrawActionSection();

            EditorGUILayout.EndScrollView();

            CollapseUndoOnMouseUp();
            if (GUI.changed) s.Save();
        }

        private void DrawCameraSection()
        {
            EditorGUILayout.LabelField("カメラ設定", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                bool axis = GUILayout.Toggle(!s.usePanYaw, "軸", EditorStyles.miniButtonLeft, GUILayout.Width(60f));
                bool rot = GUILayout.Toggle(s.usePanYaw, "回転", EditorStyles.miniButtonRight, GUILayout.Width(60f));
                if (axis && s.usePanYaw) { RecordUndo("カメラモード"); s.usePanYaw = false; }
                else if (rot && !s.usePanYaw) { RecordUndo("カメラモード"); s.usePanYaw = true; }
                GUILayout.FlexibleSpace();
            }

            if (!s.usePanYaw)
            {
                // 軸: カメラの向きは変えず、位置だけを上下左右前後に平行移動する
                UndoSlider("X (左右)", ref s.camPos.x, -5f, 5f);
                UndoSlider("Y (上下)", ref s.camPos.y, -5f, 5f);
                UndoSlider("Z (前後)", ref s.camPos.z, -10f, 10f);
            }
            else
            {
                // 回転: 現在位置を保ったまま、その場で向きだけを回す
                UndoSlider("パン (水平)", ref s.camRot.y, 0f, 360f);
                UndoSlider("ヨー (垂直)", ref s.camRot.x, -89f, 89f);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("正面"))
                {
                    RecordUndo("正面");
                    float dz = Mathf.Max(0.3f, Mathf.Abs(s.camPos.z));
                    s.camPos = new Vector3(s.camPos.x, s.camPos.y, dz);
                    s.camRot = new Vector3(0f, 180f, 0f);
                }
                if (GUILayout.Button("背面"))
                {
                    RecordUndo("背面");
                    float dz = Mathf.Max(0.3f, Mathf.Abs(s.camPos.z));
                    s.camPos = new Vector3(s.camPos.x, s.camPos.y, -dz);
                    s.camRot = new Vector3(0f, 0f, 0f);
                }
            }

            if (GUILayout.Button("カメラを初期設定に戻す"))
            {
                RecordUndo("カメラ初期化");
                s.ResetCamera();
            }
        }

        private void DrawBackgroundSection()
        {
            EditorGUILayout.LabelField("背景", EditorStyles.boldLabel);
            UndoToggle("アバターに合わせて自動設定", ref s.autoBackground);

            using (new EditorGUI.DisabledScope(!s.autoBackground))
            {
                EditorGUI.BeginChangeCheck();
                int style = EditorGUILayout.Popup("配色", (int)s.backgroundStyle,
                    new[] { "同系色(パステル)", "補色(パステル)" });
                if (EditorGUI.EndChangeCheck())
                {
                    RecordUndo("背景配色");
                    s.backgroundStyle = (BackgroundStyle)style;
                }
            }

            using (new EditorGUI.DisabledScope(s.autoBackground))
            {
                EditorGUI.BeginChangeCheck();
                Color c = EditorGUILayout.ColorField("背景色", s.backgroundColor);
                if (EditorGUI.EndChangeCheck())
                {
                    RecordUndo("背景色");
                    s.backgroundColor = c;
                }
            }
        }

        private void DrawOptionsSection()
        {
            EditorGUILayout.LabelField("オプション", EditorStyles.boldLabel);
            UndoToggle("自動高さ合わせ(アバター中心基準)", ref s.autoHeight);
            UndoToggle("撮影済みをスキップ", ref s.skipExisting);
            UndoToggle("再撮影警告文", ref s.showRetakeWarning);

            _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "詳細設定", true);
            if (!_showAdvanced) return;

            EditorGUI.indentLevel++;
            UndoToggle("アバターリストを上書きする", ref s.overwriteList);
            if (UndoToggle("NDMFで処理してから撮影(MA対応・推奨)", ref s.useNdmf)) Defer(CloseSession);
            using (new EditorGUI.DisabledScope(s.useNdmf && NdmfBridge.IsAvailable))
            {
                if (UndoToggle("簡易モード時にMA Bone Proxyを非表示", ref s.hideBoneProxy)) Defer(CloseSession);
            }

            EditorGUI.BeginChangeCheck();
            string names = EditorGUILayout.TextField(
                new GUIContent("撮影時に隠す名前", "名前に含まれる文字で一致(カンマ区切り)。例: 水着,Kimono"), s.hideNames);
            if (EditorGUI.EndChangeCheck())
            {
                RecordUndo("隠す名前");
                s.hideNames = names;
            }

            UndoToggle("SDKアイコン範囲(4:3)のガイドを表示", ref s.showCropGuide);

            if (!NdmfBridge.IsAvailable)
            {
                EditorGUILayout.HelpBox(
                    "NDMFが見つかりません。Modular Avatarの切り替え衣装・髪を正確に反映するにはNDMFが必要です。",
                    MessageType.Info);
            }
            EditorGUI.indentLevel--;
        }

        private void DrawPreviewSection()
        {
            if (_session == null || _previewEntry == null) return;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("プレビュー: " + _previewEntry.name, EditorStyles.boldLabel);
                if (GUILayout.Button("再構築", EditorStyles.miniButton, GUILayout.Width(56f)))
                {
                    AvatarEntry target = _previewEntry;
                    Defer(() => { CloseSession(); OpenSession(target); });
                }
                if (GUILayout.Button("このアバターを撮影", EditorStyles.miniButton, GUILayout.Width(110f)))
                {
                    Defer(CaptureFromPreview);
                }
                if (GUILayout.Button("閉じる", EditorStyles.miniButton, GUILayout.Width(50f)))
                {
                    Defer(CloseSession);
                }
            }
            EditorGUILayout.LabelField(_session.Description, EditorStyles.miniLabel);

            float size = Mathf.Max(128f, Mathf.Min(PreviewPixels, position.width - 40f));
            Rect rect;
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                rect = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(false), GUILayout.ExpandHeight(false));
                GUILayout.FlexibleSpace();
            }

            if (Event.current.type == EventType.Repaint)
            {
                _session.DrawLive(rect, s, ResolveBackground(_session));
                if (s.showCropGuide) DrawCropGuide(rect);
            }
            EditorGUILayout.Space();
        }

        /// <summary>SDKアイコンは4:3で中央切り出しされるため、上下の切れる範囲を暗くして示す。</summary>
        private static void DrawCropGuide(Rect rect)
        {
            float band = rect.height / 8f;
            Color shade = new Color(0f, 0f, 0f, 0.5f);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, band), shade);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - band, rect.width, band), shade);
        }

        private void DrawListSection()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(string.Format("アバターリスト ({0}体)", _entries.Count), EditorStyles.boldLabel);
                if (GUILayout.Button("リストを更新", EditorStyles.miniButton, GUILayout.Width(90f)))
                {
                    Defer(() => { ScanScene(s.overwriteList); SaveEntries(); });
                }
            }

            DrawDropArea();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("全選択", EditorStyles.miniButton)) SetAllSelected(true);
                if (GUILayout.Button("選択解除", EditorStyles.miniButton)) SetAllSelected(false);
            }

            if (_entries.Count == 0)
            {
                EditorGUILayout.HelpBox("アバターがありません。「リストを更新」またはドラッグ&ドロップで追加してください。", MessageType.Info);
                return;
            }

            for (int i = 0; i < _entries.Count; i++) DrawRow(i, _entries[i]);
        }

        private void DrawDropArea()
        {
            Rect r = GUILayoutUtility.GetRect(0f, 34f, GUILayout.ExpandWidth(true));
            GUI.Box(r, "Hierarchyのオブジェクトをここにドラッグ&ドロップで追加", EditorStyles.helpBox);

            Event ev = Event.current;
            if ((ev.type != EventType.DragUpdated && ev.type != EventType.DragPerform) || !r.Contains(ev.mousePosition))
            {
                return;
            }

            var dropped = new List<GameObject>();
            foreach (Object o in DragAndDrop.objectReferences)
            {
                GameObject go = o as GameObject;
                if (go != null && !EditorUtility.IsPersistent(go)) dropped.Add(go);
            }

            DragAndDrop.visualMode = dropped.Count > 0 ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
            if (ev.type == EventType.DragPerform && dropped.Count > 0)
            {
                DragAndDrop.AcceptDrag();
                Defer(() =>
                {
                    foreach (GameObject go in dropped) Upsert(go, true); // 手動追加は条件チェックなし
                    RefreshThumbFlags();
                    SaveEntries();
                });
            }
            ev.Use();
        }

        private void DrawRow(int index, AvatarEntry e)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                e.selected = EditorGUILayout.Toggle(e.selected, GUILayout.Width(16f));
                GUILayout.Label(new GUIContent(e.name, e.manual ? "手動追加" : "自動検出"),
                    GUILayout.MinWidth(90f), GUILayout.ExpandWidth(true));
                GUILayout.Label(StatusText(e), EditorStyles.miniLabel, GUILayout.Width(58f));

                using (new EditorGUI.DisabledScope(e.IsMissing))
                {
                    bool isPreview = _session != null && _previewEntry == e;
                    bool want = GUILayout.Toggle(isPreview, "プレビュー", EditorStyles.miniButton, GUILayout.Width(64f));
                    if (want != isPreview) Defer(() => TogglePreview(e));

                    if (GUILayout.Button("撮影", EditorStyles.miniButton, GUILayout.Width(40f)))
                    {
                        Defer(() => CaptureOne(e));
                    }
                }

                using (new EditorGUI.DisabledScope(e.IsMissing || !e.hasThumb))
                {
                    if (GUILayout.Button(new GUIContent("SDK", "撮影画像をVRChat SDKのアバターアイコンに設定"),
                        EditorStyles.miniButton, GUILayout.Width(36f)))
                    {
                        Defer(() => SetSdkIcon(e));
                    }
                }

                if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(22f)))
                {
                    Defer(() => RemoveEntry(e));
                }
                using (new EditorGUI.DisabledScope(index == 0))
                {
                    if (GUILayout.Button("↑", EditorStyles.miniButton, GUILayout.Width(22f))) Defer(() => MoveEntry(e, -1));
                }
                using (new EditorGUI.DisabledScope(index == _entries.Count - 1))
                {
                    if (GUILayout.Button("↓", EditorStyles.miniButton, GUILayout.Width(22f))) Defer(() => MoveEntry(e, 1));
                }
            }
        }

        private static string StatusText(AvatarEntry e)
        {
            if (e.IsMissing) return "(未検出)";
            if (e.IsHidden) return "(非表示)";
            return e.hasThumb ? "✓撮影済" : "";
        }

        private void DrawActionSection()
        {
            EditorGUILayout.LabelField("撮影実行", EditorStyles.boldLabel);
            if (GUILayout.Button("選択アバターのみ撮影", GUILayout.Height(30f)))
            {
                Defer(CaptureSelected);
            }

            EditorGUILayout.Space();
            if (GUILayout.Button("保存フォルダを開く", GUILayout.Height(24f)))
            {
                Defer(OpenSaveFolder);
            }
        }

        // ---- GUIヘルパー ----

        private void RecordUndo(string label)
        {
            if (_undoGroup < 0)
            {
                Undo.IncrementCurrentGroup();
                _undoGroup = Undo.GetCurrentGroup();
            }
            Undo.RecordObject(this, label);
        }

        /// <summary>スライダーのドラッグ中の細かいUndoを、マウスを離した時に1つへまとめる。</summary>
        private void CollapseUndoOnMouseUp()
        {
            if (_undoGroup >= 0 && Event.current.rawType == EventType.MouseUp)
            {
                Undo.CollapseUndoOperations(_undoGroup);
                _undoGroup = -1;
            }
        }

        private bool UndoSlider(string label, ref float value, float min, float max)
        {
            EditorGUI.BeginChangeCheck();
            float v = EditorGUILayout.Slider(label, value, min, max);
            if (!EditorGUI.EndChangeCheck()) return false;
            RecordUndo(label);
            value = v;
            return true;
        }

        private bool UndoToggle(string label, ref bool value)
        {
            EditorGUI.BeginChangeCheck();
            bool v = EditorGUILayout.Toggle(label, value);
            if (!EditorGUI.EndChangeCheck()) return false;
            RecordUndo(label);
            value = v;
            return true;
        }

        /// <summary>OnGUI中に重い処理やリスト変更をするとレイアウトが壊れるため、処理は次のエディタ更新で行う。</summary>
        private void Defer(System.Action action)
        {
            EditorApplication.delayCall += () =>
            {
                action();
                Repaint();
            };
        }

        private Color ResolveBackground(RenderSession session)
        {
            if (s.autoBackground && session != null && session.Stats.valid)
            {
                return BackgroundPalette.From(session.Stats, s.backgroundStyle);
            }
            return s.backgroundColor;
        }

        // ---- アバターリスト ----

        private void ReloadForScene()
        {
            CloseSession();
            _sceneKey = ThumbStore.SceneKey(SceneManager.GetActiveScene());

            // 現在のシーンのぶんだけを読み込む(別シーンのアバターはリストに残さない)
            _entries.Clear();
            foreach (AvatarRecord r in ThumbStore.Load(_sceneKey))
            {
                _entries.Add(new AvatarEntry
                {
                    id = r.id,
                    blueprintId = r.blueprintId,
                    name = r.name,
                    thumbnailPath = r.thumbnailPath,
                    memo = r.memo,
                    manual = r.manual,
                    sceneObject = AvatarIdentity.ResolveStableId(r.id)
                });
            }

            ScanScene(false);
            SaveEntries();
            Repaint();
        }

        private void ScanScene(bool overwrite)
        {
            var matched = new HashSet<AvatarEntry>();
            foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (AvatarIdentity.IsAvatar(root)) matched.Add(Upsert(root, false));
            }
            if (overwrite) _entries.RemoveAll(e => !matched.Contains(e));
            RefreshThumbFlags();
        }

        /// <summary>IDで既存エントリを探し、無ければ追加する。名前変更では別アバターにならない。</summary>
        private AvatarEntry Upsert(GameObject go, bool manual)
        {
            string id = AvatarIdentity.GetStableId(go);
            string blueprint = AvatarIdentity.GetBlueprintId(go);

            AvatarEntry e = null;
            if (id != null) e = _entries.Find(x => x.id == id);
            if (e == null && blueprint != null)
            {
                e = _entries.Find(x => x.blueprintId == blueprint && (x.sceneObject == null || x.sceneObject == go));
            }
            if (e == null)
            {
                // 旧バージョンのデータ(IDなし)は名前で一度だけ引き継ぐ
                e = _entries.Find(x => string.IsNullOrEmpty(x.id) && x.name == go.name && x.sceneObject == null);
            }
            if (e == null)
            {
                var used = new HashSet<string>(_entries.Select(x => x.thumbnailPath), System.StringComparer.OrdinalIgnoreCase);
                e = new AvatarEntry { thumbnailPath = ThumbStore.MakeThumbPath(_sceneKey, go.name, used) };
                _entries.Add(e);
            }

            e.sceneObject = go;
            e.name = go.name;
            if (id != null) e.id = id;
            if (blueprint != null) e.blueprintId = blueprint;
            if (manual) e.manual = true;
            return e;
        }

        private void RemoveEntry(AvatarEntry e)
        {
            if (!EditorUtility.DisplayDialog("確認", e.name + " をリストから削除しますか?\n(撮影済みの画像ファイルは残ります)", "削除", "キャンセル"))
            {
                return;
            }
            if (_previewEntry == e) CloseSession();
            _entries.Remove(e);
            SaveEntries();
        }

        private void MoveEntry(AvatarEntry e, int delta)
        {
            int from = _entries.IndexOf(e);
            int to = from + delta;
            if (from < 0 || to < 0 || to >= _entries.Count) return;
            _entries.RemoveAt(from);
            _entries.Insert(to, e);
            SaveEntries();
        }

        private void SetAllSelected(bool value)
        {
            foreach (AvatarEntry e in _entries) e.selected = value;
        }

        private void RefreshThumbFlags()
        {
            foreach (AvatarEntry e in _entries) e.hasThumb = File.Exists(e.thumbnailPath);
        }

        private void SaveEntries()
        {
            ThumbStore.Save(_sceneKey, _entries);
        }

        // ---- プレビュー ----

        private void TogglePreview(AvatarEntry e)
        {
            bool same = _session != null && _previewEntry == e;
            CloseSession();
            if (!same) OpenSession(e);
        }

        private void OpenSession(AvatarEntry e)
        {
            if (e == null || e.sceneObject == null) return;
            try
            {
                EditorUtility.DisplayProgressBar("プレビュー準備中", e.name, 0.5f);
                _session = RenderSession.Create(e.sceneObject, s);
                _previewEntry = e;
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[AvatarThumbnail] プレビュー作成に失敗: " + ex);
                _session = null;
                _previewEntry = null;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private void CloseSession()
        {
            if (_session != null)
            {
                _session.Dispose();
                _session = null;
            }
            _previewEntry = null;
        }

        // ---- 撮影 ----

        private void CaptureSelected()
        {
            List<AvatarEntry> targets = _entries.Where(e => e.selected && !e.IsMissing).ToList();
            if (targets.Count == 0)
            {
                EditorUtility.DisplayDialog("エラー", "撮影対象のアバターがありません", "OK");
                return;
            }

            RefreshThumbFlags();
            int existing = s.skipExisting ? targets.Count(e => e.hasThumb) : 0;
            if (s.showRetakeWarning && existing > 0)
            {
                string message =
                    "撮影済のアバターが " + existing + " 体含まれるため、スキップされますがよろしいですか？\n\n" +
                    "※プレビュー表示と撮影画像はアバター表示が異なる場合がございます\n" +
                    "※この警告文は「再撮影警告文」オプションをオフで非表示にできます";
                if (!EditorUtility.DisplayDialog("確認", message, "撮影する", "キャンセル")) return;
            }

            RunCapture(targets, false);
        }

        private void CaptureOne(AvatarEntry e)
        {
            RefreshThumbFlags();
            if (e.hasThumb && !EditorUtility.DisplayDialog("確認", e.name + " は既に撮影済みです。\n再撮影しますか?", "撮影する", "キャンセル"))
            {
                return;
            }
            RunCapture(new List<AvatarEntry> { e }, true);
        }

        private void CaptureFromPreview()
        {
            if (_session == null || _previewEntry == null) return;
            try
            {
                SaveThumbnail(_session, _previewEntry);
                AssetDatabase.Refresh();
                ShowNotification(new GUIContent(_previewEntry.name + " を撮影しました"));
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[AvatarThumbnail] 撮影に失敗: " + ex);
                EditorUtility.DisplayDialog("撮影失敗", ex.Message, "OK");
            }
        }

        private void RunCapture(List<AvatarEntry> targets, bool force)
        {
            int done = 0, skipped = 0, failed = 0;
            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    AvatarEntry e = targets[i];
                    string label = string.Format("{0} ({1}/{2})", e.name, i + 1, targets.Count);
                    if (EditorUtility.DisplayCancelableProgressBar("サムネイル撮影", label, (float)i / targets.Count)) break;

                    if (!force && s.skipExisting && File.Exists(e.thumbnailPath))
                    {
                        skipped++;
                        continue;
                    }

                    try
                    {
                        using (RenderSession session = RenderSession.Create(e.sceneObject, s))
                        {
                            SaveThumbnail(session, e);
                        }
                        done++;
                    }
                    catch (System.Exception ex)
                    {
                        failed++;
                        Debug.LogError("[AvatarThumbnail] " + e.name + " の撮影に失敗: " + ex);
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            AssetDatabase.Refresh();
            RefreshThumbFlags();
            EditorUtility.DisplayDialog("撮影完了",
                string.Format("撮影: {0}体\nスキップ: {1}体\n失敗: {2}体", done, skipped, failed), "OK");
        }

        private void SaveThumbnail(RenderSession session, AvatarEntry e)
        {
            session.PlaceCamera(s);
            Texture2D tex = session.RenderOpaque(ThumbnailSize, ResolveBackground(session));
            if (tex == null) throw new System.InvalidOperationException("レンダリング結果が空でした");
            try
            {
                string dir = Path.GetDirectoryName(e.thumbnailPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(e.thumbnailPath, tex.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(tex);
            }
            e.hasThumb = true;
        }

        private void OpenSaveFolder()
        {
            Directory.CreateDirectory(ThumbStore.LogFolder);
            AssetDatabase.Refresh();

            Object folder = AssetDatabase.LoadAssetAtPath<Object>(ThumbStore.LogFolder);
            if (folder == null) return;
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = folder;
            EditorGUIUtility.PingObject(folder);
        }

        // ---- VRChat SDK アイコン ----

        private async void SetSdkIcon(AvatarEntry e)
        {
            if (e.sceneObject == null || !File.Exists(e.thumbnailPath)) return;

            string imagePath = SdkIconBridge.CreateSdkImage(e.thumbnailPath);
            if (imagePath == null)
            {
                EditorUtility.DisplayDialog("SDKアイコン", "SDK用の画像を作成できませんでした。", "OK");
                return;
            }

            string blueprint = AvatarIdentity.GetBlueprintId(e.sceneObject);
            string message =
                "「" + e.name + "」の撮影画像をVRChatのアバターアイコンに設定します。\n\n" +
                "・自動で設定: SDKのAPIでオンラインのアバターのアイコンを更新します(すぐ反映されます)\n" +
                "・手動で設定: SDKパネルを開き、画像の場所を開いてパスをコピーします\n\n" +
                "Blueprint ID: " + (string.IsNullOrEmpty(blueprint) ? "(未設定: 未アップロード)" : blueprint) + "\n" +
                "※アイコンは4:3で中央が切り出されます(プレビューのガイド参照)";

            int choice = EditorUtility.DisplayDialogComplex("SDKアイコンに設定", message, "自動で設定", "キャンセル", "手動で設定");
            if (choice == 1) return;

            if (choice == 2)
            {
                ManualSdkGuide(imagePath);
                return;
            }

            if (string.IsNullOrEmpty(blueprint))
            {
                EditorUtility.DisplayDialog("SDKアイコン",
                    "このアバターにはBlueprint IDがありません(未アップロード)。\n手動設定の手順を案内します。", "OK");
                ManualSdkGuide(imagePath);
                return;
            }

            SdkIconResult result;
            try
            {
                EditorUtility.DisplayProgressBar("SDKアイコン", "VRChatへ送信中...", 0.5f);
                result = await SdkIconBridge.TryUploadAsync(blueprint, imagePath);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (result.ok)
            {
                EditorUtility.DisplayDialog("SDKアイコン", result.message, "OK");
                return;
            }

            Debug.LogWarning("[AvatarThumbnail] SDKアイコンの自動設定に失敗: " + result.message);
            EditorUtility.DisplayDialog("SDKアイコン",
                "自動設定に失敗しました。\n" + result.message + "\n\n手動設定の手順を案内します。", "OK");
            ManualSdkGuide(imagePath);
        }

        private static void ManualSdkGuide(string imagePath)
        {
            SdkIconBridge.PrepareManual(imagePath);
            EditorUtility.DisplayDialog("手動で設定",
                "SDKコントロールパネルを開き、画像の場所を開きました(パスはクリップボードにもコピー済み)。\n\n" +
                "Builder → 対象アバター → サムネイルの「Select Image」で、次のファイルを選んでください。\n" + imagePath,
                "OK");
        }
    }

    #endregion
}
#endif
