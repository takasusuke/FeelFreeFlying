using UnityEngine;
using UnityEngine.InputSystem;

namespace FeelFreeFlying.Flight
{
    /// <summary>
    /// 操作設定の画面。**操作方式を試遊中に切り替えるためのもの**（→ docs/m1-plan.md §2）。
    ///
    /// M1で決めたいのは「どの操作が気持ちいいか」なので、比べる手段が要る。
    /// ビルドし直さないと試せないと、比較のたびに数分待つことになる。
    ///
    /// 製品の設定画面ではない。項目が固まったら作り直す。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SettingsScreen : MonoBehaviour
    {
        [SerializeField] private FlightController controller;
        [SerializeField] private FlightInput input;
        [SerializeField] private FlightHud hud;
        [SerializeField] private TitleScreen title;

        private static readonly string[] ItemLabels =
        {
            "操縦方式",
            "上下反転",
            "ジャンプ",
        };

        private GUIStyle titleStyle;
        private GUIStyle itemStyle;
        private GUIStyle selectedStyle;
        private GUIStyle valueStyle;
        private GUIStyle selectedValueStyle;
        private GUIStyle helpStyle;

        private int index;
        private bool open;

        /// <summary>開いている間は操作を止める。</summary>
        public bool IsOpen => open;

        private void Update()
        {
            if (TogglePressed()) SetOpen(!open);
            if (!open) return;

            if (MoveVertical(out int step)) index = (index + step + ItemLabels.Length) % ItemLabels.Length;
            if (MoveHorizontal(out int direction)) Change(index, direction);
        }

        private void SetOpen(bool value)
        {
            open = value;

            // 設定中は飛ばない。カーソルは<see cref="FlightInput"/>が持っているので、
            // 入力ごと止めれば自然に返る
            // 起動画面がまだ出ているなら、閉じても操作を始めない
            bool playing = !value && (title == null || title.Started);

            if (controller != null) controller.enabled = playing;
            if (input != null) input.enabled = playing;
            if (hud != null) hud.enabled = playing;
        }

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
                if (pad.dpad.right.wasPressedThisFrame || pad.buttonSouth.wasPressedThisFrame) direction = 1;
                else if (pad.dpad.left.wasPressedThisFrame) direction = -1;
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.rightArrowKey.wasPressedThisFrame || keyboard.enterKey.wasPressedThisFrame) direction = 1;
                else if (keyboard.leftArrowKey.wasPressedThisFrame) direction = -1;
            }

            return direction != 0;
        }

        private static void Change(int item, int direction)
        {
            switch (item)
            {
                case 0:
                    FlightSettings.Steering = FlightSettings.Steering == SteeringMode.IndependentView
                        ? SteeringMode.FollowView
                        : SteeringMode.IndependentView;
                    break;
                case 1:
                    FlightSettings.InvertPitch = !FlightSettings.InvertPitch;
                    break;
                case 2:
                    FlightSettings.Jump = FlightSettings.Jump == JumpBinding.LeftTrigger
                        ? JumpBinding.Cross
                        : JumpBinding.LeftTrigger;
                    break;
            }
        }

        private static string ValueLabel(int item) => item switch
        {
            0 => FlightSettings.SteeringLabel(FlightSettings.Steering),
            1 => FlightSettings.InvertPitch ? "あり" : "なし",
            2 => FlightSettings.JumpLabel(FlightSettings.Jump),
            _ => string.Empty,
        };

        private void OnGUI()
        {
            if (!open) return;

            EnsureStyles();

            var panel = new Rect(Screen.width * 0.5f - 360f, Screen.height * 0.25f, 720f, 320f);
            GUI.Box(panel, GUIContent.none);

            GUI.Label(new Rect(panel.x, panel.y + 16f, panel.width, 40f), "操作設定", titleStyle);

            for (int i = 0; i < ItemLabels.Length; i++)
            {
                var row = new Rect(panel.x + 40f, panel.y + 80f + i * 46f, panel.width - 80f, 40f);
                GUIStyle style = i == index ? selectedStyle : itemStyle;
                GUI.Label(row, $"{(i == index ? "▶ " : "   ")}{ItemLabels[i]}", style);
                GUI.Label(row, ValueLabel(i), i == index ? selectedValueStyle : valueStyle);
            }

            GUI.Label(new Rect(panel.x, panel.yMax - 76f, panel.width, 60f),
                "上下で項目、左右で切り替え / OPTIONS・Tab で閉じる\n" +
                "「視線の方向へ飛ぶ」はスパイダーマン等に近い操作。" +
                "「視点と進路を分ける」は高度を保ったまま見回せる",
                helpStyle);
        }

        private void EnsureStyles()
        {
            titleStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 28,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white },
            };
            itemStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 20,
                normal = { textColor = new Color(1f, 1f, 1f, 0.75f) },
            };
            selectedStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 20,
                normal = { textColor = new Color(0.7f, 0.92f, 1f, 1f) },
            };
            valueStyle ??= new GUIStyle(itemStyle) { alignment = TextAnchor.MiddleRight };
            selectedValueStyle ??= new GUIStyle(selectedStyle) { alignment = TextAnchor.MiddleRight };
            helpStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(1f, 1f, 1f, 0.7f) },
            };
        }
    }
}
