#if UNITY_EDITOR
// ============================================================================
//  UIテーマ(角丸カード / ピル型トグル / グラデーションヘッダー)
//  ライト/ダークどちらのエディタスキンでも読めるよう、色はスキンに合わせて切り替える。
// ============================================================================
using System;
using UnityEditor;
using UnityEngine;

namespace LollipopCandy.AvatarHub
{
    internal static class Ui
    {
        // ブランドカラー
        public static readonly Color Accent = new Color(1.00f, 0.43f, 0.66f);   // キャンディピンク
        public static readonly Color AccentDeep = new Color(0.93f, 0.30f, 0.55f);
        public static readonly Color Mint = new Color(0.42f, 0.84f, 0.72f);
        public static readonly Color Sky = new Color(0.45f, 0.72f, 0.98f);
        public static readonly Color Warn = new Color(1.00f, 0.72f, 0.28f);
        public static readonly Color Danger = new Color(0.95f, 0.40f, 0.40f);

        private static Texture2D _round;
        private static Texture2D _roundHover;
        private static Texture2D _roundActive;
        private static Texture2D _gradient;
        private static GUIStyle _card, _button, _buttonPrimary, _pill, _title, _muted, _chip, _small, _smallPrimary,
            _headerTitle, _headerSub, _row, _tagField, _bigLabel, _wrapMuted;

        public static bool Dark { get { return EditorGUIUtility.isProSkin; } }

        public static Color CardColor { get { return Dark ? new Color(0.285f, 0.285f, 0.335f) : new Color(0.93f, 0.93f, 0.97f); } }
        public static Color RowColor { get { return Dark ? new Color(0.25f, 0.25f, 0.30f) : new Color(0.96f, 0.96f, 0.99f); } }
        public static Color NeutralButton { get { return Dark ? new Color(0.40f, 0.40f, 0.47f) : new Color(0.84f, 0.84f, 0.90f); } }
        public static Color MutedText { get { return Dark ? new Color(0.72f, 0.72f, 0.80f) : new Color(0.38f, 0.38f, 0.46f); } }
        public static Color BodyText { get { return Dark ? new Color(0.92f, 0.92f, 0.96f) : new Color(0.12f, 0.12f, 0.16f); } }

        // ---- 初期化 ----

        public static void Ensure()
        {
            if (_round != null && _gradient != null && _card != null) return;
            Build();
        }

