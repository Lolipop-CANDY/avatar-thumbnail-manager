#if UNITY_EDITOR
// ============================================================================
//  「リスト」タブ(検索 / 並べ替え / グループ / ドラッグ並べ替え / 仮想化)
// ============================================================================
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LollipopCandy.AvatarHub
{
    public partial class AvatarThumbnailManager
    {
        private sealed class ViewItem
        {
            public AvatarEntry e;     // 行
            public string header;     // グループ見出し(e == null)
            public int count;
        }

        private readonly List<ViewItem> _view = new List<ViewItem>();
        private string _search = "";
        private int _filter;   // 0 全て / 1 撮影済み / 2 未撮影 / 3 非表示 / 4 シーンに無い
        private int _sort;     // 0 リスト順 / 1 名前 / 2 撮影日時 / 3 タグ
        private bool _group;
        private int _dragFrom = -1;
        private int _dragTo = -1;

        private bool ReorderEnabled
        {
            get { return _sort == 0 && _filter == 0 && !_group && string.IsNullOrEmpty(_search); }
        }

        private void BuildView()
        {
            _viewDirty = false;
            _view.Clear();

            IEnumerable<AvatarEntry> q = _entries;
            if (!string.IsNullOrEmpty(_search))
            {
                string key = _search.Trim();
                q = q.Where(e => (e.name ?? "").IndexOf(key, System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 (e.tag ?? "").IndexOf(key, System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 (e.memo ?? "").IndexOf(key, System.StringComparison.OrdinalIgnoreCase) >= 0);
            }
            switch (_filter)
            {
                case 1: q = q.Where(e => e.hasThumb); break;
                case 2: q = q.Where(e => !e.hasThumb); break;
                case 3: q = q.Where(e => e.IsHidden); break;
                case 4: q = q.Where(e => e.IsMissing); break;
            }

            List<AvatarEntry> list = q.ToList();
            switch (_sort)
            {
                case 1: list = list.OrderBy(e => e.name, System.StringComparer.CurrentCultureIgnoreCase).ToList(); break;
                case 2: list = list.OrderByDescending(e => e.thumbTime).ToList(); break;
                case 3:
                    list = list.OrderBy(e => string.IsNullOrEmpty(e.tag) ? "￿" : e.tag, System.StringComparer.CurrentCultureIgnoreCase)
                               .ThenBy(e => e.name, System.StringComparer.CurrentCultureIgnoreCase).ToList();
                    break;
            }

            if (_group)
            {
                var order = new List<string>();
                var map = new Dictionary<string, List<AvatarEntry>>();
                foreach (AvatarEntry e in list)
                {
                    string g = string.IsNullOrEmpty(e.tag) ? "(タグなし)" : e.tag;
                    if (!map.ContainsKey(g))
                    {
                        map[g] = new List<AvatarEntry>();
                        order.Add(g);
                    }
                    map[g].Add(e);
                }
                foreach (string g in order)
                {
                    _view.Add(new ViewItem { header = g, count = map[g].Count });
                    foreach (AvatarEntry e in map[g]) _view.Add(new ViewItem { e = e });
                }
            }
            else
            {
                foreach (AvatarEntry e in list) _view.Add(new ViewItem { e = e });
            }
        }

        private void DrawListTab()
        {
            DrawListControls();
            DrawDropArea();

            if (_viewDirty) BuildView();

            using (Ui.Card("アバター", _view.Count(v => v.e != null) + " / " + _entries.Count))
            {
                if (_view.Count == 0)
                {
                    GUILayout.Label(_entries.Count == 0
                        ? "アバターがありません。「リストを更新」でシーンから読み込むか、上の枠へドラッグして追加してください。"
                        : "条件に一致するアバターがありません。", Ui.WrapMuted);
                }
                else
                {
                    if (!ReorderEnabled && _sort == 0 && !_group)
                    {
                        GUILayout.Label("検索・絞り込み中は並べ替えできません。", Ui.Muted);
                    }
                    else if (!ReorderEnabled)
                    {
                        GUILayout.Label("並べ替え表示中は ↑↓ とドラッグが使えません(「リスト順」で有効)。", Ui.Muted);
                    }
                    DrawRows();
                }
            }
        }

        private void DrawListControls()
        {
            using (Ui.Card("リスト操作"))
            {
                EditorGUI.BeginChangeCheck();
                _search = EditorGUILayout.TextField("検索", _search);
                using (new EditorGUILayout.HorizontalScope())
                {
                    _filter = EditorGUILayout.Popup(_filter, new[] { "すべて", "撮影済み", "未撮影", "非表示", "シーンに無い" }, GUILayout.Width(110f));
                    _sort = EditorGUILayout.Popup(_sort, new[] { "リスト順", "名前順", "撮影日時順", "タグ順" }, GUILayout.Width(110f));
                    _group = Ui.Pill("タグでグループ", _group, 0f);
                }
                if (EditorGUI.EndChangeCheck()) _viewDirty = true;

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (Ui.Button("全選択", false, 0f, 24f)) foreach (AvatarEntry e in _entries) e.selected = true;
                    if (Ui.Button("選択解除", false, 0f, 24f)) foreach (AvatarEntry e in _entries) e.selected = false;
                    if (Ui.Button("リストを更新", true, 0f, 24f)) Defer(UpdateList);
                }
                if (s.overwriteList)
                {
                    GUILayout.Label("「アバターリストを上書きする」ON: 更新時にシーンの状態でリストを作り直します(個別設定は引き継ぎ)。", Ui.WrapMuted);
                }
            }
        }

        private void DrawDropArea()
        {
            Rect r = GUILayoutUtility.GetRect(1f, 44f, GUILayout.ExpandWidth(true));
            r.x += 8f;
            r.width -= 16f;
            Event ev = Event.current;
            bool hover = r.Contains(ev.mousePosition) && DragAndDrop.objectReferences.Length > 0;
            if (ev.type == EventType.Repaint)
            {
                Color c = hover ? Ui.Accent : Ui.MutedText;
                c.a = hover ? 0.28f : 0.10f;
                EditorGUI.DrawRect(r, c);
                GUI.Label(r, "ここにシーンのアバターをドラッグして追加(条件チェックなし)", Ui.WrapMuted);
            }
            if ((ev.type == EventType.DragUpdated || ev.type == EventType.DragPerform) && r.Contains(ev.mousePosition))
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                if (ev.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    GameObject[] gos = DragAndDrop.objectReferences.OfType<GameObject>().ToArray();
                    Defer(() => { foreach (GameObject go in gos) AddManual(go); });
                }
                ev.Use();
            }
        }

        private void DrawRows()
        {
            float total = 0f;
            foreach (ViewItem v in _view) total += v.e == null ? GroupHeaderHeight : RowHeight;

            Rect area = GUILayoutUtility.GetRect(1f, total, GUILayout.ExpandWidth(true));
            Event ev = Event.current;
            if (ev.type == EventType.Layout) return;

            int dragId = GUIUtility.GetControlID(FocusType.Passive);
            float viewTop = _scrolls[(int)TabId.List].y - 40f;
            float viewBottom = viewTop + position.height + 80f;

            float y = area.y;
            for (int i = 0; i < _view.Count; i++)
            {
                ViewItem v = _view[i];
                float h = v.e == null ? GroupHeaderHeight : RowHeight;
                if (y + h >= viewTop && y <= viewBottom)
                {
                    Rect row = new Rect(area.x, y, area.width, h - 4f);
                    if (v.e == null) DrawGroupHeader(row, v);
                    else DrawRow(row, v.e, i, dragId);
                }
                y += h;
            }

            // ドラッグ並べ替え
            if (_dragFrom >= 0)
            {
                switch (ev.GetTypeForControl(dragId))
                {
                    case EventType.MouseDrag:
                        if (GUIUtility.hotControl == dragId)
                        {
                            _dragTo = Mathf.Clamp(Mathf.FloorToInt((ev.mousePosition.y - area.y) / RowHeight), 0, _view.Count - 1);
                            ev.Use();
                            Repaint();
                        }
                        break;
                    case EventType.MouseUp:
                        if (GUIUtility.hotControl == dragId)
                        {
                            GUIUtility.hotControl = 0;
                            int from = _dragFrom, to = _dragTo;
                            _dragFrom = -1;
                            _dragTo = -1;
                            ev.Use();
                            if (to >= 0 && to != from) Defer(() => MoveEntry(from, to));
                        }
                        break;
                    case EventType.Repaint:
                        if (_dragTo >= 0)
                        {
                            float ly = area.y + (_dragTo + (_dragTo > _dragFrom ? 1 : 0)) * RowHeight - 2f;
                            EditorGUI.DrawRect(new Rect(area.x, ly, area.width, 3f), Ui.Accent);
                        }
                        break;
                }
            }
        }

        private void DrawGroupHeader(Rect r, ViewItem v)
        {
            if (Event.current.type != EventType.Repaint) return;
            GUI.Label(new Rect(r.x + 6f, r.y, r.width - 12f, r.height), v.header + "  (" + v.count + ")", Ui.BigLabel);
        }

        private void DrawRow(Rect r, AvatarEntry e, int index, int dragId)
        {
            Event ev = Event.current;
            Ui.RowBackground(r, e == _previewEntry);
            if (ev.type == EventType.Repaint && _dragFrom == index)
            {
                EditorGUI.DrawRect(r, new Color(Ui.Accent.r, Ui.Accent.g, Ui.Accent.b, 0.15f));
            }

            // 選択
            e.selected = EditorGUI.Toggle(new Rect(r.x + 6f, r.y + 32f, 18f, 18f), e.selected);

            // サムネイル
            Rect tr = new Rect(r.x + 28f, r.y + 6f, 64f, 64f);
            if (ev.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(tr, new Color(0f, 0f, 0f, 0.25f));
                if (e.hasThumb && e.thumbTex == null) e.thumbTex = LoadSmallThumb(e.thumbnailPath);
                if (e.thumbTex != null) GUI.DrawTexture(tr, e.thumbTex, ScaleMode.ScaleToFit, true);
                else GUI.Label(tr, "未撮影", Ui.Small);
            }

            // 名前とチップ
            float tx = r.x + 100f;
            float rightEdge = r.xMax - 34f;
            GUI.Label(new Rect(tx, r.y + 4f, Mathf.Max(40f, rightEdge - tx - 4f), 20f), e.name, Ui.Row);
            if (ev.type == EventType.Repaint)
            {
                float nameW = Mathf.Min(Ui.Row.CalcSize(new GUIContent(e.name)).x + 6f, rightEdge - tx - 120f);
                float cx = tx + nameW;
                if (e.IsHidden) cx = Chip(cx, r.y + 6f, "(非表示)", Ui.Warn);
                if (e.hasThumb) cx = Chip(cx, r.y + 6f, "✓撮影済", Ui.Mint);
                if (e.IsMissing) cx = Chip(cx, r.y + 6f, "シーンに無い", Ui.Danger);
                if (e.duplicateName) cx = Chip(cx, r.y + 6f, "同名", Ui.Sky);
                if (e.manual) cx = Chip(cx, r.y + 6f, "手動", Ui.Sky);
                if (e.look.hasCamera) cx = Chip(cx, r.y + 6f, "個別カメラ", Ui.Sky);
                if (e.look.HasPose) cx = Chip(cx, r.y + 6f, "ポーズ", Ui.Sky);
            }

            // タグ・メモ
            float fw = Mathf.Max(60f, rightEdge - tx);
            EditorGUI.BeginChangeCheck();
            string tag = EditorGUI.DelayedTextField(new Rect(tx, r.y + 28f, Mathf.Min(110f, fw * 0.4f), 18f), e.tag ?? "", Ui.TagField);
            string memo = EditorGUI.DelayedTextField(new Rect(tx + Mathf.Min(110f, fw * 0.4f) + 4f, r.y + 28f, fw - Mathf.Min(110f, fw * 0.4f) - 4f, 18f), e.memo ?? "", Ui.TagField);
            if (EditorGUI.EndChangeCheck())
            {
                e.tag = tag;
                e.memo = memo;
                _viewDirty = true;
                MarkEntriesDirty();
            }

            // ボタン
            float bx = tx;
            float by = r.y + 52f;
            bool missing = e.IsMissing;
            using (new EditorGUI.DisabledScope(missing))
            {
                if (Ui.SmallButton(new Rect(bx, by, 62f, 20f), "プレビュー", false, "この見た目を撮影タブで確認")) { Defer(() => PreviewFromList(e)); }
                bx += 66f;
                if (Ui.SmallButton(new Rect(bx, by, 44f, 20f), "撮影", true, "このアバターだけ撮影")) { Defer(() => CaptureOne(e)); }
                bx += 48f;
            }
            using (new EditorGUI.DisabledScope(!e.hasThumb || missing))
            {
                if (Ui.SmallButton(new Rect(bx, by, 44f, 20f), "SDK", false, "VRChat SDKのアイコンに設定")) { Defer(() => SetSdkIcon(e)); }
                bx += 48f;
            }
            using (new EditorGUI.DisabledScope(!e.hasThumb))
            {
                if (Ui.SmallButton(new Rect(bx, by, 44f, 20f), "履歴", false, "前の画像と比較・復元")) { Defer(() => OpenCompare(e, false)); }
                bx += 48f;
            }
            if (SharedFor(e) != null)
            {
                if (Ui.SmallButton(new Rect(bx, by, 44f, 20f), "流用", false, "他シーンで撮影済みの同じアバターの画像を使う")) { Defer(() => ReuseShared(e)); }
            }

            // 右端: ハンドル / ↑ / ↓ / ×
            float rx = r.xMax - 28f;
            bool reorder = ReorderEnabled;
            Rect handle = new Rect(rx, r.y + 4f, 24f, 18f);
            if (ev.type == EventType.Repaint)
            {
                GUI.Label(handle, reorder ? "≡" : "", Ui.BigLabel);
                EditorGUIUtility.AddCursorRect(handle, reorder ? MouseCursor.Pan : MouseCursor.Arrow);
            }
            if (reorder && ev.type == EventType.MouseDown && ev.button == 0 && handle.Contains(ev.mousePosition))
            {
                _dragFrom = index;
                _dragTo = index;
                GUIUtility.hotControl = dragId;
                ev.Use();
            }
            using (new EditorGUI.DisabledScope(!reorder))
            {
                if (Ui.SmallButton(new Rect(rx, r.y + 24f, 24f, 16f), "↑", false)) { int i = index; Defer(() => MoveEntry(i, i - 1)); }
                if (Ui.SmallButton(new Rect(rx, r.y + 41f, 24f, 16f), "↓", false)) { int i = index; Defer(() => MoveEntry(i, i + 1)); }
            }
            if (Ui.SmallButton(new Rect(rx, r.y + 58f, 24f, 16f), "×", false, "リストから削除")) { Defer(() => RemoveEntry(e)); }

            // 行クリックでヒエラルキー上のアバターを選択
            if (ev.type == EventType.MouseDown && ev.button == 0 && tr.Contains(ev.mousePosition) && e.sceneObject != null)
            {
                Selection.activeGameObject = e.sceneObject;
                EditorGUIUtility.PingObject(e.sceneObject);
            }
        }

        private static float Chip(float x, float y, string text, Color color)
        {
            Vector2 sz = Ui.ChipSize(text);
            Ui.Chip(new Rect(x, y, sz.x, sz.y), text, color);
            return x + sz.x + 4f;
        }

        private void PreviewFromList(AvatarEntry e)
        {
            if (e.IsMissing) return;
            CloseSession();
            _tab = (int)TabId.Capture;
            OpenSession(e, true);
        }
    }
}
#endif
