using UnityEngine;
using UnityEngine.InputSystem;

namespace FeelFreeFlying.Flight
{
    /// <summary>
    /// 操作設定とキーコンフィグ（→ docs/m1-plan.md §2.2）。
    ///
    /// **押しやすいボタンは人によって違う。** M1の試遊でも、ジャンプを×にするかL2にするかは
    /// 「右スティックを操作しながら押せるか」で割れた。1項目ずつ設定に出すのをやめ、
    /// **押して効く操作は全部差し替えられる**ようにしてある（<see cref="FlightBindings"/>）。
    ///
    /// 上下反転も**機首と視点で別々**に持つ。同じ人でも好みが逆になるため。
    ///
    /// IMGUIで組んでいるのは、この画面が**遊びの中身ではない**から。
    /// 見た目を整えるのはM5の課題で、ここでは「変えられること」だけを満たす。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SettingsScreen : MonoBehaviour
    {
        [SerializeField] private FlightController controller;
        [SerializeField] private FlightInput input;
        [SerializeField] private FlightHud hud;
        [SerializeField] private TitleScreen title;

        /// <summary>行の種類。ボタン配置の行は<see cref="FlightBindings.All"/>の並びで作る。</summary>
        private enum RowKind
        {
            Steering,
            InvertFlight,
            InvertLook,
            Heading,
            Binding,
            ResetBindings,
        }

        private readonly struct Row
        {
            public readonly RowKind Kind;
            public readonly FlightAction Action;

            public Row(RowKind kind, FlightAction action = default)
            {
                Kind = kind;
                Action = action;
            }

            public bool Selectable => Kind != RowKind.Heading;
        }

        private Row[] rows;

        private GUIStyle titleStyle;
        private GUIStyle itemStyle;
        private GUIStyle selectedStyle;
        private GUIStyle valueStyle;
        private GUIStyle selectedValueStyle;
        private GUIStyle headingStyle;
        private GUIStyle helpStyle;

        private int index;
        private bool open;

        /// <summary>割り当ての取り込み中か。次に押されたボタン／キーを割り当てる。</summary>
        private bool capturing;

        /// <summary>取り込みを始める前に、決定ボタンから指が離れるのを待っているか。</summary>
        private bool waitingForRelease;

        /// <summary>開いている間は操作を止める。</summary>
        public bool IsOpen => open;

        private void Awake() => BuildRows();

        private void BuildRows()
        {
            var list = new System.Collections.Generic.List<Row>
            {
                new Row(RowKind.Steering),
                new Row(RowKind.InvertFlight),
                new Row(RowKind.InvertLook),
                new Row(RowKind.Heading),
            };

            foreach (FlightAction action in FlightBindings.All) list.Add(new Row(RowKind.Binding, action));
            list.Add(new Row(RowKind.ResetBindings));

            rows = list.ToArray();
            index = 0;
        }

        private void Update()
        {
            if (capturing) { UpdateCapture(); return; }

            if (TogglePressed()) SetOpen(!open);
            if (!open) return;

            if (MoveVertical(out int step)) Move(step);
            if (MoveHorizontal(out int direction)) Change(direction);
            if (ConfirmPressed()) Confirm();
        }

        // ------------------------------------------------------------------
        // 割り当ての取り込み
        // ------------------------------------------------------------------

        /// <summary>
        /// 次に押されたボタン／キーを割り当てる。
        ///
        /// **決定ボタンを離すまで待つ。** 待たないと、取り込みを始めるのに押した×やEnterが
        /// そのまま割り当てられてしまう。
        /// </summary>
        private void UpdateCapture()
        {
            if (waitingForRelease)
            {
                if (FlightBindings.NothingPressed()) waitingForRelease = false;
                return;
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            {
                capturing = false;
                return;
            }

            FlightAction action = rows[index].Action;

            PadButton pad = FlightBindings.CapturePad();
            if (pad != PadButton.None)
            {
                FlightBindings.SetPad(action, pad);
                capturing = false;
                return;
            }

            Key key = FlightBindings.CaptureKey();
            if (key != Key.None)
            {
                FlightBindings.SetKey(action, key);
                capturing = false;
            }
        }

        private void Confirm()
        {
            switch (rows[index].Kind)
            {
                case RowKind.Binding:
                    capturing = true;
                    waitingForRelease = true;
                    break;

                case RowKind.ResetBindings:
                    FlightBindings.ResetToDefaults();
                    break;

                default:
                    Change(1); // 切り替え項目は決定でも先へ送る
                    break;
            }
        }

        // ------------------------------------------------------------------
        // 移動と変更
        // ------------------------------------------------------------------

        private void Move(int step)
        {
            // 見出しの行は飛ばす
            for (int i = 0; i < rows.Length; i++)
            {
                index = (index + step + rows.Length) % rows.Length;
                if (rows[index].Selectable) return;
            }
        }

        private void Change(int direction)
        {
            switch (rows[index].Kind)
            {
                case RowKind.Steering:
                    FlightSettings.Steering = FlightSettings.Steering == SteeringMode.IndependentView
                        ? SteeringMode.FollowView
                        : SteeringMode.IndependentView;
                    break;

                case RowKind.InvertFlight:
                    FlightSettings.InvertFlightPitch = !FlightSettings.InvertFlightPitch;
                    break;

                case RowKind.InvertLook:
                    FlightSettings.InvertLookPitch = !FlightSettings.InvertLookPitch;
                    break;

                case RowKind.Binding:
                    // 左で割り当てを外す。**外せないと「使わないボタン」を空けられない**
                    if (direction < 0)
                    {
                        FlightBindings.SetPad(rows[index].Action, PadButton.None);
                        FlightBindings.SetKey(rows[index].Action, Key.None);
                    }
                    else
                    {
                        Confirm();
                    }
                    break;
            }
        }

        private void SetOpen(bool value)
        {
            open = value;
            capturing = false;

            // 設定中は飛ばない。カーソルは<see cref="FlightInput"/>が持っているので、
            // 入力ごと止めれば自然に返る。
            // 起動画面がまだ出ているなら、閉じても操作を始めない
            bool playing = !value && (title == null || title.Started);

            if (controller != null) controller.enabled = playing;
            if (input != null) input.enabled = playing;
            if (hud != null) hud.enabled = playing;
        }

        // ------------------------------------------------------------------
        // 画面の操作（設定画面自身の入力は固定。**ここまで差し替えると開けなくなる**）
        // ------------------------------------------------------------------

        private static bool TogglePressed()
        {
            Gamepad pad = Gamepad.current;
            if (pad != null && pad.startButton.wasPressedThisFrame) return true;

            Keyboard keyboard = Keyboard.current;
            return keyboard != null && keyboard.tabKey.wasPressedThisFrame;
        }

        private static bool MoveVertical(out int step)
        {
            step = 0;
            Gamepad pad = Gamepad.current;
            if (pad != null)
            {
                if (pad.dpad.down.wasPressedThisFrame) step = 1;
                else if (pad.dpad.up.wasPressedThisFrame) step = -1;
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.downArrowKey.wasPressedThisFrame) step = 1;
                else if (keyboard.upArrowKey.wasPressedThisFrame) step = -1;
            }

            return step != 0;
        }

        private static bool MoveHorizontal(out int direction)
        {
            direction = 0;
            Gamepad pad = Gamepad.current;
            if (pad != null)
            {
                if (pad.dpad.right.wasPressedThisFrame) direction = 1;
                else if (pad.dpad.left.wasPressedThisFrame) direction = -1;
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.rightArrowKey.wasPressedThisFrame) direction = 1;
                else if (keyboard.leftArrowKey.wasPressedThisFrame) direction = -1;
            }

            return direction != 0;
        }