        private static Texture2D MakeRound(float shade)
        {
            const int n = 32;
            const float r = 11f;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float dx = Mathf.Max(0f, Mathf.Max(r - (x + 0.5f), (x + 0.5f) - (n - r)));
                    float dy = Mathf.Max(0f, Mathf.Max(r - (y + 0.5f), (y + 0.5f) - (n - r)));
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(r - dist + 0.5f);
                    byte v = (byte)Mathf.RoundToInt(shade * 255f);
                    px[y * n + x] = new Color32(v, v, v, (byte)Mathf.RoundToInt(a * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false);
            return tex;
        }

        private static void Build()
        {
            _pillOn = null;
            _round = MakeRound(1f);
            _roundHover = MakeRound(0.90f);
            _roundActive = MakeRound(0.78f);

            _gradient = new Texture2D(128, 1, TextureFormat.RGBA32, false);
            _gradient.hideFlags = HideFlags.HideAndDontSave;
            _gradient.wrapMode = TextureWrapMode.Clamp;
            _gradient.filterMode = FilterMode.Bilinear;
            var gp = new Color32[128];
            for (int i = 0; i < 128; i++)
            {
                float t = i / 127f;
                Color c = t < 0.5f ? Color.Lerp(Accent, new Color(0.72f, 0.62f, 1f), t * 2f)
                                   : Color.Lerp(new Color(0.72f, 0.62f, 1f), Sky, (t - 0.5f) * 2f);
                gp[i] = c;
            }
            _gradient.SetPixels32(gp);
            _gradient.Apply(false);

            var border = new RectOffset(11, 11, 11, 11);

            _card = RoundStyle(border, new RectOffset(12, 12, 10, 10), new RectOffset(0, 0, 4, 4));
            _row = RoundStyle(border, new RectOffset(6, 6, 6, 6), new RectOffset(0, 0, 3, 3));

            _button = RoundStyle(border, new RectOffset(10, 10, 5, 5), new RectOffset(2, 2, 2, 2));
            _button.alignment = TextAnchor.MiddleCenter;
            _button.fontStyle = FontStyle.Bold;
            _button.fixedHeight = 0;
            SetText(_button, BodyText);

            _buttonPrimary = new GUIStyle(_button);
            SetText(_buttonPrimary, Color.white);

            _pill = new GUIStyle(_button);
            _pill.fontStyle = FontStyle.Normal;
            _pill.padding = new RectOffset(10, 10, 4, 4);

            _small = new GUIStyle(_button);
            _small.fontSize = 10;
            _small.padding = new RectOffset(4, 4, 2, 2);
            _small.margin = new RectOffset(1, 1, 1, 1);

            _smallPrimary = new GUIStyle(_small);
            SetText(_smallPrimary, Color.white);

            _title = new GUIStyle(EditorStyles.boldLabel);
            _title.fontSize = 12;
            SetText(_title, BodyText);

            _bigLabel = new GUIStyle(EditorStyles.boldLabel);
            _bigLabel.fontSize = 12;
            SetText(_bigLabel, BodyText);

            _muted = new GUIStyle(EditorStyles.miniLabel);
            SetText(_muted, MutedText);

            _wrapMuted = new GUIStyle(_muted);
            _wrapMuted.wordWrap = true;

            _chip = new GUIStyle(_small);
            _chip.fontStyle = FontStyle.Bold;
            _chip.padding = new RectOffset(7, 7, 1, 1);
            _chip.margin = new RectOffset(2, 2, 2, 2);
            SetText(_chip, Color.white);

            _headerTitle = new GUIStyle(EditorStyles.boldLabel);
            _headerTitle.fontSize = 16;
            SetText(_headerTitle, Color.white);

            _headerSub = new GUIStyle(EditorStyles.label);
            _headerSub.fontSize = 10;
            SetText(_headerSub, new Color(1f, 1f, 1f, 0.9f));

            _tagField = new GUIStyle(EditorStyles.miniTextField);
        }

        private static GUIStyle RoundStyle(RectOffset border, RectOffset padding, RectOffset margin)
        {
            var st = new GUIStyle(GUI.skin.box);
            st.normal.background = _round;
            st.hover.background = _roundHover;
            st.active.background = _roundActive;
            st.focused.background = _round;
            st.onNormal.background = _round;
            st.onHover.background = _roundHover;
            st.onActive.background = _roundActive;
            st.onFocused.background = _round;
            st.border = border;
            st.padding = padding;
            st.margin = margin;
            st.overflow = new RectOffset(0, 0, 0, 0);
            st.alignment = TextAnchor.UpperLeft;
            st.stretchHeight = false;
            return st;
        }

        private static void SetText(GUIStyle st, Color c)
        {
            st.normal.textColor = c;
            st.hover.textColor = c;
            st.active.textColor = c;
            st.focused.textColor = c;
            st.onNormal.textColor = c;
            st.onHover.textColor = c;
            st.onActive.textColor = c;
            st.onFocused.textColor = c;
        }

        // ---- スタイル参照 ----

        public static GUIStyle Title { get { Ensure(); return _title; } }
        public static GUIStyle BigLabel { get { Ensure(); return _bigLabel; } }
        public static GUIStyle Muted { get { Ensure(); return _muted; } }
        public static GUIStyle WrapMuted { get { Ensure(); return _wrapMuted; } }
        public static GUIStyle TagField { get { Ensure(); return _tagField; } }
        public static GUIStyle Row { get { Ensure(); return _row; } }
        public static GUIStyle Small { get { Ensure(); return _small; } }

        // ---- 色の一時変更 ----

        public struct Tint : IDisposable
        {
            private readonly Color _bg;
            public Tint(Color background)
            {
                _bg = GUI.backgroundColor;
                GUI.backgroundColor = background;
            }
            public void Dispose() { GUI.backgroundColor = _bg; }
        }

        // ---- ヘッダー ----

        public static void Header(string sceneName, int avatarCount, int captured)
        {
            Ensure();
            Rect r = GUILayoutUtility.GetRect(0f, 54f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
            {
                GUI.DrawTexture(r, _gradient, ScaleMode.StretchToFill, false);
                EditorGUI.DrawRect(new Rect(r.x, r.yMax - 1f, r.width, 1f), new Color(1f, 1f, 1f, 0.25f));
            }
            GUI.Label(new Rect(r.x + 14f, r.y + 7f, r.width - 28f, 24f), "Avatar Thumbnail Manager", _headerTitle);
            GUI.Label(new Rect(r.x + 14f, r.y + 30f, r.width - 28f, 18f),
                string.Format("{0}  ・  アバター {1} 体  ・  撮影済 {2} 体", sceneName, avatarCount, captured), _headerSub);
        }

        // ---- ウィジェット ----

        public static bool Button(string text, bool primary = false, float width = 0f, float height = 26f)
        {
            Ensure();
            GUILayoutOption[] opts = width > 0f
                ? new[] { GUILayout.Width(width), GUILayout.Height(height) }
                : new[] { GUILayout.Height(height) };
            using (new Tint(primary ? Accent : NeutralButton))
            {
                return GUILayout.Button(text, primary ? _buttonPrimary : _button, opts);
            }
        }

        public static bool Button(GUIContent content, bool primary = false, float width = 0f, float height = 26f)
        {
            Ensure();
            GUILayoutOption[] opts = width > 0f
                ? new[] { GUILayout.Width(width), GUILayout.Height(height) }
                : new[] { GUILayout.Height(height) };
            using (new Tint(primary ? Accent : NeutralButton))
            {
                return GUILayout.Button(content, primary ? _buttonPrimary : _button, opts);
            }
        }

        /// <summary>ピル型トグル。押された後の状態を返す。</summary>
        public static bool Pill(string text, bool on, float width = 0f)
        {
            Ensure();
            GUILayoutOption[] opts = width > 0f
                ? new[] { GUILayout.Width(width), GUILayout.Height(24f) }
                : new[] { GUILayout.Height(24f) };
            using (new Tint(on ? Accent : NeutralButton))
            {
                bool clicked = GUILayout.Button(text, PillStyle(on), opts);
                return clicked ? !on : on;
            }
        }

        private static GUIStyle PillStyle(bool on)
        {
            // _pill は文字色が通常ボタン用。ON時は白文字にしたいので都度スタイルを切り替える
            if (on)
            {
                if (_pillOn == null)
                {
                    _pillOn = new GUIStyle(_pill);
                    SetText(_pillOn, Color.white);
                    _pillOn.fontStyle = FontStyle.Bold;
                }
                return _pillOn;
            }
            return _pill;
        }

        private static GUIStyle _pillOn;

        /// <summary>小さな丸ボタン(リスト行用)。</summary>
        public static bool SmallButton(Rect r, string text, bool primary = false, string tooltip = null)
        {
            Ensure();
            using (new Tint(primary ? Accent : NeutralButton))
            {
                return GUI.Button(r, new GUIContent(text, tooltip), primary ? _smallPrimary : _small);
            }
        }

        public static void Chip(Rect r, string text, Color color)
        {
            Ensure();
            if (Event.current.type != EventType.Repaint) return;
            using (new Tint(color))
            {
                _chip.Draw(r, new GUIContent(text), false, false, false, false);
            }
        }

        public static Vector2 ChipSize(string text)
        {
            Ensure();
            Vector2 s = _chip.CalcSize(new GUIContent(text));
            return new Vector2(s.x, 16f);
        }

        // ---- カード ----

        public sealed class CardScope : IDisposable
        {
            public CardScope(string title, string right)
            {
                Ensure();
                using (new Tint(CardColor))
                {
                    GUILayout.BeginVertical(_card);
                }
                if (!string.IsNullOrEmpty(title))
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(title, _title);
                    GUILayout.FlexibleSpace();
                    if (!string.IsNullOrEmpty(right)) GUILayout.Label(right, _muted);
                    GUILayout.EndHorizontal();
                    GUILayout.Space(2f);
                }
            }

            public void Dispose()
            {
                GUILayout.EndVertical();
            }
        }

        public static CardScope Card(string title, string right = null)
        {
            return new CardScope(title, right);
        }

        /// <summary>行カード(手動レイアウト用)。</summary>
        public static void RowBackground(Rect r, bool highlight)
        {
            Ensure();
            if (Event.current.type != EventType.Repaint) return;
            using (new Tint(highlight ? new Color(1f, 0.43f, 0.66f, 0.30f) : RowColor))
            {
                _row.Draw(r, GUIContent.none, false, false, false, false);
            }
        }
    }
}
#endif
