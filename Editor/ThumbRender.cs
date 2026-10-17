#if UNITY_EDITOR
// ============================================================================
//  撮影エンジン
//   - 撮影は「隔離プレビューシーン」に対象アバターの複製を作って行う。元シーンは触らない。
//   - 既定の SceneExact は、シーン上の「いまの表示状態(アクティブ/レンダラー有効)」を複製へ写し取り、
//     シーン側の変更を軽量ポーリングで追従する(リアルタイム同期)。
//   - MA Bone Proxy 配下・EditorOnly タグ・指定名のオブジェクトは常に隠す(ビルド後に表示されないため)。
//   - NdmfBuild は NDMF でビルド処理した結果を撮る(MAの既定ON/OFFが適用されるためシーンと異なることがある)。
//   - NDMF / VRChat SDK は直接参照せずリフレクションで呼ぶ。
// ============================================================================
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LollipopCandy.AvatarHub
{
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

    #region Background

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

    /// <summary>描画直前に確定した背景(Auto は色に解決済み)。</summary>
    internal sealed class ResolvedBg
    {
        public BgMode mode = BgMode.Solid;   // Solid / Gradient / Transparent / Image
        public Color solid = Color.black;
        public Color top = Color.black;
        public Color bottom = Color.black;
        public Color32[] image;
        public int imgW, imgH;
        public bool checker;                  // 透過のプレビュー用市松模様
        public bool keepAlpha;                // RGBA出力(透過PNG)
    }

    internal static class BgResolver
    {
        private sealed class ImgData { public System.DateTime stamp; public Color32[] px; public int w, h; }
        private static readonly Dictionary<string, ImgData> Cache = new Dictionary<string, ImgData>();

        public static ResolvedBg Resolve(BackgroundSpec spec, RenderSession session, OutFormat fmt, bool forPreview)
        {
            var r = new ResolvedBg();
            switch (spec.mode)
            {
                case BgMode.Auto:
                    r.mode = BgMode.Solid;
                    r.solid = (session != null && session.GetStats().valid)
                        ? BackgroundPalette.From(session.GetStats(), spec.style)
                        : spec.solid;
                    break;
                case BgMode.Solid:
                    r.mode = BgMode.Solid;
                    r.solid = spec.solid;
                    break;
                case BgMode.Gradient:
                    r.mode = BgMode.Gradient;
                    r.top = spec.top;
                    r.bottom = spec.bottom;
                    break;
                case BgMode.Transparent:
                    if (forPreview)
                    {
                        r.mode = BgMode.Transparent;
                        r.checker = true;
                    }
                    else if (fmt == OutFormat.Jpg)
                    {
                        r.mode = BgMode.Solid;          // JPGは透過できないので白
                        r.solid = Color.white;
                    }
                    else
                    {
                        r.mode = BgMode.Transparent;
                        r.keepAlpha = true;
                    }
                    break;
                case BgMode.Image:
                    ImgData d = LoadImage(spec.imagePath);
                    if (d == null)
                    {
                        r.mode = BgMode.Solid;
                        r.solid = spec.solid;
                    }
                    else
                    {
                        r.mode = BgMode.Image;
                        r.image = d.px;
                        r.imgW = d.w;
                        r.imgH = d.h;
                    }
                    break;
            }
            r.solid.a = 1f;
            return r;
        }

        private static ImgData LoadImage(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                System.DateTime stamp = File.GetLastWriteTimeUtc(path);
                ImgData d;
                if (Cache.TryGetValue(path, out d) && d.stamp == stamp) return d;

                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                try
                {
                    if (!tex.LoadImage(File.ReadAllBytes(path))) return null;
                    d = new ImgData { stamp = stamp, px = tex.GetPixels32(), w = tex.width, h = tex.height };
                }
                finally
                {
                    Object.DestroyImmediate(tex);
                }
                Cache[path] = d;
                return d;
            }
            catch (System.Exception)
            {
                return null;
            }
        }
    }

    /// <summary>黒背景/白背景の2回描画から、アバター領域(アルファ)を復元して任意の背景に合成する。</summary>
    internal static class Composer
    {
        private static float[] _dec;
        private static bool _decLinear;

        private static float[] Decode()
        {
            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            if (_dec == null || _decLinear != linear)
            {
                _dec = new float[256];
                for (int i = 0; i < 256; i++)
                {
                    float v = i / 255f;
                    _dec[i] = linear ? Mathf.GammaToLinearSpace(v) : v;
                }
                _decLinear = linear;
            }
            return _dec;
        }

        private static byte ToByte(float v)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);
        }

        public static Color32[] Compose(Color32[] onBlack, Color32[] onWhite, int w, int h, ResolvedBg bg)
        {
            float[] dec = Decode();
            bool linear = _decLinear;
            var outPx = new Color32[w * h];

            Color solid = bg.solid;
            Color top = bg.top, bottom = bg.bottom;
            float scale = bg.mode == BgMode.Image ? Mathf.Max(w / (float)bg.imgW, h / (float)bg.imgH) : 1f;

            for (int y = 0; y < h; y++)
            {
                float ty = h > 1 ? y / (h - 1f) : 0f; // y=0 は画像の下端
                Color rowGrad = bg.mode == BgMode.Gradient ? Color.Lerp(bottom, top, ty) : solid;

                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    Color32 a = onBlack[i];
                    Color32 b = onWhite[i];
                    int d = (b.r - a.r) + (b.g - a.g) + (b.b - a.b);

                    if (d <= 0) // 完全に不透明(背景が透けない)
                    {
                        outPx[i] = new Color32(a.r, a.g, a.b, 255);
                        continue;
                    }

                    // 背景色
                    Color bgc;
                    switch (bg.mode)
                    {
                        case BgMode.Gradient: bgc = rowGrad; break;
                        case BgMode.Image: bgc = SampleImage(bg, x, y, w, h, scale); break;
                        case BgMode.Transparent:
                            bgc = (((x >> 3) + (y >> 3)) & 1) == 0 ? new Color(0.80f, 0.80f, 0.80f) : new Color(0.62f, 0.62f, 0.62f);
                            break;
                        default: bgc = solid; break;
                    }

                    if (d >= 765 - 3) // 完全に背景
                    {
                        if (bg.keepAlpha) outPx[i] = new Color32(0, 0, 0, 0);
                        else outPx[i] = new Color32(ToByte(bgc.r), ToByte(bgc.g), ToByte(bgc.b), 255);
                        continue;
                    }

                    // 半透明の縁: リニア空間でアルファを求める
                    float ar = dec[a.r], ag = dec[a.g], ab = dec[a.b];
                    float wr = dec[b.r], wg = dec[b.g], wb = dec[b.b];
                    float alpha = 1f - ((wr - ar) + (wg - ag) + (wb - ab)) / 3f;
                    alpha = Mathf.Clamp01(alpha);

                    if (bg.keepAlpha)
                    {
                        float inv = alpha > 0.0001f ? 1f / alpha : 0f;
                        float fr = Mathf.Clamp01(ar * inv), fg = Mathf.Clamp01(ag * inv), fb = Mathf.Clamp01(ab * inv);
                        if (linear)
                        {
                            fr = Mathf.LinearToGammaSpace(fr);
                            fg = Mathf.LinearToGammaSpace(fg);
                            fb = Mathf.LinearToGammaSpace(fb);
                        }
                        outPx[i] = new Color32(ToByte(fr), ToByte(fg), ToByte(fb), ToByte(alpha));
                    }
                    else
                    {
                        float br = linear ? Mathf.GammaToLinearSpace(bgc.r) : bgc.r;
                        float bgG = linear ? Mathf.GammaToLinearSpace(bgc.g) : bgc.g;
                        float bb = linear ? Mathf.GammaToLinearSpace(bgc.b) : bgc.b;
                        float rr = ar + br * (1f - alpha);
                        float rg = ag + bgG * (1f - alpha);
                        float rb = ab + bb * (1f - alpha);
                        if (linear)
                        {
                            rr = Mathf.LinearToGammaSpace(rr);
                            rg = Mathf.LinearToGammaSpace(rg);
                            rb = Mathf.LinearToGammaSpace(rb);
                        }
                        outPx[i] = new Color32(ToByte(rr), ToByte(rg), ToByte(rb), 255);
                    }
                }
            }
            return outPx;
        }

        /// <summary>画像背景: 画面全体を覆う(cover)拡大でバイリニア取得。</summary>
        private static Color SampleImage(ResolvedBg bg, int x, int y, int w, int h, float scale)
        {
            float sx = (x + 0.5f - w * 0.5f) / scale + bg.imgW * 0.5f - 0.5f;
            float sy = (y + 0.5f - h * 0.5f) / scale + bg.imgH * 0.5f - 0.5f;
            int x0 = Mathf.Clamp((int)Mathf.Floor(sx), 0, bg.imgW - 1);
            int y0 = Mathf.Clamp((int)Mathf.Floor(sy), 0, bg.imgH - 1);
            int x1 = Mathf.Min(x0 + 1, bg.imgW - 1);
            int y1 = Mathf.Min(y0 + 1, bg.imgH - 1);
            float fx = Mathf.Clamp01(sx - x0), fy = Mathf.Clamp01(sy - y0);

            Color32 c00 = bg.image[y0 * bg.imgW + x0], c10 = bg.image[y0 * bg.imgW + x1];
            Color32 c01 = bg.image[y1 * bg.imgW + x0], c11 = bg.image[y1 * bg.imgW + x1];
            float r = Bil(c00.r, c10.r, c01.r, c11.r, fx, fy);
            float g = Bil(c00.g, c10.g, c01.g, c11.g, fx, fy);
            float b = Bil(c00.b, c10.b, c01.b, c11.b, fx, fy);
            return new Color(r / 255f, g / 255f, b / 255f);
        }

        private static float Bil(float a, float b, float c, float d, float fx, float fy)
        {
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }
    }

    #endregion

    #region NDMF

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

    #endregion

    #region Render session

    internal struct CamRig
    {
        public Vector3 pos;
        public Vector3 rot;
        public bool autoHeight;

        public CamRig(Vector3 pos, Vector3 rot, bool autoHeight)
        {
            this.pos = pos;
            this.rot = rot;
            this.autoHeight = autoHeight;
        }

        public int Hash()
        {
            unchecked
            {
                int h = pos.GetHashCode();
                h = h * 31 + rot.GetHashCode();
                h = h * 31 + (autoHeight ? 1 : 0);
                return h;
            }
        }
    }

    /// <summary>
    /// 1アバターぶんの撮影環境。隔離プレビューシーンに複製を置く。
    /// プレビュー表示と撮影で同じセッションを使うため、見た目が一致する。
    /// </summary>
    internal sealed class RenderSession : System.IDisposable
    {
        private static int _alive;
        private static bool _needsClean;

        private PreviewRenderUtility _pru;
        private GameObject _root;
        private bool _ndmfApplied;
        private bool _sceneExact;

        // シーン状態の同期(SceneExact)
        private Transform[] _srcT = new Transform[0];
        private Transform[] _dstT = new Transform[0];
        private bool[] _forced = new bool[0];
        private Renderer[] _srcR = new Renderer[0];
        private Renderer[] _dstR = new Renderer[0];
        private bool[] _forcedR = new bool[0];
        private string[] _paths = new string[0];     // _srcT と並行: ルートからの相対パス
        private string[] _rPaths = new string[0];    // _srcR と並行
        private bool[] _ovHide = new bool[0];
        private bool[] _ovShow = new bool[0];
        private bool[] _ovHideR = new bool[0];
        private bool[] _ovShowR = new bool[0];
        private Transform _srcRoot;

        // ポーズ・表情の復元用
        private Transform[] _poseT = new Transform[0];
        private Vector3[] _basePos = new Vector3[0];
        private Quaternion[] _baseRot = new Quaternion[0];
        private Vector3[] _baseScale = new Vector3[0];
        private SkinnedMeshRenderer[] _smrs = new SkinnedMeshRenderer[0];
        private float[][] _baseWeights = new float[0][];

        private Animator _animator;
        private ColorStats _stats;
        private bool _statsValid;

        public Bounds Bounds { get; private set; }
        public string Description { get; private set; }
        public bool NdmfFailed { get; private set; }
        public bool SupportsLiveSync { get { return _sceneExact; } }

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

            bool wantNdmf = s.Mode == CaptureMode.NdmfBuild;
            if (wantNdmf && NdmfBridge.IsAvailable)
            {
                try
                {
                    CreateClone(source);
                    _needsClean = true;
                    NdmfBridge.Process(_root);
                    _ndmfApplied = true;
                    Description = "NDMFビルド結果で撮影(MAの既定状態が適用)";
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning("[AvatarThumbnail] NDMF処理に失敗したためシーンの見た目で撮影します: " + e.Message);
                    DestroyClone();
                    NdmfFailed = true;
                }
            }

            if (_root == null)
            {
                CreateClone(source);
                _sceneExact = true;
                BuildPairs(source, s);
                SyncFromScene();
                if (NdmfFailed) Description = "NDMF処理に失敗 → シーンの見た目で撮影";
                else if (wantNdmf) Description = "NDMF未導入 → シーンの見た目で撮影";
                else Description = "シーンの見た目をそのまま撮影";
            }

            _animator = _root.GetComponent<Animator>();
            CapturePoseSnapshot();
            RecalcBounds();
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

            clone.SetActive(true); // 元が非アクティブでも撮れるよう、ルートだけは有効化
            _root = clone;
        }

        private void DestroyClone()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            _root = null;
        }

        // ---- シーン状態の同期 ----

        /// <summary>元アバターと複製の対応表を作り、常に隠すもの(MA Bone Proxy等)を決める。</summary>
        private void BuildPairs(GameObject source, ThumbSettings s)
        {
            var srcList = new List<Transform>();
            var dstList = new List<Transform>();
            var stack = new Stack<KeyValuePair<Transform, Transform>>();
            stack.Push(new KeyValuePair<Transform, Transform>(source.transform, _root.transform));

            while (stack.Count > 0)
            {
                KeyValuePair<Transform, Transform> pair = stack.Pop();
                Transform sp = pair.Key, dp = pair.Value;
                if (sp.childCount != dp.childCount) continue; // 構造が違う枝は追従しない
                for (int i = 0; i < sp.childCount; i++)
                {
                    Transform sc = sp.GetChild(i), dc = dp.GetChild(i);
                    srcList.Add(sc);
                    dstList.Add(dc);
                    stack.Push(new KeyValuePair<Transform, Transform>(sc, dc));
                }
            }

            _srcT = srcList.ToArray();
            _dstT = dstList.ToArray();
            _forced = new bool[_dstT.Length];
            _srcRoot = source.transform;
            _paths = new string[_srcT.Length];
            for (int i = 0; i < _srcT.Length; i++) _paths[i] = RelativePath(_srcT[i]);
            _ovHide = new bool[_srcT.Length];
            _ovShow = new bool[_srcT.Length];

            string[] tokens = s.HideTokens();
            for (int i = 0; i < _dstT.Length; i++)
            {
                GameObject g = _dstT[i].gameObject;
                bool hide = false;
                if (s.hideEditorOnly && g.CompareTag("EditorOnly")) hide = true;
                if (!hide && s.hideBoneProxy && AvatarIdentity.HasComponentNamed(g, "ModularAvatarBoneProxy")) hide = true;
                if (!hide && tokens.Length > 0)
                {
                    for (int k = 0; k < tokens.Length; k++)
                    {
                        if (g.name.IndexOf(tokens[k], System.StringComparison.OrdinalIgnoreCase) >= 0) { hide = true; break; }
                    }
                }
                _forced[i] = hide;
            }

            var sr = new List<Renderer>();
            var dr = new List<Renderer>();
            var fr = new List<bool>();
            var rp = new List<string>();
            Renderer rootSrc = source.GetComponent<Renderer>();
            Renderer rootDst = _root.GetComponent<Renderer>();
            if (rootSrc != null && rootDst != null) { sr.Add(rootSrc); dr.Add(rootDst); fr.Add(false); rp.Add(""); }
            for (int i = 0; i < _srcT.Length; i++)
            {
                Renderer a = _srcT[i].GetComponent<Renderer>();
                Renderer b = _dstT[i].GetComponent<Renderer>();
                if (a == null || b == null) continue;
                sr.Add(a);
                dr.Add(b);
                fr.Add(_forced[i]);
                rp.Add(_paths[i]);
            }
            _srcR = sr.ToArray();
            _dstR = dr.ToArray();
            _forcedR = fr.ToArray();
            _rPaths = rp.ToArray();
            _ovHideR = new bool[_srcR.Length];
            _ovShowR = new bool[_srcR.Length];
        }

        private string RelativePath(Transform t)
        {
            var parts = new List<string>();
            while (t != null && t != _srcRoot)
            {
                parts.Add(t.name);
                t = t.parent;
            }
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        /// <summary>撮影に含められる(Rendererを持つ)オブジェクトの一覧。パスとシーン上の表示状態。</summary>
        public List<KeyValuePair<string, bool>> ListRendererObjects()
        {
            var list = new List<KeyValuePair<string, bool>>();
            var seen = new HashSet<string>();
            for (int i = 0; i < _srcT.Length; i++)
            {
                if (_srcT[i] == null) continue;
                if (_srcT[i].GetComponent<Renderer>() == null) continue;
                if (!seen.Add(_paths[i])) continue;
                list.Add(new KeyValuePair<string, bool>(_paths[i], _srcT[i].gameObject.activeInHierarchy));
            }
            return list;
        }

        /// <summary>アバター個別の「隠す / 強制表示」を設定する(SceneExactのみ)。</summary>
        public void SetVisibilityOverrides(List<string> hide, List<string> show)
        {
            if (!_sceneExact) return;
            var hs = new HashSet<string>(hide ?? new List<string>());
            var ss = new HashSet<string>();
            if (show != null)
            {
                foreach (string p in show)
                {
                    // 強制表示は親も有効にしないと見えないため、祖先も対象にする
                    string cur = p;
                    while (!string.IsNullOrEmpty(cur))
                    {
                        ss.Add(cur);
                        int k = cur.LastIndexOf('/');
                        cur = k >= 0 ? cur.Substring(0, k) : "";
                    }
                }
            }
            var exactShow = new HashSet<string>(show ?? new List<string>());
            for (int i = 0; i < _paths.Length; i++)
            {
                _ovHide[i] = hs.Contains(_paths[i]);
                _ovShow[i] = !_ovHide[i] && ss.Contains(_paths[i]);
            }
            for (int i = 0; i < _rPaths.Length; i++)
            {
                _ovHideR[i] = hs.Contains(_rPaths[i]);
                _ovShowR[i] = !_ovHideR[i] && exactShow.Contains(_rPaths[i]);
            }
            SyncFromScene();
        }

        /// <summary>元シーンの表示状態(GameObjectのactive/Rendererのenabled)を複製へ反映する。変化があれば true。</summary>
        public bool SyncFromScene()
        {
            if (!_sceneExact) return false;
            bool changed = false;

            for (int i = 0; i < _srcT.Length; i++)
            {
                Transform s = _srcT[i], d = _dstT[i];
                if (s == null || d == null) continue;
                bool want = _ovShow[i] || (!_forced[i] && !_ovHide[i] && s.gameObject.activeSelf);
                GameObject dg = d.gameObject;
                if (dg.activeSelf != want)
                {
                    dg.SetActive(want);
                    changed = true;
                }
            }

            for (int i = 0; i < _srcR.Length; i++)
            {
                Renderer s = _srcR[i], d = _dstR[i];
                if (s == null || d == null) continue;
                bool want = _ovShowR[i] || (!_forcedR[i] && !_ovHideR[i] && s.enabled);
                if (d.enabled != want)
                {
                    d.enabled = want;
                    changed = true;
                }
            }

            if (changed)
            {
                RecalcBounds();
                _statsValid = false;
            }
            return changed;
        }

        // ---- ポーズ・表情 ----

        private void CapturePoseSnapshot()
        {
            _poseT = _root.GetComponentsInChildren<Transform>(true);
            _basePos = new Vector3[_poseT.Length];
            _baseRot = new Quaternion[_poseT.Length];
            _baseScale = new Vector3[_poseT.Length];
            for (int i = 0; i < _poseT.Length; i++)
            {
                _basePos[i] = _poseT[i].localPosition;
                _baseRot[i] = _poseT[i].localRotation;
                _baseScale[i] = _poseT[i].localScale;
            }

            _smrs = _root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            _baseWeights = new float[_smrs.Length][];
            for (int i = 0; i < _smrs.Length; i++)
            {
                Mesh m = _smrs[i].sharedMesh;
                int n = m != null ? m.blendShapeCount : 0;
                _baseWeights[i] = new float[n];
                for (int k = 0; k < n; k++) _baseWeights[i][k] = _smrs[i].GetBlendShapeWeight(k);
            }
        }

        private void RestoreBase()
        {
            for (int i = 0; i < _poseT.Length; i++)
            {
                if (_poseT[i] == null) continue;
                _poseT[i].localPosition = _basePos[i];
                _poseT[i].localRotation = _baseRot[i];
                _poseT[i].localScale = _baseScale[i];
            }
            for (int i = 0; i < _smrs.Length; i++)
            {
                if (_smrs[i] == null) continue;
                for (int k = 0; k < _baseWeights[i].Length; k++) _smrs[i].SetBlendShapeWeight(k, _baseWeights[i][k]);
            }
        }

        /// <summary>保存済みのポーズ(アニメーションクリップ)と表情(ブレンドシェイプ)を複製に適用する。</summary>
        public void ApplyLook(AvatarLook look)
        {
            RestoreBase();
            if (look != null) SetVisibilityOverrides(look.hidePaths, look.showPaths);
            if (look != null)
            {
                AnimationClip clip = ThumbAssets.LoadClip(look.clipGuid);
                if (clip != null)
                {
                    try { clip.SampleAnimation(_root, Mathf.Clamp(look.clipTime, 0f, clip.length)); }
                    catch (System.Exception e) { Debug.LogWarning("[AvatarThumbnail] ポーズの適用に失敗: " + e.Message); }
                }
                if (look.blends != null) ApplyBlends(look.blends);
            }
            RecalcBounds();
            _statsValid = false;
        }

        private void ApplyBlends(List<BlendOverride> blends)
        {
            for (int b = 0; b < blends.Count; b++)
            {
                if (string.IsNullOrEmpty(blends[b].shape)) continue;
                for (int i = 0; i < _smrs.Length; i++)
                {
                    if (_smrs[i] == null || _smrs[i].sharedMesh == null) continue;
                    int idx = _smrs[i].sharedMesh.GetBlendShapeIndex(blends[b].shape);
                    if (idx >= 0) _smrs[i].SetBlendShapeWeight(idx, blends[b].weight);
                }
            }
        }

        public List<string> BlendShapeNames()
        {
            var set = new SortedDictionary<string, bool>(System.StringComparer.Ordinal);
            for (int i = 0; i < _smrs.Length; i++)
            {
                Mesh m = _smrs[i] != null ? _smrs[i].sharedMesh : null;
                if (m == null) continue;
                for (int k = 0; k < m.blendShapeCount; k++) set[m.GetBlendShapeName(k)] = true;
            }
            return set.Keys.ToList();
        }

        // ---- 範囲・カメラ ----

        private void RecalcBounds()
        {
            bool has = false;
            Bounds b = new Bounds(_root.transform.position + Vector3.up * 0.8f, Vector3.one);
            foreach (Renderer r in _root.GetComponentsInChildren<Renderer>(false))
            {
                if (!r.enabled) continue;
                if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                if (!has) { b = r.bounds; has = true; }
                else { b.Encapsulate(r.bounds); }
            }
            Bounds = b;
        }

        public Vector3 BasePoint(bool autoHeight)
        {
            return autoHeight ? Bounds.center : _root.transform.position;
        }

        public void PlaceCamera(CamRig rig)
        {
            Transform t = _pru.camera.transform;
            t.position = BasePoint(rig.autoHeight) + rig.pos;
            t.rotation = Quaternion.Euler(rig.rot.x, rig.rot.y, 0f);
        }

        public float CameraDistance(CamRig rig)
        {
            Vector3 cam = BasePoint(rig.autoHeight) + rig.pos;
            return Mathf.Max(0.15f, Vector3.Distance(cam, Bounds.center));
        }

        public float VerticalFovRad
        {
            get { return _pru.camera.fieldOfView * Mathf.Deg2Rad; }
        }

        /// <summary>構図プリセットに合うカメラ位置を計算する(正面、アバターの大きさに自動で合わせる)。</summary>
        public CamRig ComputeFraming(Framing f, bool autoHeight, float aspect)
        {
            Bounds b = Bounds;
            float H = Mathf.Max(0.2f, b.size.y);
            Vector3 c = b.center;
            float t = Mathf.Tan(_pru.camera.fieldOfView * 0.5f * Mathf.Deg2Rad);

            float top = b.max.y;
            Transform head = (_animator != null && _animator.isHuman) ? _animator.GetBoneTransform(HumanBodyBones.Head) : null;
            if (head != null) top = Mathf.Min(top, head.position.y + 0.16f * H); // 頭上の大きな小物に引っ張られない

            float cy, rh, rw = 0f;
            switch (f)
            {
                case Framing.Bust:
                {
                    float tp = top + 0.02f * H;
                    float bt = tp - 0.38f * H;
                    cy = (tp + bt) * 0.5f;
                    rh = tp - bt;
                    break;
                }
                case Framing.Face:
                {
                    float tp = top + 0.015f * H;
                    float bt = tp - 0.20f * H;
                    cy = (tp + bt) * 0.5f;
                    rh = tp - bt;
                    break;
                }
                default:
                    cy = c.y;
                    rh = H * 1.08f;
                    rw = Mathf.Max(b.size.x, b.size.z) * 1.05f;
                    break;
            }

            float d = rh * 0.5f / t;
            if (rw > 0f) d = Mathf.Max(d, rw * 0.5f / (t * Mathf.Max(0.2f, aspect)));
            if (f == Framing.Full) d = Mathf.Max(d, b.extents.z + 0.2f);
            d = Mathf.Max(d, 0.25f);

            Vector3 world = new Vector3(c.x, cy, c.z + d);
            Vector3 basePt = BasePoint(autoHeight);
            return new CamRig(world - basePt, new Vector3(0f, 180f, 0f), autoHeight);
        }

        // ---- 描画 ----

        private Texture2D RenderRaw(int w, int h, Color background)
        {
            Camera cam = _pru.camera;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = background;
            cam.aspect = w / (float)h;
            _pru.BeginStaticPreview(new Rect(0f, 0f, w, h));
            cam.Render();
            return _pru.EndStaticPreview();
        }

        private Color32[] RenderPixels(int w, int h, Color background)
        {
            Texture2D tex = RenderRaw(w, h, background);
            if (tex == null) return null;
            Color32[] px = tex.GetPixels32();
            Object.DestroyImmediate(tex);
            return px;
        }

        /// <summary>
        /// 黒/白の2回描画 → 合成して指定サイズの画像にする。
        /// reuse があり同サイズ・同形式ならそれを再利用して返す(プレビュー用)。呼び出し側で破棄すること。
        /// </summary>
        public Texture2D RenderComposite(int w, int h, CamRig rig, ResolvedBg bg, Texture2D reuse)
        {
            PlaceCamera(rig);
            Color32[] onBlack = RenderPixels(w, h, Color.black);
            Color32[] onWhite = RenderPixels(w, h, Color.white);
            if (onBlack == null || onWhite == null || onBlack.Length != onWhite.Length || onBlack.Length != w * h) return null;

            Color32[] px = Composer.Compose(onBlack, onWhite, w, h, bg);

            TextureFormat fmt = bg.keepAlpha ? TextureFormat.RGBA32 : TextureFormat.RGB24;
            Texture2D tex = reuse;
            if (tex == null || tex.width != w || tex.height != h || tex.format != fmt)
            {
                if (tex != null) Object.DestroyImmediate(tex);
                tex = new Texture2D(w, h, fmt, false);
            }
            tex.SetPixels32(px);
            tex.Apply(false);
            return tex;
        }

        // ---- 配色の自動解析 ----

        public ColorStats GetStats()
        {
            if (!_statsValid)
            {
                _stats = AnalyzeColors();
                _statsValid = true;
            }
            return _stats;
        }

        /// <summary>黒背景/白背景の差分でアバター領域を抽出し、主色相と平均輝度を求める。</summary>
        private ColorStats AnalyzeColors()
        {
            const int size = 96;
            Camera cam = _pru.camera;
            Bounds b = Bounds;
            float half = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float dist = Mathf.Max(b.extents.y, b.extents.x) / half * 1.15f + b.extents.z;
            cam.transform.position = b.center + new Vector3(0f, 0f, dist);
            cam.transform.rotation = Quaternion.Euler(0f, 180f, 0f);

            Color32[] onBlack = RenderPixels(size, size, Color.black);
            Color32[] onWhite = RenderPixels(size, size, Color.white);
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

        // ---- 事前チェック ----

        public List<CheckIssue> RunChecks(CamRig rig, float aspect, Framing framing)
        {
            var issues = new List<CheckIssue>();
            if (NdmfFailed) issues.Add(new CheckIssue(1, "NDMF処理に失敗したため、シーンの見た目で撮影します。"));

            Renderer[] rs = _root.GetComponentsInChildren<Renderer>(false)
                .Where(r => r.enabled && !(r is ParticleSystemRenderer) && !(r is TrailRenderer) && !(r is LineRenderer))
                .ToArray();
            if (rs.Length == 0)
            {
                issues.Add(new CheckIssue(2, "表示されるメッシュがありません(すべて非表示、または空のアバター)。"));
                return issues;
            }

            var broken = new List<string>();
            for (int i = 0; i < rs.Length; i++)
            {
                Material[] mats = rs[i].sharedMaterials;
                bool bad = false;
                for (int k = 0; k < mats.Length && !bad; k++)
                {
                    if (mats[k] == null) bad = true;
                    else if (mats[k].shader == null || !mats[k].shader.isSupported
                             || mats[k].shader.name.IndexOf("InternalErrorShader", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        bad = true;
                }
                if (bad) broken.Add(rs[i].name);
            }
            if (broken.Count > 0)
            {
                issues.Add(new CheckIssue(1, "マテリアル/シェーダーに問題がある可能性(ピンクになる): "
                    + string.Join(", ", broken.Take(3).ToArray()) + (broken.Count > 3 ? " ほか" + (broken.Count - 3) + "件" : "")));
            }

            Camera cam = _pru.camera;
            PlaceCamera(rig);
            cam.aspect = aspect;

            // バウンディングボックス8頂点の投影
            Bounds b = Bounds;
            float minX = 1e9f, maxX = -1e9f, minY = 1e9f, maxY = -1e9f;
            bool behind = false;
            for (int i = 0; i < 8; i++)
            {
                Vector3 p = b.center + new Vector3(
                    (i & 1) == 0 ? -b.extents.x : b.extents.x,
                    (i & 2) == 0 ? -b.extents.y : b.extents.y,
                    (i & 4) == 0 ? -b.extents.z : b.extents.z);
                Vector3 v = cam.WorldToViewportPoint(p);
                if (v.z <= 0f) behind = true;
                minX = Mathf.Min(minX, v.x); maxX = Mathf.Max(maxX, v.x);
                minY = Mathf.Min(minY, v.y); maxY = Mathf.Max(maxY, v.y);
            }

            if (behind)
            {
                issues.Add(new CheckIssue(2, "カメラがアバターの内側/後ろにあります。位置を調整してください。"));
            }
            else
            {
                if (framing == Framing.Full || framing == Framing.Custom)
                {
                    if (minX < -0.02f || maxX > 1.02f || minY < -0.02f || maxY > 1.02f)
                        issues.Add(new CheckIssue(1, "アバターが画角からはみ出しています。"));
                    else if (maxY - minY < 0.35f && maxX - minX < 0.35f)
                        issues.Add(new CheckIssue(0, "アバターが小さく写っています。"));
                }
                Transform head = (_animator != null && _animator.isHuman) ? _animator.GetBoneTransform(HumanBodyBones.Head) : null;
                if (head != null)
                {
                    Vector3 hv = cam.WorldToViewportPoint(head.position);
                    if (hv.z <= 0f || hv.x < 0.03f || hv.x > 0.97f || hv.y < 0.03f || hv.y > 0.97f)
                        issues.Add(new CheckIssue(1, "頭が画角に入っていません。"));
                }
            }
            return issues;
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

    #region Assets helper

    internal static class ThumbAssets
    {
        public static AnimationClip LoadClip(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) return null;
            return AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
        }

        public static string GuidOf(Object obj)
        {
            if (obj == null) return "";
            string path = AssetDatabase.GetAssetPath(obj);
            return string.IsNullOrEmpty(path) ? "" : AssetDatabase.AssetPathToGUID(path);
        }
    }

    #endregion
}
#endif
