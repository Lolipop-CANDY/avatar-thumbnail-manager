#if UNITY_EDITOR
// ============================================================================
//  「全シーン」タブ / 「設定」タブ
// ============================================================================
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LollipopCandy.AvatarHub
{
    public partial class AvatarThumbnailManager
    {
        private readonly HashSet<string> _batchSel = new HashSet<string>();

        // ---------------------------------------------------------------- 全シーン

        private static int CountThumbs(ProjectRecord p)
        {
            if (p.avatars == null) return 0;
            int n = 0;
            foreach (AvatarRecord a in p.avatars)
            {
                if (string.IsNullOrEmpty(a.thumbnailPath)) continue;
                if (File.Exists(a.thumbnailPath) ||
                    File.Exists(ThumbStore.WithExtension(a.thumbnailPath, Path.GetExtension(a.thumbnailPath).ToLowerInvariant() == ".png" ? ".jpg" : ".png")))
                {
                    n++;
                }
            }
            return n;
        }

        private void DrawScenesTab()
        {
            using (Ui.Card("全シーン", _projects.Count + " 件"))
            {
                GUILayout.Label("このツールで一度開いたシーン(またはここで追加したシーン)の一覧です。" +
                                "チェックしたシーンを順に開いて、まとめて撮影できます。", Ui.WrapMuted);

                if (_projects.Count == 0)
                {
                    GUILayout.Label("まだシーンがありません。", Ui.Muted);
                }

                foreach (ProjectRecord p in _projects)
                {
                    bool current = p.projectName == _sceneKey;
                    int n = p.avatars != null ? p.avatars.Count : 0;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        bool sel = _batchSel.Contains(p.projectName);
                        bool ns = EditorGUILayout.Toggle(sel, GUILayout.Width(18f));
                        if (ns != sel)
                        {
                            if (ns) _batchSel.Add(p.projectName);
                            else _batchSel.Remove(p.projectName);
                        }
                        GUILayout.Label(p.projectName + (current ? "  (現在)" : ""), Ui.Row);
                        GUILayout.FlexibleSpace();
                        GUILayout.Label(CountThumbs(p) + " / " + n + " 撮影済", Ui.Muted);
                        using (new EditorGUI.DisabledScope(current || string.IsNullOrEmpty(p.scenePath)))
                        {
                            if (Ui.Button("開く", false, 50f, 20f))
                            {
                                string path = p.scenePath;
                                Defer(() =>
                                {
                                    if (UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                                    {
                                        UnityEditor.SceneManagement.EditorSceneManager.OpenScene(path);
                                    }
                                });
                            }
                        }
                    }
                    if (string.IsNullOrEmpty(p.scenePath))
                    {
                        GUILayout.Label("   シーンファイルの場所が未記録です(そのシーンを一度開くと記録されます)", Ui.Muted);
                    }
                }

                GUILayout.Space(4f);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (Ui.Button("シーンを追加…", false, 0f, 26f)) Defer(AddSceneFile);
                    int selCount = _projects.Count(p => _batchSel.Contains(p.projectName));
                    using (new EditorGUI.DisabledScope(selCount == 0))
                    {
                        if (Ui.Button("選択したシーンを一括撮影 (" + selCount + ")", true, 0f, 26f))
                        {
                            List<ProjectRecord> list = _projects.Where(p => _batchSel.Contains(p.projectName)).ToList();
                            Defer(() => RunBatchScenes(list));
                        }
                    }
                }
            }
        }

        private void AddSceneFile()
        {
            string abs = EditorUtility.OpenFilePanel("追加するシーンを選択", Application.dataPath, "unity");
            if (string.IsNullOrEmpty(abs)) return;
            string proj = Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/');
            abs = abs.Replace('\\', '/');
            if (!abs.StartsWith(proj + "/", System.StringComparison.OrdinalIgnoreCase))
            {
                EditorUtility.DisplayDialog("シーンを追加", "このプロジェクト内のシーンを選んでください。", "OK");
                return;
            }
            string rel = abs.Substring(proj.Length + 1);
            string key = Path.GetFileNameWithoutExtension(rel);

            DatabaseRecord db = ThumbStore.ReadAll();
            if (db.projects.Any(p => p.projectName == key))
            {
                EditorUtility.DisplayDialog("シーンを追加", "同名のシーン「" + key + "」は既に登録されています。", "OK");
                return;
            }
            if (_saveEntriesAt >= 0) SaveEntries();
            ThumbStore.Save(key, rel, new List<AvatarEntry>());
            RefreshProjects();
        }

        // ---------------------------------------------------------------- 設定

        private struct SizePreset
        {
            public string label;
            public int w, h;
            public SizePreset(string l, int w, int h) { label = l; this.w = w; this.h = h; }
        }

        private static readonly SizePreset[] SizePresets =
        {
            new SizePreset("1024²", 1024, 1024),
            new SizePreset("512²", 512, 512),
            new SizePreset("2048²", 2048, 2048),
            new SizePreset("VRChat 4:3", 1200, 900),
            new SizePreset("16:9", 1280, 720),
            new SizePreset("3:4 縦", 900, 1200),
        };

        private int CloneSig()
        {
            unchecked
            {
                int h = s.captureMode * 31 + (s.hideBoneProxy ? 1 : 0) * 7 + (s.hideEditorOnly ? 1 : 0) * 13;
                h = h * 31 + (s.hideNames == null ? 0 : s.hideNames.GetHashCode());
                return h;
            }
        }

        private void DrawSettingsTab()
        {
            int cloneBefore = CloneSig();

            using (Ui.Card("出力"))
            {
                GUILayout.Label("サイズ", Ui.Muted);
                using (new EditorGUILayout.HorizontalScope())
                {
                    foreach (SizePreset p in SizePresets)
                    {
                        bool on = s.outW == p.w && s.outH == p.h;
                        if (Ui.Pill(p.label, on) && !on)
                        {
                            RecordUndo("出力サイズ");
                            s.outW = p.w;
                            s.outH = p.h;
                        }
                    }
                }
                EditorGUI.BeginChangeCheck();
                using (new EditorGUILayout.HorizontalScope())
                {
                    int w = EditorGUILayout.IntField("カスタム 幅×高さ", s.outW);
                    int h = EditorGUILayout.IntField(s.outH, GUILayout.Width(70f));
                    if (EditorGUI.EndChangeCheck())
                    {
                        RecordUndo("出力サイズ");
                        s.outW = Mathf.Clamp(w, 64, 4096);
                        s.outH = Mathf.Clamp(h, 64, 4096);
                    }
                }

                GUILayout.Label("形式", Ui.Muted);
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool png = s.Format == OutFormat.Png;
                    if (Ui.Pill("PNG", png) && !png) { RecordUndo("形式"); s.outFormat = (int)OutFormat.Png; }
                    bool jpg = s.Format == OutFormat.Jpg;
                    if (Ui.Pill("JPG", jpg) && !jpg) { RecordUndo("形式"); s.outFormat = (int)OutFormat.Jpg; }
                    GUILayout.FlexibleSpace();
                }
                if (s.Format == OutFormat.Jpg)
                {
                    EditorGUI.BeginChangeCheck();
                    int q = EditorGUILayout.IntSlider("JPG品質", s.jpgQuality, 40, 100);
                    if (EditorGUI.EndChangeCheck()) { RecordUndo("JPG品質"); s.jpgQuality = q; }
                }
                GUILayout.Label("形式を変えた後に再撮影すると、新しい形式で保存し直します(元の画像は履歴に残ります)。", Ui.WrapMuted);
            }

            using (Ui.Card("撮影方式"))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool exact = s.Mode == CaptureMode.SceneExact;
                    if (Ui.Pill("シーンの見た目そのまま", exact, 0f) && !exact) { RecordUndo("撮影方式"); s.captureMode = (int)CaptureMode.SceneExact; }
                    bool ndmf = s.Mode == CaptureMode.NdmfBuild;
                    if (Ui.Pill("NDMFビルド結果", ndmf, 0f) && !ndmf) { RecordUndo("撮影方式"); s.captureMode = (int)CaptureMode.NdmfBuild; }
                }
                GUILayout.Label(s.Mode == CaptureMode.SceneExact
                    ? "ヒエラルキーで表示になっているものだけを、そのまま撮影します。シーンで表示/非表示を切り替えると、プレビューにも反映されます。"
                    : "NDMF(Modular Avatar等)のビルド処理を通した結果で撮影します。MAの既定状態が適用されます。NDMF未導入や失敗時はシーンの見た目で撮影します。",
                    Ui.WrapMuted);

                if (s.Mode == CaptureMode.SceneExact)
                {
                    GUILayout.Space(2f);
                    GUILayout.Label("撮影から外すもの", Ui.Muted);
                    UndoToggle("MA Bone Proxy を隠す", ref s.hideBoneProxy);
                    UndoToggle("EditorOnly タグを隠す", ref s.hideEditorOnly);
                    EditorGUI.BeginChangeCheck();
                    string names = EditorGUILayout.DelayedTextField("名前に含む(, 区切り)", s.hideNames);
                    if (EditorGUI.EndChangeCheck()) { RecordUndo("非表示ルール"); s.hideNames = names; }
                }
            }

            using (Ui.Card("撮影オプション"))
            {
                UndoToggle("自動高さ合わせ", ref s.autoHeight);
                UndoToggle("撮影済みをスキップ", ref s.skipExisting);
                UndoToggle("再撮影警告文", ref s.showRetakeWarning);
                UndoToggle("アバターリストを上書きする", ref s.overwriteList);
                UndoToggle("アバターごとのカメラを使う", ref s.perAvatarCamera);
                UndoToggle("撮影後に前の画像と比較する", ref s.compareAfterCapture);
                UndoToggle("他シーンの同じアバターの画像を流用する", ref s.reuseSharedThumb);
            }

            using (Ui.Card("プレビュー"))
            {
                UndoToggle("シーンの変更をリアルタイムで反映", ref s.liveSync);
                UndoToggle("SDKアイコンの切り出しガイドを表示", ref s.showCropGuide);
                EditorGUI.BeginChangeCheck();
                int dim = EditorGUILayout.IntSlider("プレビュー解像度", s.previewDim, 192, 768);
                if (EditorGUI.EndChangeCheck()) { RecordUndo("プレビュー解像度"); s.previewDim = dim; }
                GUILayout.Label("小さいほど軽く動きます。プレビューは変更があった時だけ再描画します。", Ui.WrapMuted);
            }

            if (CloneSig() != cloneBefore && _session != null) _needsRebuild = true;
        }
    }
}
#endif
