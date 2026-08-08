using UnityEngine;
using UnityEngine.InputSystem;

namespace FeelFreeFlying.Flight
{
    /// <summary>1フレーム分の操作入力。値はすべて正規化済み。</summary>
    public struct FlightInputState
    {
        /// <summary>機首の上下。+1で上げ、-1で下げ。</summary>
        public float Pitch;

        /// <summary>ロール。+1で右へ傾ける（＝右旋回）。</summary>
        public float Roll;

        /// <summary>速度の増減。+1で加速、-1で減速。</summary>
        public float Throttle;

        /// <summary>押している間だけ加速。</summary>
        public bool Boost;

        /// <summary>姿勢を水平に戻す。</summary>
        public bool Level;

        /// <summary>開始地点に戻す。</summary>
        public bool Reset;
    }

    /// <summary>
    /// 入力の読み取り。**Input Systemのデバイスを直接見る**。
    ///
    /// .inputactions アセットを使わないのは、M1で試したいのが「操作方式そのもの」だからで、
    /// キー割り当てを変えるたびにアセットを開いて編集するより、ここを直接書き換えるほうが速い。
    /// 操作方式が固まったら（→ `requirements.md` §11）アクションアセットへ移す。
    ///
    /// マウスは**画面中央からの仮想スティック**として扱う。生のデルタを角速度に流すと、
    /// 手を止めた瞬間に機体も止まって「浮いている」感じが消えるため。
    /// </summary>
    public sealed class FlightInput : MonoBehaviour
    {
        [Header("マウス")]
        [Tooltip("仮想スティックが端まで振り切れるまでのマウス移動量（px）")]
        [SerializeField, Min(1f)] private float mouseRange = 400f;

        [Tooltip("入力を止めたときに中央へ戻る速さ（1秒あたりの割合）。0で戻らない")]
        [SerializeField, Range(0f, 5f)] private float mouseSelfCentering = 0.6f;

        [SerializeField] private bool invertMousePitch = false;

        [Header("ゲームパッド")]
        [Tooltip("スティックの遊び")]
        [SerializeField, Range(0f, 0.5f)] private float stickDeadZone = 0.15f;

        [SerializeField] private bool invertStickPitch = false;

        [Header("カーソル")]
        [Tooltip("マウスで操作するので既定で隠す。**Escで必ず解放できるようにしておくこと**")]
        [SerializeField] private bool lockCursor = true;

        private Vector2 virtualStick;
        private bool cursorCaptured;

        /// <summary>カーソルを掴んでいるか。falseの間はマウスで操縦しない。</summary>
        public bool CursorCaptured => cursorCaptured;

        /// <summary>マウス由来の仮想スティック位置（-1〜1）。HUDの表示に使う。</summary>
        public Vector2 VirtualStick => virtualStick;

        /// <summary>直近に操作されたのがゲームパッドか。操作ヒントの出し分けに使う。</summary>
        public bool UsingGamepad { get; private set; }

        private void OnEnable()
        {
            SetCursorCaptured(lockCursor);
        }

        private void OnDisable()
        {
            SetCursorCaptured(false);
        }

