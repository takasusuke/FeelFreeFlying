using UnityEngine;

namespace FeelFreeFlying.Flight
{
    /// <summary>
    /// 飛行の操作（M1の本体 → `docs/m1-plan.md`）。
    ///
    /// **航空力学は再現しない**（`CLAUDE.md` 不変条件3）。失速・迎え角・エンジン出力を持たず、
    /// 姿勢角を直接積分する。落下も墜落も無い。速度の下限が0より上なので、**操作を止めても
    /// 機体は滑空し続ける**——これが「浮遊感」の土台になる。
    ///
    /// 唯一物理っぽく振る舞うのが<see cref="diveAcceleration"/>で、機首を下げると速度が乗り、
    /// 上げると落ちる。これが無いと、どの姿勢でも同じ速度で飛ぶ「レール感」が出る。
    ///
    /// 旋回はロールから作る（バンク旋回）。ヨーの直接入力を持たないのは、
    /// 「傾けて曲がる」ほうが操作が減って気持ちよさに集中できるため。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FlightController : MonoBehaviour
    {
        [Header("参照")]
        [SerializeField] private FlightInput input;

        [Header("速度 (m/s)")]
        [Tooltip("スロットルを絞りきった時の速度。**0にしない**。止まると浮遊感が消える")]
        [SerializeField, Min(1f)] private float speedMin = 18f;

        [SerializeField, Min(1f)] private float speedMax = 110f;

        [Tooltip("開始時および姿勢リセット時の速度")]
        [SerializeField, Min(1f)] private float speedStart = 45f;

        [Tooltip("スロットル操作に速度が追従する速さ (m/s^2)")]
        [SerializeField, Min(1f)] private float throttleAcceleration = 22f;

        [Tooltip("ブースト中の速度倍率")]
        [SerializeField, Range(1f, 3f)] private float boostMultiplier = 1.6f;

        [Header("姿勢")]
        [Tooltip("機首上下の角速度 (度/秒)")]
        [SerializeField, Min(1f)] private float pitchRate = 55f;

        [SerializeField, Range(10f, 89f)] private float pitchLimit = 65f;

        [Tooltip("ロールの角速度 (度/秒)")]
        [SerializeField, Min(1f)] private float rollRate = 120f;

        [SerializeField, Range(10f, 89f)] private float rollLimit = 70f;

        [Tooltip("バンク角いっぱいで曲がる速さ (度/秒)")]
        [SerializeField, Min(1f)] private float turnRate = 60f;

        [Tooltip("入力が無いとき水平に戻る速さ (度/秒)。0で戻らない")]
        [SerializeField, Range(0f, 120f)] private float autoLevelRate = 25f;

        [Tooltip("姿勢の追従の鈍さ (秒)。大きいほどふわっとするが、遅れて感じる")]
        [SerializeField, Range(0f, 0.5f)] private float attitudeSmoothing = 0.1f;

        [Header("浮遊感")]
        [Tooltip("機首を下げた時に乗る加速度 (m/s^2)。上げた時は同じだけ減速する。0で無効")]
        [SerializeField, Range(0f, 60f)] private float diveAcceleration = 18f;

        [Header("高度")]
        [Tooltip("これ以下に降りられない。**地面と衝突させない**（コライダーを持たないため）")]
        [SerializeField] private float altitudeMin = 12f;

        [SerializeField] private float altitudeMax = 2000f;

        private float pitchDegrees;
        private float rollDegrees;
        private float yawDegrees;
        private float speed;

        private Vector3 startPosition;
        private float startYaw;

        /// <summary>現在の速度 (m/s)。HUDとカメラが読む。</summary>
        public float Speed => speed;

        /// <summary>速度の範囲内での位置 (0〜1)。カメラの画角に使う。</summary>
        public float SpeedRatio => Mathf.InverseLerp(speedMin, speedMax, speed);

        public float PitchDegrees => pitchDegrees;
        public float RollDegrees => rollDegrees;
        public bool IsBoosting { get; private set; }

        private bool viewToggleRequested;

        /// <summary>
        /// 視点切替が押されたか。**押されていたらtrueを返して同時に消費する。**
        /// 入力は<see cref="FlightController"/>が1フレームに1回だけ読む（マウスのデルタを
        /// 二重に積まないため）ので、カメラ側はここ経由で受け取る。
        /// </summary>
        public bool ConsumeViewToggle()
        {
            if (!viewToggleRequested) return false;
            viewToggleRequested = false;
            return true;
        }

        private void Awake()
        {
            if (input == null) input = GetComponent<FlightInput>();

            startPosition = transform.position;
            startYaw = transform.eulerAngles.y;
            ResetPose();
        }

        private void Update()
        {
            FlightInputState state = input != null ? input.Read() : default;
            float dt = Time.deltaTime;

            if (state.ToggleView) viewToggleRequested = true;

            if (state.Reset)
            {
                ResetPose();
                return;
            }

            UpdateAttitude(state, dt);
            UpdateSpeed(state, dt);
            Move(dt);
        }

        private void UpdateAttitude(FlightInputState state, float dt)
        {
            pitchDegrees += state.Pitch * pitchRate * dt;
            rollDegrees += state.Roll * rollRate * dt;

            if (state.Level)
            {
                // 明示的な水平戻しは、自動より速くないと「効いた感じ」がしない
                pitchDegrees = Mathf.MoveTowards(pitchDegrees, 0f, pitchRate * 2f * dt);
                rollDegrees = Mathf.MoveTowards(rollDegrees, 0f, rollRate * 2f * dt);
            }
            else if (autoLevelRate > 0f)
            {
                // 入力が無い軸だけ戻す。入力中に戻すと操作と喧嘩する
                if (Mathf.Approximately(state.Roll, 0f))
                {
                    rollDegrees = Mathf.MoveTowards(rollDegrees, 0f, autoLevelRate * dt);
                }
                if (Mathf.Approximately(state.Pitch, 0f))
                {
                    pitchDegrees = Mathf.MoveTowards(pitchDegrees, 0f, autoLevelRate * 0.5f * dt);
                }
            }

            pitchDegrees = Mathf.Clamp(pitchDegrees, -pitchLimit, pitchLimit);
            rollDegrees = Mathf.Clamp(rollDegrees, -rollLimit, rollLimit);

            // バンク旋回。傾けた分だけ機首が回る
            yawDegrees += Mathf.Sin(rollDegrees * Mathf.Deg2Rad) * turnRate * dt;

            var target = Quaternion.Euler(-pitchDegrees, yawDegrees, -rollDegrees);
            transform.rotation = attitudeSmoothing > 0f
                ? Quaternion.Slerp(transform.rotation, target, 1f - Mathf.Exp(-dt / attitudeSmoothing))
                : target;
        }

        private void UpdateSpeed(FlightInputState state, float dt)
        {
            IsBoosting = state.Boost;

            float throttle01 = Mathf.InverseLerp(-1f, 1f, state.Throttle);
            float targetSpeed = Mathf.Lerp(speedMin, speedMax, throttle01);
            if (IsBoosting) targetSpeed = Mathf.Min(targetSpeed * boostMultiplier, speedMax * boostMultiplier);

            speed = Mathf.MoveTowards(speed, targetSpeed, throttleAcceleration * dt);

            // 降下で速度が乗り、上昇で削がれる。位置エネルギーの交換のつもりで、力学ではない
            speed += Mathf.Sin(-pitchDegrees * Mathf.Deg2Rad) * diveAcceleration * dt;
            speed = Mathf.Clamp(speed, speedMin * 0.5f, speedMax * boostMultiplier);
        }

        private void Move(float dt)
        {
            Vector3 position = transform.position + transform.forward * (speed * dt);

            // 地面にも建物にもコライダーが無いので、下限で受け止める（→ M2でどうするか決める）
            if (position.y < altitudeMin)
            {
                position.y = altitudeMin;
                if (pitchDegrees < 0f) pitchDegrees = Mathf.MoveTowards(pitchDegrees, 0f, 90f * dt);
            }
            position.y = Mathf.Min(position.y, altitudeMax);

            transform.position = position;
        }

        /// <summary>開始地点・水平姿勢・初期速度に戻す。</summary>
        public void ResetPose()
        {
            pitchDegrees = 0f;
            rollDegrees = 0f;
            yawDegrees = startYaw;
            speed = speedStart;

            transform.SetPositionAndRotation(startPosition, Quaternion.Euler(0f, startYaw, 0f));
        }
    }
}
