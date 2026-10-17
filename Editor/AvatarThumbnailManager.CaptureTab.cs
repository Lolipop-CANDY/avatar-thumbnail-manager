#if UNITY_EDITOR
// ============================================================================
//  「撮影」タブ: プレビュー / チェック / カメラ / 背景 / ポーズ・表情
// ============================================================================
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LollipopCandy.AvatarHub
{
    public partial class AvatarThumbnailManager
    {
        private string _presetName = "";

        private void DrawCaptureTab()
        {
            DrawPreviewCard();
            if (_session != null) DrawIssuesCard();
            DrawCameraCard();
            DrawBackgroundCard();
            if (_session != null && _previewEntry != null) DrawVisibilityCard();
            if (_session != null && _previewEntry != null) DrawLookCard();
        }

        // ---- 表示の調整(小物の非表示 / 髪などの強制表示) ----

        private void AddPathMenu(GenericMenu menu, string prefix, List<string> target, string path, bool sceneActive, bool isHide)
        {
            string label = prefix + path + (sceneActive ? "" : "  [シーンで非表示]");
            bool already = target.Contains(path);
            AvatarLook look = _previewEntry.look;
            string p = path;
            menu.AddItem(new GUIContent(label), already, () =>
            {
                if (target.Contains(p)) target.Remove(p);
                else
                {
                    target.Add(p);
                    (isHide ? look.showPaths : look.hidePaths).Remove(p); // 隠す/表示は同時にしない
                }
                LookChanged();
            });
        }

        private void DrawVisibilityCard()
        {
            if (!_session.SupportsLiveSync) return;
            AvatarLook look = _previewEntry.look;
            string right = look.HasVisibilityOverrides ? (look.hidePaths.Count + look.showPaths.Count) + "件設定中" : null;
            using (Ui.Card("表示の調整", right))
            {
                GUILayout.Label("このアバターだけ、撮影に写すもの・写さないものを選べます。" +
                                "小物が邪魔なら「隠す」、髪などが写らない時は「強制表示」を使ってください。", Ui.WrapMuted);

                List<KeyValuePair<string, bool>> objs = null;
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (Ui.Button("＋ 隠すものを選ぶ", false, 0f, 24f))
                    {
                        objs = _session.ListRendererObjects();
                        var menu = new GenericMenu();
                        foreach (var kv in objs) AddPathMenu(menu, "", look.hidePaths, kv.Key, kv.Value, true);
                        menu.ShowAsContext();
                    }
                    if (Ui.Button("＋ 強制表示するものを選ぶ", false, 0f, 24f))
                    {
                        objs = _session.ListRendererObjects();
                        var menu = new GenericMenu();
                        foreach (var kv in objs) AddPathMenu(menu, "", look.showPaths, kv.Key, kv.Value, false);
                        menu.ShowAsContext();
                    }
                }

                DrawPathList("隠す", look.hidePaths);
                DrawPathList("強制表示", look.showPaths);

                using (new EditorGUI.DisabledScope(!look.HasVisibilityOverrides))
                {
                    if (Ui.Button("表示の調整をリセット", false, 0f, 22f))
                    {
                        look.hidePaths.Clear();
                        look.showPaths.Clear();
                        LookChanged();
                    }
                }
            }
        }

        private void DrawPathList(string title, List<string> list)
        {
            int remove = -1;
            for (int i = 0; i < list.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(title + ": " + list[i], EditorStyles.miniLabel);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(22f))) remove = i;
                }
            }
            if (remove >= 0)
            {
                list.RemoveAt(remove);
                LookChanged();
            }
        }

        // ---- プレビュー ----

        private void DrawPreviewCard()
        {
            using (Ui.Card("プレビュー", _previewEntry != null ? _previewEntry.name : null))
            {
                List<AvatarEntry> avail = _entries.Where(e => !e.IsMissing).ToList();
                if (avail.Count == 0)
                {
                    GUILayout.Label("撮影できるアバターがありません。「リスト」タブで追加してください。", Ui.WrapMuted);
                    return;
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    string[] names = avail.Select(e => e.name).ToArray();
                    int cur = _previewEntry != null ? avail.IndexOf(_previewEntry) : _pickIndex;
                    cur = Mathf.Clamp(cur, 0, names.Length - 1);

                    EditorGUI.BeginChangeCheck();
                    int pick = EditorGUILayout.Popup(cur, names);
                    if (EditorGUI.EndChangeCheck())
                    {
                        _pickIndex = pick;
                        if (_session != null)
                        {
                            AvatarEntry t = avail[pick];
                            Defer(() => { CloseSession(); OpenSession(t, true); });
                        }
                    }
                    else
                    {
                        _pickIndex = cur;
                    }

                    if (_session == null)
                    {
                        if (Ui.Button("プレビュー開始", true, 110f, 20f))
                        {
                            AvatarEntry t = avail[_pickIndex];
                            Defer(() => OpenSession(t, true));
                        }
                    }
                    else if (Ui.Button("閉じる", false, 70f, 20f))
                    {
                        Defer(() => CloseSession());
                    }
                }

                if (_session == null)
                {
                    GUILayout.Label("アバターを選んで「プレビュー開始」。シーン上の表示状態がそのまま映り、" +
                                    "シーン側で表示/非表示を切り替えると自動で更新されます。", Ui.WrapMuted);
                    return;
                }

                float aspect = s.Aspect;
                float w = Mathf.Min(position.width - 64f, 520f);
                float h = w / aspect;
                if (h > 440f)
                {
                    h = 440f;
                    w = h * aspect;
                }

                Rect r;
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    r = GUILayoutUtility.GetRect(w, h, GUILayout.ExpandWidth(false), GUILayout.ExpandHeight(false));
                    GUILayout.FlexibleSpace();
                }
                DrawPreviewSurface(r);

                GUILayout.Space(2f);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (Ui.Button("再構築", false, 80f, 26f)) _needsRebuild = true;
                    if (Ui.Button("このアバターを撮影", true, 0f, 26f)) Defer(CaptureFromPreview);
                }

                string live = (s.liveSync && _session.SupportsLiveSync) ? " ・ リアルタイム同期 ON" : "";
                GUILayout.Label(_session.Description + live + "  (ドラッグで移動/回転・ホイールで前後)", Ui.Muted);
            }
        }

        private void DrawPreviewSurface(Rect r)
        {
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(r, new Color(0f, 0f, 0f, 0.35f));
                if (_previewTex != null) GUI.DrawTexture(r, _previewTex, ScaleMode.StretchToFill, false);
                if (s.showCropGuide) DrawCropGuide(r, s.Aspect);

                if (s.liveSync && _session != null && _session.SupportsLiveSync)
                {
                    Vector2 cs = Ui.ChipSize("LIVE");
                    Ui.Chip(new Rect(r.x + 6f, r.y + 6f, cs.x, cs.y), "LIVE", Ui.Mint);
                }
            }
            HandlePreviewInput(r);
        }

        /// <summary>SDKアイコンは4:3で中央切り出しされるため、切れる範囲を暗くして示す。</summary>
        private static void DrawCropGuide(Rect rect, float aspect)
        {
            Color shade = new Color(0f, 0f, 0f, 0.5f);
            float target = 4f / 3f;
            if (aspect < target)
            {
                float visible = aspect / target;            // 縦に残る割合
                float band = rect.height * (1f - visible) * 0.5f;
                EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, band), shade);
                EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - band, rect.width, band), shade);
            }
            else if (aspect > target + 0.01f)
            {
                float visible = target / aspect;            // 横に残る割合
                float band = rect.width * (1f - visible) * 0.5f;
                EditorGUI.DrawRect(new Rect(rect.x, rect.y, band, rect.height), shade);
                EditorGUI.DrawRect(new Rect(rect.xMax - band, rect.y, band, rect.height), shade);
            }
        }

        private void HandlePreviewInput(Rect r)
        {
            Event ev = Event.current;
            int id = GUIUtility.GetControlID(FocusType.Passive);
            switch (ev.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (ev.button == 0 && r.Contains(ev.mousePosition))
                    {
                        GUIUtility.hotControl = id;
                        RecordUndo("カメラ操作");
                        ev.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id)
                    {
                        DragCamera(ev.delta, r.height);
                        ev.Use();
                        GUI.changed = true;
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        GUIUtility.hotControl = 0;
                        ev.Use();
                    }
                    break;
                case EventType.ScrollWheel:
                    if (r.Contains(ev.mousePosition))
                    {
                        RecordUndo("カメラ前後");
                        Dolly(ev.delta.y);
                        ev.Use();
                        GUI.changed = true;
                    }
                    break;
            }
        }

        private void DragCamera(Vector2 delta, float rectHeight)
        {
            if (_session == null) return;
            if (!s.usePanYaw)
            {
                // 軸: 向きは変えず、画面の左右・上下へ平行移動
                float dist = _session.CameraDistance(CurrentRig());
                float k = 2f * dist * Mathf.Tan(_session.VerticalFovRad * 0.5f) / Mathf.Max(1f, rectHeight);
                Quaternion q = Quaternion.Euler(s.camRot.x, s.camRot.y, 0f);
                Vector3 right = q * Vector3.right;
                Vector3 up = q * Vector3.up;
                s.camPos = s.camPos + (right * (-delta.x) + up * delta.y) * k;
            }
            else
            {
                // 回転: その場で向きだけを変える
                s.camRot.y = Mathf.Repeat(s.camRot.y - delta.x * 0.2f, 360f);
                s.camRot.x = Mathf.Clamp(s.camRot.x - delta.y * 0.2f, -89f, 89f);
            }
            s.framing = (int)Framing.Custom;
        }

        private void Dolly(float scrollY)
        {
            if (_session == null) return;
            float dist = _session.CameraDistance(CurrentRig());
            Quaternion q = Quaternion.Euler(s.camRot.x, s.camRot.y, 0f);
            Vector3 fwd = q * Vector3.forward;
            s.camPos = s.camPos - fwd * (scrollY * 0.04f * dist);
            s.framing = (int)Framing.Custom;
        }

        // ---- チェック ----

        private void DrawIssuesCard()
        {
            if (_issues == null || _issues.Count == 0) return;
            using (Ui.Card("撮影前チェック", _issues.Count + "件"))
            {
                foreach (CheckIssue i in _issues)
                {
                    Color c = i.level >= 2 ? Ui.Danger : (i.level == 1 ? Ui.Warn : Ui.Sky);
                    Color old = GUI.contentColor;
                    GUI.contentColor = c;
                    GUILayout.Label("● " + i.text, EditorStyles.wordWrappedMiniLabel);
                    GUI.contentColor = old;
                }
            }
        }

        // ---- カメラ ----

        private static string FramingName(Framing f)
        {
            switch (f)
            {
                case Framing.Full: return "全身";
                case Framing.Bust: return "バストアップ";
                case Framing.Face: return "顔アップ";
                default: return "手動";
            }
        }

        private void SetFraming(Framing f)
        {
            RecordUndo("構図");
            s.framing = (int)f;
            s.usePanYaw = false;
            if (_session != null) ApplyFramingNow();
        }

        /// <summary>構図プリセットをカメラ値へ反映する(セッションがある時)。</summary>
        private void ApplyFramingNow()
        {
            if (_session == null || s.FramingValue == Framing.Custom) return;
            CamRig rig = _session.ComputeFraming(s.FramingValue, s.autoHeight, s.Aspect);
            s.camPos = rig.pos;
            s.camRot = rig.rot;
        }

        private void DrawCameraCard()
        {
            using (Ui.Card("カメラ", FramingName(s.FramingValue)))
            {
                GUILayout.Label("構図", Ui.Muted);
                using (new EditorGUILayout.HorizontalScope())
                {
                    Framing[] list = { Framing.Full, Framing.Bust, Framing.Face };
                    foreach (Framing f in list)
                    {
                        bool on = s.FramingValue == f;
                        if (Ui.Pill(FramingName(f), !on ? false : true) && !on) SetFraming(f);
                    }
                }

                GUILayout.Space(2f);
                GUILayout.Label("操作モード", Ui.Muted);
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool axis = Ui.Pill("軸(平行移動)", !s.usePanYaw, 110f);
                    if (axis && s.usePanYaw) { RecordUndo("カメラモード"); s.usePanYaw = false; }
                    bool rot = Ui.Pill("回転(その場)", s.usePanYaw, 110f);
                    if (rot && !s.usePanYaw) { RecordUndo("カメラモード"); s.usePanYaw = true; }
                    GUILayout.FlexibleSpace();
                }

                bool changed = false;
                if (!s.usePanYaw)
                {
                    changed |= UndoSlider("X (左右)", ref s.camPos.x, -10f, 10f);
                    changed |= UndoSlider("Y (上下)", ref s.camPos.y, -10f, 10f);
                    changed |= UndoSlider("Z (前後)", ref s.camPos.z, -20f, 20f);
                }
                else
                {
                    changed |= UndoSlider("パン (水平)", ref s.camRot.y, 0f, 360f);
                    changed |= UndoSlider("ヨー (垂直)", ref s.camRot.x, -89f, 89f);
                }
                if (changed) s.framing = (int)Framing.Custom;

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (Ui.Button("正面", false, 0f, 24f))
                    {
                        RecordUndo("正面");
                        float dz = Mathf.Max(0.3f, Mathf.Abs(s.camPos.z));
                        s.camPos = new Vector3(s.camPos.x, s.camPos.y, dz);
                        s.camRot = new Vector3(0f, 180f, 0f);
                        s.framing = (int)Framing.Custom;
                    }
                    if (Ui.Button("背面", false, 0f, 24f))
                    {
                        RecordUndo("背面");
                        float dz = Mathf.Max(0.3f, Mathf.Abs(s.camPos.z));
                        s.camPos = new Vector3(s.camPos.x, s.camPos.y, -dz);
                        s.camRot = new Vector3(0f, 0f, 0f);
                        s.framing = (int)Framing.Custom;
                    }
                }

                if (Ui.Button("カメラを初期設定に戻す", false, 0f, 24f))
                {
                    RecordUndo("カメラ初期化");
                    s.ResetCamera();
                }

                if (_previewEntry != null)
                {
                    GUILayout.Space(4f);
                    AvatarLook look = _previewEntry.look;
                    GUILayout.Label(look.hasCamera
                        ? "このアバターは個別カメラを保存済みです"
                        : "このアバターの個別カメラは未保存です", Ui.Muted);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (Ui.Button("このアバターのカメラを保存", false, 0f, 24f))
                        {
                            look.hasCamera = true;
                            look.camPos = s.camPos;
                            look.camRot = s.camRot;
                            look.usePanYaw = s.usePanYaw;
                            look.framing = s.framing;
                            _viewDirty = true;
                            MarkEntriesDirty();
                            ShowNotification(new GUIContent(_previewEntry.name + " のカメラを保存しました"));
                        }
                        using (new EditorGUI.DisabledScope(!look.hasCamera))
                        {
                            if (Ui.Button("保存を解除", false, 90f, 24f))
                            {
                                look.hasCamera = false;
                                _viewDirty = true;
                                MarkEntriesDirty();
                            }
                        }
                    }
                }
            }
        }

        // ---- 背景 ----

        private void BgEdit(bool isLook, string label)
        {
            if (isLook) MarkEntriesDirty();
            else RecordUndo(label);
        }

        private void DrawBackgroundCard()
        {
            AvatarLook look = _previewEntry != null ? _previewEntry.look : null;
            using (Ui.Card("背景", look != null && look.bgOverride ? "このアバター専用" : "全アバター共通"))
            {
                if (look != null)
                {
                    EditorGUI.BeginChangeCheck();
                    bool ov = EditorGUILayout.ToggleLeft("「" + _previewEntry.name + "」だけ別の背景にする", look.bgOverride);
                    if (EditorGUI.EndChangeCheck())
                    {
                        if (ov && !look.bgOverride) look.bg = s.bg.Clone();
                        look.bgOverride = ov;
                        MarkEntriesDirty();
                    }
                }

                bool isLook = look != null && look.bgOverride;
                BackgroundSpec spec = isLook ? look.bg : s.bg;

                string[] modeNames = { "自動", "単色", "グラデ", "透過", "画像" };
                using (new EditorGUILayout.HorizontalScope())
                {
                    for (int i = 0; i < modeNames.Length; i++)
                    {
                        bool on = (int)spec.mode == i;
                        if (Ui.Pill(modeNames[i], on) && !on)
                        {
                            BgEdit(isLook, "背景モード");
                            spec.mode = (BgMode)i;
                        }
                    }
                }

                switch (spec.mode)
                {
                    case BgMode.Auto:
                    {
                        EditorGUI.BeginChangeCheck();
                        int style = EditorGUILayout.Popup("配色", (int)spec.style, new[] { "同系色(パステル)", "補色(パステル)" });
                        if (EditorGUI.EndChangeCheck())
                        {
                            BgEdit(isLook, "背景配色");
                            spec.style = (BackgroundStyle)style;
                        }
                        if (_session != null && _session.GetStats().valid)
                        {
                            Color auto = BackgroundPalette.From(_session.GetStats(), spec.style);
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                EditorGUILayout.ColorField("自動で決まった色", auto);
                                if (Ui.Button("固定色にする", false, 90f, 18f))
                                {
                                    BgEdit(isLook, "背景を固定色化");
                                    spec.solid = auto;
                                    spec.mode = BgMode.Solid;
                                }
                            }
                        }
                        break;
                    }
                    case BgMode.Solid:
                    {
                        EditorGUI.BeginChangeCheck();
                        Color c = EditorGUILayout.ColorField("背景色", spec.solid);
                        if (EditorGUI.EndChangeCheck()) { BgEdit(isLook, "背景色"); spec.solid = c; }
                        break;
                    }
                    case BgMode.Gradient:
                    {
                        EditorGUI.BeginChangeCheck();
                        Color t = EditorGUILayout.ColorField("上の色", spec.top);
                        Color b = EditorGUILayout.ColorField("下の色", spec.bottom);
                        if (EditorGUI.EndChangeCheck())
                        {
                            BgEdit(isLook, "グラデーション");
                            spec.top = t;
                            spec.bottom = b;
                        }
                        break;
                    }
                    case BgMode.Transparent:
                        GUILayout.Label("PNG出力では背景が透過になります(JPG出力では白になります)。プレビューは市松模様で表示。", Ui.WrapMuted);
                        break;
                    case BgMode.Image:
                    {
                        Texture2D cur = string.IsNullOrEmpty(spec.imagePath) ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(spec.imagePath);
                        EditorGUI.BeginChangeCheck();
                        Texture2D tex = (Texture2D)EditorGUILayout.ObjectField("背景画像(PNG/JPG)", cur, typeof(Texture2D), false);
                        if (EditorGUI.EndChangeCheck())
                        {
                            BgEdit(isLook, "背景画像");
                            spec.imagePath = tex != null ? AssetDatabase.GetAssetPath(tex) : "";
                        }
                        GUILayout.Label("画像は画面全体を覆うように拡大して使います。", Ui.WrapMuted);
                        break;
                    }
                }
            }
        }

        // ---- ポーズ・表情 ----

        private void LookChanged()
        {
            _lookDirty = true;
            _viewDirty = true;
            MarkEntriesDirty();
        }

        private void DrawLookCard()
        {
            AvatarLook look = _previewEntry.look;
            using (Ui.Card("ポーズ・表情", _previewEntry.name + " 専用"))
            {
                AnimationClip clip = ThumbAssets.LoadClip(look.clipGuid);
                EditorGUI.BeginChangeCheck();
                AnimationClip nc = (AnimationClip)EditorGUILayout.ObjectField("ポーズ(クリップ)", clip, typeof(AnimationClip), false);
                if (EditorGUI.EndChangeCheck())
                {
                    look.clipGuid = ThumbAssets.GuidOf(nc);
                    look.clipTime = 0f;
                    LookChanged();
                }
                if (clip != null)
                {
                    EditorGUI.BeginChangeCheck();
                    float t = EditorGUILayout.Slider("再生位置(秒)", look.clipTime, 0f, Mathf.Max(0.01f, clip.length));
                    if (EditorGUI.EndChangeCheck())
                    {
                        look.clipTime = t;
                        LookChanged();
                    }
                }

                GUILayout.Space(4f);
                GUILayout.Label("表情(ブレンドシェイプ)", Ui.BigLabel);
                int remove = -1;
                for (int i = 0; i < look.blends.Count; i++)
                {
                    BlendOverride b = look.blends[i];
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUI.BeginChangeCheck();
                        float w = EditorGUILayout.Slider(b.shape, b.weight, 0f, 100f);
                        if (EditorGUI.EndChangeCheck())
                        {
                            b.weight = w;
                            LookChanged();
                        }
                        if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(22f))) remove = i;
                    }
                }
                if (remove >= 0)
                {
                    look.blends.RemoveAt(remove);
                    LookChanged();
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (Ui.Button("＋ 表情を追加", false, 110f, 22f))
                    {
                        var menu = new GenericMenu();
                        foreach (string n in _session.BlendShapeNames())
                        {
                            string name = n;
                            if (look.blends.Any(x => x.shape == name))
                            {
                                menu.AddDisabledItem(new GUIContent(name));
                            }
                            else
                            {
                                menu.AddItem(new GUIContent(name), false, () =>
                                {
                                    look.blends.Add(new BlendOverride { shape = name, weight = 100f });
                                    LookChanged();
                                });
                            }
                        }
                        menu.ShowAsContext();
                    }
                    if (Ui.Button("ポーズ・表情をリセット", false, 0f, 22f))
                    {
                        look.clipGuid = "";
                        look.clipTime = 0f;
                        look.blends.Clear();
                        LookChanged();
                    }
                }

                // 表情プリセット(ブレンドシェイプ名で適用するため、別アバターにも使い回せる)
                GUILayout.Space(4f);
                GUILayout.Label("表情プリセット", Ui.Muted);
                using (new EditorGUILayout.HorizontalScope())
                {
                    _presetName = EditorGUILayout.TextField(_presetName);
                    using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_presetName) || look.blends.Count == 0))
                    {
                        if (Ui.Button("保存", false, 50f, 20f))
                        {
                            RecordUndo("表情プリセット保存");
                            s.exprPresets.RemoveAll(p => p.name == _presetName.Trim());
                            s.exprPresets.Add(new ExprPreset
                            {
                                name = _presetName.Trim(),
                                blends = look.blends.Select(x => new BlendOverride { shape = x.shape, weight = x.weight }).ToList()
                            });
                        }
                    }
                }
                if (s.exprPresets.Count > 0)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        string[] pn = new[] { "(プリセットを選ぶ)" }.Concat(s.exprPresets.Select(p => p.name)).ToArray();
                        EditorGUI.BeginChangeCheck();
                        int pick = EditorGUILayout.Popup(0, pn);
                        if (EditorGUI.EndChangeCheck() && pick > 0)
                        {
                            ExprPreset p = s.exprPresets[pick - 1];
                            look.blends = p.blends.Select(x => new BlendOverride { shape = x.shape, weight = x.weight }).ToList();
                            LookChanged();
                        }
                        if (Ui.Button("プリセット削除…", false, 100f, 20f))
                        {
                            var menu = new GenericMenu();
                            foreach (ExprPreset p in s.exprPresets.ToList())
                            {
                                ExprPreset target = p;
                                menu.AddItem(new GUIContent(p.name), false, () =>
                                {
                                    RecordUndo("表情プリセット削除");
                                    s.exprPresets.Remove(target);
                                    Repaint();
                                });
                            }
                            menu.ShowAsContext();
                        }
                    }
                }

                GUILayout.Space(4f);
                using (new EditorGUI.DisabledScope(!look.HasPoseOrExpression))
                {
                    if (Ui.Button("このポーズ・表情を、選択中の他アバターにも適用", false, 0f, 24f))
                    {
                        int n = 0;
                        foreach (AvatarEntry e in _entries)
                        {
                            if (e == _previewEntry || !e.selected) continue;
                            e.look.CopyPoseFrom(look);
                            n++;
                        }
                        MarkEntriesDirty();
                        _viewDirty = true;
                        ShowNotification(new GUIContent(n + " 体に適用しました"));
                    }
                }
            }
        }
    }
}
#endif