        /// <summary>
        /// Escでカーソルを返し、画面をクリックで掴み直す。
        ///
        /// **解放手段の無いカーソルロックを作らない。** ウィンドウ表示のビルドで閉じ込めると、
        /// 他のアプリを操作できなくなり、ゲームを終了させることすらできない。
        /// </summary>
        private void UpdateCursorCapture()
        {
            if (!lockCursor) return;

            Keyboard keyboard = Keyboard.current;
            if (cursorCaptured && keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            {
                SetCursorCaptured(false);
                return;
            }

            Mouse mouse = Mouse.current;
            if (!cursorCaptured && mouse != null && mouse.leftButton.wasPressedThisFrame)
            {
                SetCursorCaptured(true);
            }
        }

        private void SetCursorCaptured(bool captured)
        {
            cursorCaptured = captured;
            Cursor.lockState = captured ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !captured;
        }

        public FlightInputState Read()
        {
            var state = new FlightInputState();

            UpdateCursorCapture();
            ReadGamepad(ref state);
            ReadKeyboardAndMouse(ref state);

            state.Pitch = Mathf.Clamp(state.Pitch, -1f, 1f);
            state.Roll = Mathf.Clamp(state.Roll, -1f, 1f);
            state.Throttle = Mathf.Clamp(state.Throttle, -1f, 1f);
            return state;
        }

        private void ReadGamepad(ref FlightInputState state)
        {
            Gamepad pad = Gamepad.current;
            if (pad == null) return;

            Vector2 stick = ApplyDeadZone(pad.leftStick.ReadValue());
            if (stick.sqrMagnitude > 0f) UsingGamepad = true;

            state.Pitch += invertStickPitch ? stick.y : -stick.y;
            state.Roll += stick.x;

            float throttle = pad.rightTrigger.ReadValue() - pad.leftTrigger.ReadValue();
            if (Mathf.Abs(throttle) > 0.01f) UsingGamepad = true;
            state.Throttle += throttle;

            if (pad.buttonSouth.isPressed) { state.Boost = true; UsingGamepad = true; }
            if (pad.buttonEast.isPressed) { state.Level = true; UsingGamepad = true; }
            if (pad.selectButton.wasPressedThisFrame) { state.Reset = true; UsingGamepad = true; }
        }

        private void ReadKeyboardAndMouse(ref FlightInputState state)
        {
            // カーソルを返している間はマウスで操縦しない（他のアプリを触っている最中に機体が動かないように）
            Mouse mouse = lockCursor && !cursorCaptured ? null : Mouse.current;
            if (mouse != null)
            {
                Vector2 delta = mouse.delta.ReadValue();
                if (delta.sqrMagnitude > 0f) UsingGamepad = false;

                virtualStick += delta / mouseRange;

                if (mouseSelfCentering > 0f)
                {
                    virtualStick = Vector2.MoveTowards(
                        virtualStick, Vector2.zero, mouseSelfCentering * Time.deltaTime);
                }

                virtualStick = Vector2.ClampMagnitude(virtualStick, 1f);

                state.Pitch += invertMousePitch ? virtualStick.y : -virtualStick.y;
                state.Roll += virtualStick.x;
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.wKey.isPressed) { state.Throttle += 1f; UsingGamepad = false; }
            if (keyboard.sKey.isPressed) { state.Throttle -= 1f; UsingGamepad = false; }
            if (keyboard.aKey.isPressed) { state.Roll -= 1f; UsingGamepad = false; }
            if (keyboard.dKey.isPressed) { state.Roll += 1f; UsingGamepad = false; }
            if (keyboard.upArrowKey.isPressed) { state.Pitch += 1f; UsingGamepad = false; }
            if (keyboard.downArrowKey.isPressed) { state.Pitch -= 1f; UsingGamepad = false; }
            if (keyboard.leftShiftKey.isPressed) { state.Boost = true; UsingGamepad = false; }

            if (keyboard.spaceKey.isPressed)
            {
                state.Level = true;
                virtualStick = Vector2.zero; // 水平に戻す時はマウスの蓄積も消す
                UsingGamepad = false;
            }

            if (keyboard.rKey.wasPressedThisFrame)
            {
                state.Reset = true;
                virtualStick = Vector2.zero;
                UsingGamepad = false;
            }
        }

        private Vector2 ApplyDeadZone(Vector2 stick)
        {
            float magnitude = stick.magnitude;
            if (magnitude < stickDeadZone) return Vector2.zero;

            // 遊びの外側を0〜1に引き伸ばす。切り捨てるだけだと中心付近で入力が飛ぶ
            return stick / magnitude * Mathf.InverseLerp(stickDeadZone, 1f, magnitude);
        }
    }
}
