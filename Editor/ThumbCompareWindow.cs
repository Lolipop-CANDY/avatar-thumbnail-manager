#if UNITY_EDITOR
// ============================================================================
//  撮影結果の比較ウィンドウ(以前の画像 ⇔ 現在の画像、復元)
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LollipopCandy.AvatarHub
{
    internal sealed class ThumbCompareWindow : EditorWindow
    {
        private string _currentPath = "";
        private string _title = "";
        private List<string> _history = new List<string>();
        private int _index;
        private Texture2D _cur;
        private Texture2D _old;
        private Action<string> _onRestored;
        private bool _afterCapture;

        public static void Open(string title, string currentPath, Action<string> onRestored, bool afterCapture)
        {
            var w = GetWindow<ThumbCompareWindow>(true, "撮影結果の比較", true);
            w.minSize = new Vector2(560f, 420f);
            w._title = title;
            w._currentPath = currentPath;
            w._onRestored = onRestored;
            w._afterCapture = afterCapture;
            w._index = 0;
            w.Reload();
            w.Show();
            w.Focus();
        }

        private void OnDisable()
        {
            Dispose(ref _cur);
            Dispose(ref _old);
        }

        private static void Dispose(ref Texture2D t)
        {
            if (t != null) DestroyImmediate(t);
            t = null;
        }

        private static Texture2D LoadFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var t = new Texture2D(2, 2, TextureFormat.RGB24, false);
                t.hideFlags = HideFlags.HideAndDontSave;
                return t.LoadImage(File.ReadAllBytes(path)) ? t : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void Reload()
        {
            _history = ThumbHistory.List(_currentPath);
            _index = Mathf.Clamp(_index, 0, Mathf.Max(0, _history.Count - 1));
            Dispose(ref _cur);
            Dispose(ref _old);
            _cur = LoadFile(_currentPath);
            if (_history.Count > 0) _old = LoadFile(_history[_index]);
            Repaint();
        }

        private static string Label(string path)
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            int us = stem.LastIndexOf('_');
            string tail = us >= 0 ? stem.Substring(us + 1) : stem;
            // 20261017-051122 → 2026/10/17 05:11:22
            if (tail.Length >= 15 && tail[8] == '-')
            {
                return string.Format("{0}/{1}/{2} {3}:{4}:{5}", tail.Substring(0, 4), tail.Substring(4, 2), tail.Substring(6, 2),
                    tail.Substring(9, 2), tail.Substring(11, 2), tail.Substring(13, 2));
            }
            return tail;
        }

        private void OnGUI()
        {
            Ui.Ensure();
            GUILayout.Space(6f);
            using (Ui.Card(_title, _afterCapture ? "撮影しました" : "比較"))
            {
                if (_history.Count == 0)
                {
                    GUILayout.Label("比較できる以前の画像がありません(再撮影すると履歴が保存されます)。", Ui.WrapMuted);
                }
                else
                {
                    var names = new string[_history.Count];
                    for (int i = 0; i < names.Length; i++) names[i] = (i + 1) + ". " + Label(_history[i]);
                    EditorGUI.BeginChangeCheck();
                    int idx = EditorGUILayout.Popup("以前の画像", _index, names);
                    if (EditorGUI.EndChangeCheck())
                    {
                        _index = idx;
                        Dispose(ref _old);
                        _old = LoadFile(_history[_index]);
                    }
                }
            }

            float avail = position.width - 36f;
            float size = Mathf.Clamp(avail * 0.5f - 6f, 120f, 420f);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                DrawImage("以前", _old, size, _history.Count > 0 ? Label(_history[_index]) : "");
                GUILayout.Space(8f);
                DrawImage("現在", _cur, size, "今の画像");
                GUILayout.FlexibleSpace();
            }

            GUILayout.FlexibleSpace();
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_history.Count == 0 || _old == null))
                {
                    if (Ui.Button("以前の画像に戻す", false, 150f, 28f)) Restore();
                }
                GUILayout.FlexibleSpace();
                if (Ui.Button(_afterCapture ? "この画像を採用" : "閉じる", true, 130f, 28f)) Close();
            }
            GUILayout.Space(8f);
        }

        private static void DrawImage(string label, Texture2D tex, float size, string sub)
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(size)))
            {
                GUILayout.Label(label, Ui.BigLabel);
                Rect r = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(false), GUILayout.ExpandHeight(false));
                EditorGUI.DrawRect(r, new Color(0f, 0f, 0f, 0.35f));
                if (tex != null) GUI.DrawTexture(r, tex, ScaleMode.ScaleToFit, false);
                GUILayout.Label(sub, Ui.Muted);
            }
        }

        private void Restore()
        {
            if (_history.Count == 0) return;
            string oldPath = _history[_index];
            if (!EditorUtility.DisplayDialog("以前の画像に戻す",
                "現在の画像を履歴に残したうえで、選んだ以前の画像に戻します。", "戻す", "キャンセル")) return;

            try
            {
                ThumbHistory.Archive(_currentPath); // 現在の画像も履歴へ(元に戻せるように)
                string target = ThumbStore.WithExtension(_currentPath, Path.GetExtension(oldPath));
                File.Copy(oldPath, target, true);
                if (!string.Equals(target, _currentPath, StringComparison.OrdinalIgnoreCase) && File.Exists(_currentPath))
                {
                    AssetDatabase.DeleteAsset(_currentPath);
                }
                AssetDatabase.ImportAsset(target);
                _currentPath = target;
                if (_onRestored != null) _onRestored(target);
                _afterCapture = false;
                _index = 0;
                Reload();
            }
            catch (Exception e)
            {
                Debug.LogError("[AvatarThumbnail] 画像の復元に失敗: " + e);
                EditorUtility.DisplayDialog("復元に失敗", e.Message, "OK");
            }
        }
    }
}
#endif
