using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace FeelFreeFlying.Flight
{
    /// <summary>押しっぱなしで効く操作か、押した瞬間に1回効く操作か。</summary>
    public enum PressKind
    {
        /// <summary>押している間ずっと（ブースト・ダッシュ・ジャンプ）。</summary>
        Held,

        /// <summary>押した瞬間に1回だけ（切り替え系）。</summary>
        Pressed,
    }

    /// <summary>
    /// 割り当てを変えられる操作。
    ///
    /// **スティックとWASDは含めない。** 「左スティックで進む／右スティックで見る」は
    /// 操縦方式そのもの（<see cref="SteeringMode"/>）で選ぶ話で、ボタンの差し替えとは別。
    /// ここに入れるのは**押して効くもの**だけ。
    /// </summary>
    public enum FlightAction
    {
        Boost,
        Dash,
        Jump,
        LevelFlight,
        ToggleMotion,
        ToggleView,
        DropStraight,
        RecenterView,
        Reset,
        ToggleInvert,
        ToggleCollision,
    }

    /// <summary>
    /// ゲームパッドのボタン。**PlayStationの呼び方で持つ**（PS5で試遊しているため）。
    /// 表示だけXbox系に読み替える（<see cref="FlightBindings.PadLabel"/>）。
    /// </summary>
    public enum PadButton
    {
        None = 0,
        Cross, Circle, Square, Triangle,
        L1, R1, L2, R2, L3, R3,
        Up, Down, Left, Right,
        Options, Share,
    }

    /// <summary>
    /// ボタン配置（docs/m1-plan.md §2）。
    ///
    /// **人によって押しやすいボタンが違う。** 実際、ジャンプを×にするかL2にするかは
    /// 「右スティックを操作しながら押せるか」で割れた。1つずつ設定に出すより、
    /// **全部の操作を差し替えられる**ほうが早い。
    ///
    /// 値は<see cref="PlayerPrefs"/>に残す。**アクションアセットは使わない**——
    /// 入力は<see cref="FlightInput"/>がデバイスを直接読む作りで、
    /// そこへ割り当て表を1枚かませるほうが構造が単純になる。
    /// </summary>
    public static class FlightBindings
    {
        private const string PadKeyPrefix = "ff.bind.pad.";
        private const string KeyKeyPrefix = "ff.bind.key.";

        public static readonly FlightAction[] All =
            (FlightAction[])Enum.GetValues(typeof(FlightAction));

        private readonly struct Definition
        {
            public readonly string Label;
            public readonly PadButton Pad;
            public readonly Key Key;
            public readonly PressKind Kind;

            public Definition(string label, PadButton pad, Key key, PressKind kind)
            {
                Label = label;
                Pad = pad;
                Key = key;
                Kind = kind;
            }
        }

        /// <summary>
        /// 既定の配置。**M1の試遊で落ち着いた形**（`m1-plan.md` §2）。
        /// 加減速はR2/L2のアナログなので、ここには出てこない。
        /// </summary>
        private static readonly Dictionary<FlightAction, Definition> Defaults =
            new Dictionary<FlightAction, Definition>
            {
                [FlightAction.Boost] = new Definition("ブースト（飛行）", PadButton.L1, Key.LeftShift, PressKind.Held),
                [FlightAction.Dash] = new Definition("ダッシュ（歩行）", PadButton.R1, Key.LeftShift, PressKind.Held),
                [FlightAction.Jump] = new Definition("ジャンプ（歩行）", PadButton.L2, Key.Space, PressKind.Held),
                [FlightAction.LevelFlight] = new Definition("水平に戻す（飛行）", PadButton.L3, Key.Space, PressKind.Held),
                [FlightAction.ToggleMotion] = new Definition("着地する / 飛び立つ", PadButton.Square, Key.F, PressKind.Pressed),
                [FlightAction.ToggleView] = new Definition("一人称 / 三人称", PadButton.Triangle, Key.C, PressKind.Pressed),
                [FlightAction.DropStraight] = new Definition("真下に落ちる", PadButton.Circle, Key.LeftCtrl, PressKind.Pressed),
                [FlightAction.RecenterView] = new Definition("視点を進行方向へ", PadButton.R3, Key.None, PressKind.Pressed),
                [FlightAction.Reset] = new Definition("姿勢をリセット", PadButton.Share, Key.R, PressKind.Pressed),
                [FlightAction.ToggleInvert] = new Definition("上下反転の切り替え", PadButton.Up, Key.I, PressKind.Pressed),
                [FlightAction.ToggleCollision] = new Definition("当たり判定の切り替え", PadButton.Down, Key.K, PressKind.Pressed),
            };

        private static readonly Dictionary<FlightAction, PadButton> PadBindings =
            new Dictionary<FlightAction, PadButton>();

        private static readonly Dictionary<FlightAction, Key> KeyBindings =
            new Dictionary<FlightAction, Key>();

        static FlightBindings()
        {
            foreach (FlightAction action in All)
            {
                Definition definition = Defaults[action];

                PadBindings[action] = (PadButton)PlayerPrefs.GetInt(
                    PadKeyPrefix + action, (int)definition.Pad);
                KeyBindings[action] = (Key)PlayerPrefs.GetInt(
                    KeyKeyPrefix + action, (int)definition.Key);
            }
        }

        public static string Label(FlightAction action) => Defaults[action].Label;

        public static PressKind Kind(FlightAction action) => Defaults[action].Kind;

        public static PadButton Pad(FlightAction action) => PadBindings[action];

        public static Key Keyboard(FlightAction action) => KeyBindings[action];

        /// <summary>
        /// パッドのボタンを割り当てる。
        /// **同じボタンを使っている別の操作からは外す。** 1つのボタンで2つ動くと、
        /// どちらが効いたのか分からないまま「壊れている」と感じる。
        /// </summary>
        public static void SetPad(FlightAction action, PadButton button)
        {
            if (button != PadButton.None)
            {
                foreach (FlightAction other in All)
                {
                    if (other != action && PadBindings[other] == button) SetPadRaw(other, PadButton.None);
                }
            }

            SetPadRaw(action, button);
            PlayerPrefs.Save();
        }

        public static void SetKey(FlightAction action, Key key)
        {
            if (key != Key.None)
            {
                foreach (FlightAction other in All)
                {
                    if (other != action && KeyBindings[other] == key) SetKeyRaw(other, Key.None);
                }
            }

            SetKeyRaw(action, key);
            PlayerPrefs.Save();
        }

        private static void SetPadRaw(FlightAction action, PadButton button)
        {
            PadBindings[action] = button;
            PlayerPrefs.SetInt(PadKeyPrefix + action, (int)button);
        }

        private static void SetKeyRaw(FlightAction action, Key key)
        {
            KeyBindings[action] = key;
            PlayerPrefs.SetInt(KeyKeyPrefix + action, (int)key);
        }

        public static void ResetToDefaults()
        {
            foreach (FlightAction action in All)
            {
                SetPadRaw(action, Defaults[action].Pad);
                SetKeyRaw(action, Defaults[action].Key);
            }

            PlayerPrefs.Save();
        }

        /// <summary>既定から変えられているか。設定画面に「既定」と出すために使う。</summary>
        public static bool IsDefault(FlightAction action) =>
            PadBindings[action] == Defaults[action].Pad && KeyBindings[action] == Defaults[action].Key;

        // ------------------------------------------------------------------
        // 読み取り
        // ------------------------------------------------------------------

        /// <summary>割り当てられたパッドのボタンが今押されているか。</summary>
        public static bool IsPadActive(Gamepad pad, FlightAction action)
        {
            ButtonControl control = Control(pad, PadBindings[action]);
            if (control == null) return false;

            return Kind(action) == PressKind.Held ? control.isPressed : control.wasPressedThisFrame;
        }

        public static bool IsKeyActive(Keyboard keyboard, FlightAction action)
        {
            Key key = KeyBindings[action];
            if (keyboard == null || key == Key.None) return false;

            KeyControl control = keyboard[key];
            if (control == null) return false;

            return Kind(action) == PressKind.Held ? control.isPressed : control.wasPressedThisFrame;
        }

        /// <summary>
        /// ボタンに対応するコントロール。
        /// **L2/R2はアナログ**だが、<see cref="ButtonControl"/>なので押下として扱える
        /// （既定のしきい値が入っている）。
        /// </summary>
        private static ButtonControl Control(Gamepad pad, PadButton button)
        {
            if (pad == null) return null;

            return button switch
            {
                PadButton.Cross => pad.buttonSouth,
                PadButton.Circle => pad.buttonEast,
                PadButton.Square => pad.buttonWest,
                PadButton.Triangle => pad.buttonNorth,
                PadButton.L1 => pad.leftShoulder,
                PadButton.R1 => pad.rightShoulder,
                PadButton.L2 => pad.leftTrigger,
                PadButton.R2 => pad.rightTrigger,
                PadButton.L3 => pad.leftStickButton,
                PadButton.R3 => pad.rightStickButton,
                PadButton.Up => pad.dpad.up,
                PadButton.Down => pad.dpad.down,
                PadButton.Left => pad.dpad.left,
                PadButton.Right => pad.dpad.right,
                PadButton.Options => pad.startButton,
                PadButton.Share => pad.selectButton,
                _ => null,
            };
        }

        // ------------------------------------------------------------------
        // 割り当ての取り込み（設定画面から使う）
        // ------------------------------------------------------------------

        private static readonly PadButton[] Assignable =
        {
            PadButton.Cross, PadButton.Circle, PadButton.Square, PadButton.Triangle,
            PadButton.L1, PadButton.R1, PadButton.L2, PadButton.R2,
            PadButton.L3, PadButton.R3,
            PadButton.Up, PadButton.Down, PadButton.Left, PadButton.Right,
            PadButton.Share,
            // OPTIONSは設定画面の開閉に使うので割り当て先から外す
        };

        /// <summary>今フレームに押されたパッドのボタン。無ければ<see cref="PadButton.None"/>。</summary>
        public static PadButton CapturePad()
        {
            Gamepad pad = Gamepad.current;
            if (pad == null) return PadButton.None;

            foreach (PadButton button in Assignable)
            {
                ButtonControl control = Control(pad, button);
                if (control != null && control.wasPressedThisFrame) return button;
            }

            return PadButton.None;
        }

        /// <summary>今フレームに押されたキー。Escは取り消しに使うので拾わない。</summary>
        public static Key CaptureKey()
        {
            Keyboard keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null) return Key.None;

            foreach (KeyControl control in keyboard.allKeys)
            {
                if (!control.wasPressedThisFrame) continue;
                if (control.keyCode == Key.Escape) continue;

                return control.keyCode;
            }

            return Key.None;
        }

        /// <summary>何も押されていないか。**取り込みを始める前に指が離れるのを待つ**ために見る。</summary>
        public static bool NothingPressed()
        {
            Gamepad pad = Gamepad.current;
            if (pad != null)
            {
                foreach (PadButton button in Assignable)
                {
                    ButtonControl control = Control(pad, button);
                    if (control != null && control.isPressed) return false;
                }
            }

            Keyboard keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard != null && keyboard.anyKey.isPressed) return false;

            return true;
        }

        // ------------------------------------------------------------------
        // 表示
        // ------------------------------------------------------------------

        /// <summary>ボタンの表示名。**パッドの系統で呼び方を変える**（×とA、○とB）。</summary>
        public static string PadLabel(PadButton button, bool playStation) => button switch
        {
            PadButton.None => "—",
            PadButton.Cross => playStation ? "×" : "A",
            PadButton.Circle => playStation ? "○" : "B",
            PadButton.Square => playStation ? "□" : "X",
            PadButton.Triangle => playStation ? "△" : "Y",
            PadButton.L1 => playStation ? "L1" : "LB",
            PadButton.R1 => playStation ? "R1" : "RB",
            PadButton.L2 => playStation ? "L2" : "LT",
            PadButton.R2 => playStation ? "R2" : "RT",
            PadButton.L3 => playStation ? "L3" : "LS",
            PadButton.R3 => playStation ? "R3" : "RS",
            PadButton.Up => "↑",
            PadButton.Down => "↓",
            PadButton.Left => "←",
            PadButton.Right => "→",
            PadButton.Options => playStation ? "OPTIONS" : "MENU",
            PadButton.Share => playStation ? "SHARE" : "VIEW",
            _ => button.ToString(),
        };

        public static string KeyLabel(Key key) => key switch
        {
            Key.None => "—",
            Key.LeftShift => "左Shift",
            Key.RightShift => "右Shift",
            Key.LeftCtrl => "左Ctrl",
            Key.RightCtrl => "右Ctrl",
            Key.LeftAlt => "左Alt",
            Key.RightAlt => "右Alt",
            Key.Space => "Space",
            Key.Enter => "Enter",
            Key.Tab => "Tab",
            Key.UpArrow => "↑",
            Key.DownArrow => "↓",
            Key.LeftArrow => "←",
            Key.RightArrow => "→",
            _ => key.ToString(),
        };
    }
}