        private static bool ConfirmPressed()
        {
            Gamepad pad = Gamepad.current;
            if (pad != null && pad.buttonSouth.wasPressedThisFrame) return true;

            Keyboard keyboard = Keyboard.current;
            return keyboard != null && keyboard.enterKey.wasPressedThisFrame;
        }

        // ------------------------------------------------------------------
        // 表示
        // ------------------------------------------------------------------

        private string RowLabel(int i) => rows[i].Kind switch
        {
            RowKind.Steering => "操縦方式",
            RowKind.InvertFlight => "機首の上下反転（飛行）",
            RowKind.InvertLook => "視点の上下反転（カメラ）",
            RowKind.Heading => "― ボタン配置 ―",
            RowKind.Binding => FlightBindings.Label(rows[i].Action),
            RowKind.ResetBindings => "ボタン配置を既定に戻す",
            _ => string.Empty,
        };

        private string RowValue(int i)
        {
            switch (rows[i].Kind)
            {
                case RowKind.Steering:
                    return FlightSettings.SteeringLabel(FlightSettings.Steering);
                case RowKind.InvertFlight:
                    return FlightSettings.InvertFlightPitch ? "あり" : "なし";
                case RowKind.InvertLook:
                    return FlightSettings.InvertLookPitch ? "あり" : "なし";
                case RowKind.Binding:
                {
                    if (capturing && i == index) return "← 押してください（Escで取消）";

                    FlightAction action = rows[i].Action;
                    bool playStation = input == null || input.IsPlayStationPad;

                    return $"{FlightBindings.PadLabel(FlightBindings.Pad(action), playStation)}" +
                           $"　/　{FlightBindings.KeyLabel(FlightBindings.Keyboard(action))}";
                }
                default:
                    return string.Empty;
            }
        }

