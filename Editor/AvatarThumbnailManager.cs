#if UNITY_EDITOR
// ============================================================================
//  VRChatアバターサムネイル撮影管理ツール  v3.0
//
//   撮影: シーン上の「いまの見た目」を隔離プレビューシーンへ複製して撮る(元シーンは触らない)。
//   同一性: GlobalObjectId / Blueprint ID(名前を変えても別アバター扱いにならない)。
//   プレビュー: 必要な時だけ低解像度で再描画する(変更検知 + 間引き)。見えていない時は何もしない。
//   構成: ThumbModels(設定・DB) / ThumbRender(撮影エンジン) / ThumbTheme(UI) /
//         ThumbCompareWindow(比較) / ThumbSdkIcon(SDKアイコン) / このファイル(ウィンドウ)
// ============================================================================
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LollipopCandy.AvatarHub
{
    public partial class AvatarThumbnailManager : EditorWindow
    {
        private const double RenderInterval = 0.045;   // プレビュー再描画の最小間隔(約22fps上限)
        private const double SyncInterval = 0.25;      // シーン状態のポーリング間隔
        private const double HeartbeatInterval = 1.0;  // 外部変更検出用の生存確認
        private const float RowHeight = 82f;
        private const float GroupHeaderHeight = 28f;

        private enum TabId { Capture = 0, List = 1, Scenes = 2, Settings = 3 }

        [SerializeField] private ThumbSettings s = new ThumbSettings();
        [SerializeField] private int _tab;

        // データ
        private readonly List<AvatarEntry> _entries = new List<AvatarEntry>();
        private string _sceneKey = "";
        private string _scenePath = "";
        private List<ProjectRecord> _projects = new List<ProjectRecord>();
        private readonly Dictionary<string, string> _sharedThumbs = new Dictionary<string, string>();

        // UI状態
        private readonly Vector2[] _scrolls = new Vector2[4];
        private int _undoGroup = -1;

        // プレビュー
        private RenderSession _session;
        private AvatarEntry _previewEntry;
        private Texture2D _previewTex;
        private List<CheckIssue> _issues = new List<CheckIssue>();
        private bool _previewDirty;
        private bool _issuesDirty;
        private bool _needsRebuild;
        private bool _lookDirty;
        private int _lastSig;
        private int _pickIndex;
        private double _lastGui;
        private double _lastBeat;
        private double _lastSync;
        private double _lastRender;
        private double _saveEntriesAt = -1;
        private double _saveSettingsAt = -1;

        [MenuItem("ろりぽっぷCANDY/あばたーはぶリスト作成ツール")]
        public static void ShowWindow()
        {
            AvatarThumbnailManager window = GetWindow<AvatarThumbnailManager>("あばたーはぶリスト作成");
            window.minSize = new Vector2(520f, 640f);
            window.Show();
        }

        // ---- ライフサイクル ----

        private void OnEnable()
        {
            if (s == null || !s.initialized) s = ThumbSettings.Load();
            s.Sanitize();

            Undo.undoRedoPerformed += OnUndoRedo;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;
            EditorApplication.update += Tick;

            _lastGui = EditorApplication.timeSinceStartup;
            ReloadForScene();
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
            EditorSceneManager.sceneOpened -= OnSceneOpened;
            EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChanged;
            EditorApplication.update -= Tick;

            CloseSession(); // 個別カメラ表示中なら共通カメラ値を戻してから保存する
            if (s != null) s.Save();
            if (_saveEntriesAt >= 0) SaveEntries();
            foreach (AvatarEntry e in _entries)
            {
                if (e.thumbTex != null) DestroyImmediate(e.thumbTex);
                e.thumbTex = null;
            }
            if (_previewTex != null) DestroyImmediate(_previewTex);
            _previewTex = null;
        }

        private void OnFocus()
        {
            RefreshThumbFlags();
            RefreshProjects();
            Repaint();
        }

        private void OnHierarchyChange()
        {
            bool renamed = false;
            foreach (AvatarEntry e in _entries)
            {
                if (e.sceneObject != null && e.name != e.sceneObject.name)
                {
                    e.name = e.sceneObject.name; // 表示名が変わった。画像名は次の撮影/リスト更新で追従
                    renamed = true;
                }
            }
            if (renamed)
            {
                MarkDuplicates();
                _viewDirty = true;
                MarkEntriesDirty();
            }
            Repaint();
        }

        private void OnUndoRedo() { Repaint(); }

        private void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            if (!_batchBusy) ReloadForScene();
        }

        private void OnActiveSceneChanged(Scene previous, Scene next)
        {
            if (!_batchBusy) ReloadForScene();
        }

        // ---- 定期処理(リアルタイム同期・遅延保存) ----

        private void Tick()
        {
            double now = EditorApplication.timeSinceStartup;

            // 遅延保存(スライダー操作中に毎回ディスクへ書かない)
            if (_saveEntriesAt >= 0 && now >= _saveEntriesAt) SaveEntries();
            if (_saveSettingsAt >= 0 && now >= _saveSettingsAt)
            {
                _saveSettingsAt = -1;
                s.Save();
            }

            // ウィンドウが見えていない間(OnGUIが来ない間)は何もしない
            if (now - _lastGui > 2.5) return;

            // 生存確認: 1秒に1回だけ再描画して「見えている」ことを更新する(外部変更の検出用)
            if (now - _lastBeat > HeartbeatInterval)
            {
                _lastBeat = now;
                Repaint();
            }

            if (_session == null || _tab != (int)TabId.Capture) return;
            if (EditorApplication.isCompiling || EditorApplication.isPlaying) return;

            try
            {
                if (_needsRebuild)
                {
                    _needsRebuild = false;
                    AvatarEntry target = _previewEntry;
                    CloseSession(true);
                    OpenSession(target, false);
                    return;
                }

                if (_lookDirty)
                {
                    _lookDirty = false;
                    _session.ApplyLook(_previewEntry.look);
                    _previewDirty = true;
                    _issuesDirty = true;
                }

                if (s.liveSync && _session.SupportsLiveSync && now - _lastSync > SyncInterval)
                {
                    _lastSync = now;
                    if (_session.SyncFromScene())
                    {
                        _previewDirty = true;
                        _issuesDirty = true;
                    }
                }

                int sig = Signature();
                if (sig != _lastSig)
                {
                    _lastSig = sig;
                    _previewDirty = true;
                    _issuesDirty = true;
                }

                if (_previewDirty && now - _lastRender > RenderInterval)
                {
                    _lastRender = now;
                    _previewDirty = false;
                    RenderPreview();
                    if (_issuesDirty)
                    {
                        _issuesDirty = false;
                        _issues = _session.RunChecks(CurrentRig(), s.Aspect, s.FramingValue);
                    }
                    Repaint();
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[AvatarThumbnail] プレビュー更新に失敗: " + ex);
                CloseSession();
            }
        }

        /// <summary>プレビューに影響する設定の署名。変わった時だけ再描画する。</summary>
        private int Signature()
        {
            unchecked
            {
                int h = CurrentRig().Hash();
                h = h * 31 + EffectiveBg(_previewEntry).Hash();
                h = h * 31 + s.outW * 7 + s.outH;
                h = h * 31 + s.previewDim;
                h = h * 31 + (s.liveSync ? 1 : 0);
                return h;
            }
        }

        private CamRig CurrentRig()
        {
            return new CamRig(s.camPos, s.camRot, s.autoHeight);
        }

        private BackgroundSpec EffectiveBg(AvatarEntry e)
        {
            return (e != null && e.look != null && e.look.bgOverride) ? e.look.bg : s.bg;
        }

        private void RenderPreview()
        {
            float aspect = s.Aspect;
            int w, h;
            if (aspect >= 1f)
            {
                w = s.previewDim;
                h = Mathf.Max(64, Mathf.RoundToInt(w / aspect));
            }
            else
            {
                h = s.previewDim;
                w = Mathf.Max(64, Mathf.RoundToInt(h * aspect));
            }

            ResolvedBg bg = BgResolver.Resolve(EffectiveBg(_previewEntry), _session, s.Format, true);
            Texture2D tex = _session.RenderComposite(w, h, CurrentRig(), bg, _previewTex);
            if (tex != null) _previewTex = tex;
        }

        private void MarkEntriesDirty()
        {
            _saveEntriesAt = EditorApplication.timeSinceStartup + 0.6;
        }

        private void MarkSettingsDirty()
        {
            _saveSettingsAt = EditorApplication.timeSinceStartup + 0.6;
        }

        // ---- GUI 枠組み ----

        private void OnGUI()
        {
            _lastGui = EditorApplication.timeSinceStartup;
            Ui.Ensure();

            int captured = 0;
            for (int i = 0; i < _entries.Count; i++) if (_entries[i].hasThumb) captured++;
            Ui.Header(_sceneKey, _entries.Count, captured);
            DrawTabs();

            _scrolls[_tab] = EditorGUILayout.BeginScrollView(_scrolls[_tab]);
            GUILayout.Space(4f);
            switch ((TabId)_tab)
            {
                case TabId.Capture: DrawCaptureTab(); break;
                case TabId.List: DrawListTab(); break;
                case TabId.Scenes: DrawScenesTab(); break;
                default: DrawSettingsTab(); break;
            }
            GUILayout.Space(8f);
            EditorGUILayout.EndScrollView();

            DrawFooter();

            CollapseUndoOnMouseUp();
            if (GUI.changed) MarkSettingsDirty();
        }

        private void DrawTabs()
        {
            string[] names = { "撮影", "リスト", "全シーン", "設定" };
            GUILayout.Space(6f);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(8f);
                for (int i = 0; i < names.Length; i++)
                {
                    bool on = Ui.Pill(names[i], _tab == i, 0f);
                    if (on && _tab != i)
                    {
                        _tab = i;
                        if (i == (int)TabId.Scenes) RefreshProjects();
                        GUIUtility.keyboardControl = 0;
                    }
                }
                GUILayout.FlexibleSpace();
                GUILayout.Space(8f);
            }
            GUILayout.Space(2f);
        }

        private void DrawFooter()
        {
            using (Ui.Card(null))
            {
                int sel = _entries.Count(e => e.selected && !e.IsMissing);
                GUILayout.Label(string.Format("選択中 {0} 体 ・ 出力 {1}×{2} {3}", sel, s.outW, s.outH,
                    s.Format == OutFormat.Png ? "PNG" : "JPG"), Ui.Muted);
                if (Ui.Button("選択アバターのみ撮影", true, 0f, 32f))
                {
                    Defer(CaptureSelected);
                }
                GUILayout.Space(2f);
                if (Ui.Button("保存フォルダを開く", false, 0f, 24f))
                {
                    Defer(OpenSaveFolder);
                }
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

        /// <summary>ピル型のトグルをUndo付きで。変更があれば true。</summary>
        private bool UndoPill(string label, ref bool value, float width)
        {
            bool v = Ui.Pill(label, value, width);
            if (v == value) return false;
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
    }
}
#endif