        private void OnGUI()
        {
            if (!open) return;

            EnsureStyles();

            float height = 150f + rows.Length * 34f;
            var panel = new Rect(Screen.width * 0.5f - 400f,
                Mathf.Max(20f, Screen.height * 0.5f - height * 0.5f), 800f, height);
            GUI.Box(panel, GUIContent.none);

            GUI.Label(new Rect(panel.x, panel.y + 14f, panel.width, 36f), "操作設定", titleStyle);

            for (int i = 0; i < rows.Length; i++)
            {
                var row = new Rect(panel.x + 36f, panel.y + 60f + i * 34f, panel.width - 72f, 30f);

                if (rows[i].Kind == RowKind.Heading)
                {
                    GUI.Label(row, RowLabel(i), headingStyle);
                    continue;
                }

                bool selected = i == index;
                GUI.Label(row, $"{(selected ? "▶ " : "   ")}{RowLabel(i)}", selected ? selectedStyle : itemStyle);
                GUI.Label(row, RowValue(i), selected ? selectedValueStyle : valueStyle);
            }

            GUI.Label(new Rect(panel.x, panel.yMax - 66f, panel.width, 56f),
                capturing
                    ? "割り当てたいボタンかキーを押してください（Escで取消）"
                    : "上下で項目 / 左右で切り替え / 決定（×・Enter）でボタンを割り当て / 左で割り当てを外す\n" +
                      "OPTIONS・Tab で閉じる　　※スティックとWASDは「操縦方式」で決まる",
                helpStyle);
        }

        private void EnsureStyles()
        {
            titleStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 26,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white },
            };
            itemStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 17,
                normal = { textColor = new Color(1f, 1f, 1f, 0.75f) },
            };
            selectedStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 17,
                normal = { textColor = new Color(0.7f, 0.92f, 1f, 1f) },
            };
            valueStyle ??= new GUIStyle(itemStyle) { alignment = TextAnchor.MiddleRight };
            selectedValueStyle ??= new GUIStyle(selectedStyle) { alignment = TextAnchor.MiddleRight };
            headingStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(1f, 1f, 1f, 0.45f) },
            };
            helpStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(1f, 1f, 1f, 0.7f) },
            };
        }
    }
}
